using System.Globalization;
using System.Text;
using System.Text.Json;
using Alp.Api.Common;
using Alp.Api.Http;
using Alp.Api.Projects;
using Alp.Data;
using Alp.Domain;
using Alp.Reports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Alp.Api.Reports;

public static class ReportEndpoints
{
    // §4.4: "Rapor yükü boyut sınırı (varsayılan 5 MB); SVG dizeleri şişebilir."
    private const long ReportBodyLimitBytes = 5 * 1024 * 1024;

    // ---- Saklama politikası: üretilen belge diske YAZILMAZ ----
    //
    // Karar (2026-07-30, kullanıcı): rapor dosyası sunucuda tutulmaz. Gerekçe
    // ve alternatifler docs/kod-incelemesi-2026-07-29.md "Üretilen rapor
    // dosyalarında saklama sınırı yok" maddesinde: dosya tutan her seçenek
    // temizlik görevi ya da kota gerektiriyordu (tek kullanıcı hız sınırının
    // izin verdiği tempoda günde ~290 MB üretebiliyor).
    //
    // Rapor türetilmiş veridir: kaynağı kaydedilmiş hesapların `ReportJson`
    // bölümleridir ve onlar veritabanında zaten duruyor. Bu yüzden "tekrar
    // indir" bir dosya kopyası değil, kayıttan YENİDEN ÜRETİMDİR (bkz.
    // Download). `Reports` tablosu kütük olarak kalır: hangi rapor, kim,
    // ne zaman, kaç bayt.
    //
    // Bunun kabul edilen sınırı: projeye kaydedilmemiş tek seferlik bir rapor
    // geri getirilemez — o ekranın verisi hiçbir yerde durmuyor.
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").RequireAuthorization();

        group.MapPost("/pdf", GeneratePdf).RequireRateLimiting("reports").LimitBodySize(ReportBodyLimitBytes);
        group.MapPost("/xlsx", GenerateXlsx).RequireRateLimiting("reports").LimitBodySize(ReportBodyLimitBytes);
        group.MapGet("/", ListReports);
        // İndirme artık dizgiyi yeniden koşuyor, diskten okumuyor: üretim
        // uçlarıyla aynı kovaya girer, yoksa ücretsiz bir CPU musluğu olurdu.
        group.MapGet("/{id:guid}/download", Download).RequireRateLimiting("reports");

        // ---- Proje raporu ----
        //
        // Yukarıdaki iki üretim ucu yükü İSTEMCİDEN alır (araç ekranı canlı
        // SVG'yi o an yakalar). Proje raporunda böyle bir canlı ekran yok:
        // bölümler zaten kayıtlı. Proje detayı artık `ReportJson` göndermediği
        // için istemci onları toplayamaz da — yük burada sunucuda kurulur ve
        // gövde yalnız belgenin künyesini taşır.
        //
        // Rota projenin altında ama kod rapor tarafında duruyor: dizgi, hata
        // kodları ve kütük kaydı burada tek yerde.
        var projectReports = app.MapGroup("/api/projects/{id:guid}/report").RequireAuthorization();

        projectReports.MapPost("/pdf", GenerateProjectPdf)
            .RequireRateLimiting("reports").LimitBodySize(ProjectReportBodyLimitBytes);
        projectReports.MapPost("/xlsx", GenerateProjectXlsx)
            .RequireRateLimiting("reports").LimitBodySize(ProjectReportBodyLimitBytes);
    }

    // Gövdede yalnız başlık, hazırlayan ve tarih var — 8 KB fazlasıyla yeter.
    private const long ProjectReportBodyLimitBytes = 8 * 1024;

    private static async Task<IResult> GenerateProjectPdf(
        Guid id, ProjectReportRequest req, AppDbContext db, HttpContext http, PdfReportBuilder builder)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        return await GenerateProjectReport(id, req, db, http, ReportFormat.Pdf, builder.Build);
    }

    private static Task<IResult> GenerateProjectXlsx(
        Guid id, ProjectReportRequest req, AppDbContext db, HttpContext http, XlsxReportBuilder builder) =>
        GenerateProjectReport(id, req, db, http, ReportFormat.Xlsx, builder.Build);

    private static async Task<IResult> GenerateProjectReport(
        Guid id, ProjectReportRequest req, AppDbContext db, HttpContext http,
        ReportFormat format, Func<ReportPayload, byte[]> build)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new ApiError("MISSING_FIELDS", new { field = "title" }));
        if (string.IsNullOrWhiteSpace(req.PreparedBy)) return Results.BadRequest(new ApiError("MISSING_FIELDS", new { field = "preparedBy" }));
        if (string.IsNullOrWhiteSpace(req.Date)) return Results.BadRequest(new ApiError("MISSING_FIELDS", new { field = "date" }));

        // Var olmayan ve başkasına ait proje AYNI 404'ü verir.
        var projectName = await OwnedProjectName(db, id, userId);
        if (projectName is null) return Results.NotFound(new ApiError("PROJECT_NOT_FOUND"));

        var payload = await ProjectPayload(db, id, userId, req.Title.Trim(), req.PreparedBy.Trim(), req.Date);
        if (payload is null)
        {
            // Projede hiç kayıtlı rapor bölümü yok — indirmenin üretecek verisi
            // yok. Geçmişten indirmedeki durumla aynı, kod da aynı.
            return Results.Conflict(new ApiError("REPORT_NOT_REPRODUCIBLE", new { reason = "no-sections" }));
        }

        byte[] bytes;
        try
        {
            bytes = build(payload);
        }
        catch (ReportLayoutException)
        {
            return Results.UnprocessableEntity(new ApiError("REPORT_TOO_LARGE"));
        }

        var isPdf = format == ReportFormat.Pdf;
        var record = await LogReport(db, userId, payload, format, bytes, id);

        return Results.File(
            bytes,
            isPdf ? PdfContentType : XlsxContentType,
            DownloadName(payload, projectName, record, isPdf ? "pdf" : "xlsx"));
    }

    private static async Task<IResult> GeneratePdf(
        ReportPayload payload,
        [FromQuery] Guid? projectId,
        PdfReportBuilder builder,
        AppDbContext db,
        HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var invalid = Validate(payload);
        if (invalid is not null) return Results.BadRequest(invalid);

        string? projectName = null;
        if (projectId is not null)
        {
            projectName = await OwnedProjectName(db, projectId.Value, userId);
            if (projectName is null) return Results.NotFound(new ApiError("PROJECT_NOT_FOUND"));
        }

        byte[] bytes;
        try
        {
            bytes = builder.Build(payload);
        }
        catch (ReportLayoutException)
        {
            // Yük geçerli ama içerik sayfa düzenine sığmıyor (çok bölümlü,
            // grafikli proje raporu). Bu bir sunucu arızası değil, girdinin
            // sınırı — 500 değil 422 döner ve arayüz bunu okunur bir cümleye
            // çevirir. Excel aynı yükü kaldırdığı için kullanıcıya kalan yol
            // Excel indirmektir.
            return Results.UnprocessableEntity(new ApiError("REPORT_TOO_LARGE"));
        }

        var record = await LogReport(db, userId, payload, ReportFormat.Pdf, bytes, projectId);

        return Results.File(bytes, PdfContentType, DownloadName(payload, projectName, record, "pdf"));
    }

    private static async Task<IResult> GenerateXlsx(
        ReportPayload payload,
        [FromQuery] Guid? projectId,
        XlsxReportBuilder builder,
        AppDbContext db,
        HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var invalid = Validate(payload);
        if (invalid is not null) return Results.BadRequest(invalid);

        string? projectName = null;
        if (projectId is not null)
        {
            projectName = await OwnedProjectName(db, projectId.Value, userId);
            if (projectName is null) return Results.NotFound(new ApiError("PROJECT_NOT_FOUND"));
        }

        var bytes = builder.Build(payload);
        var record = await LogReport(db, userId, payload, ReportFormat.Xlsx, bytes, projectId);

        return Results.File(bytes, XlsxContentType, DownloadName(payload, projectName, record, "xlsx"));
    }

    // Var olmayan ve başkasına ait proje AYNI 404'ü verir — anti-enumeration
    // kuralı burada da geçerli (bkz. Download).
    //
    // Sahiplik kontrolü ile ad okuma tek sorguda yapılır: ad dosya adında
    // kullanılıyor ve ayrı bir `AnyAsync` + `Select` çifti aynı satırı iki kez
    // okurdu. `null` "yok ya da senin değil" demektir.
    private static Task<string?> OwnedProjectName(AppDbContext db, Guid projectId, string userId) =>
        db.Projects
            .Where(p => p.Id == projectId && p.UserId == userId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync();

    private static async Task<IResult> ListReports(AppDbContext db, HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var reports = await db.Reports
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.GeneratedAt)
            .Select(r => new ReportSummary(r.Id, r.Title, r.PreparedBy, r.Format, r.FileSize, r.GeneratedAt))
            .ToListAsync();

        return Results.Ok(reports);
    }

    // ---- Geçmişten indirme = kayıttan yeniden üretim ----
    //
    // Dosya saklanmadığı için (bkz. MapReportEndpoints üstündeki not) burada
    // diskten okunacak bir şey yok: rapor, projedeki hesapların kaydedilmiş
    // `ReportJson` bölümlerinden yeniden dizilir. Bölümler istemcinin gönderdiği
    // hâlleriyle (SVG dahil) duruyor, yani belge içerik olarak aynı çıkar.
    //
    // Bilinçli iki fark:
    //   - Belgenin tarihi ilk üretimin günüdür (`GeneratedAt`), yeniden basma
    //     günü değil. İndirilen belge "o gün alınmış rapor" olarak okunur.
    //   - Proje o günden beri değiştiyse rapor GÜNCEL hâli gösterir; anlık
    //     görüntü saklanmıyor. Değişmemiş bir projede sonuç birebir aynıdır.
    private static async Task<IResult> Download(
        Guid id,
        AppDbContext db,
        HttpContext http,
        PdfReportBuilder pdf,
        XlsxReportBuilder xlsx)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var report = await db.Reports
            .Where(r => r.Id == id)
            .Select(r => new
            {
                r.UserId,
                r.ProjectId,
                r.Title,
                r.PreparedBy,
                r.Format,
                r.GeneratedAt,
                // Proje silinmişse FK `SetNull`'a düşer, yani ProjectId de null olur.
                ProjectName = r.Project == null ? null : r.Project.Name,
            })
            .FirstOrDefaultAsync();

        // Var olmayan ve başkasına ait rapor AYNI yanıtı verir — hangisi
        // olduğunu dışarı sızdırmaz.
        if (report is null || report.UserId != userId) return Results.NotFound();

        // Projesiz (tek araçtan alınmış) rapor ile projesi sonradan silinmiş
        // rapor aynı yere düşer: yeniden üretecek kaynak veri yok. 404 DEĞİL —
        // kayıt duruyor ve kullanıcının onu görmesi doğru; eksik olan kaynak.
        if (report.ProjectId is null)
        {
            return Results.Conflict(new ApiError("REPORT_NOT_REPRODUCIBLE", new { reason = "no-project" }));
        }

        var payload = await ProjectPayload(
            db, report.ProjectId.Value, userId, report.Title, report.PreparedBy,
            report.GeneratedAt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));

        if (payload is null)
        {
            return Results.Conflict(new ApiError("REPORT_NOT_REPRODUCIBLE", new { reason = "no-sections" }));
        }

        var isPdf = report.Format == ReportFormat.Pdf;
        byte[] bytes;
        try
        {
            bytes = isPdf ? pdf.Build(payload) : xlsx.Build(payload);
        }
        catch (ReportLayoutException)
        {
            return Results.UnprocessableEntity(new ApiError("REPORT_TOO_LARGE"));
        }

        var contentType = isPdf ? PdfContentType : XlsxContentType;
        var ext = isPdf ? "pdf" : "xlsx";

        // Ad, üretim yolundaki kuralın proje dalıyla aynı: projeyi ayırt eden
        // şey adıdır, `Title` bütün raporlarda aynı sabittir ("DONANIM RAPORU").
        var basis = string.IsNullOrWhiteSpace(report.ProjectName) ? report.Title : report.ProjectName;
        var name = $"{Slugify(basis)}-{IsoDate(report.GeneratedAt)}.{ext}";
        return Results.File(bytes, contentType, name);
    }

    // Projedeki kaydedilmiş hesaplardan rapor yükünü kurar. `null` dönmesi
    // "yeniden üretecek okunabilir bölüm yok" demektir.
    //
    // İki yol da buradan geçer: geçmişten indirme (Download) ve proje ekranının
    // rapor düğmesi (GenerateProjectReport). İkisi ayrı ayrı yazılsaydı,
    // bölümlerin sırası ya da bozuk kaydın atlanması gibi kurallar zamanla
    // ayrışır ve aynı proje iki yoldan farklı belge verirdi.
    private static async Task<ReportPayload?> ProjectPayload(
        AppDbContext db, Guid projectId, string userId, string title, string preparedBy, string date)
    {
        var stored = await db.Calculations
            .Where(c => c.ProjectId == projectId && c.ReportJson != null)
            .OrderBy(c => c.SortOrder)
            .Select(c => new { c.ReportJson, c.SchemaVersion })
            .ToListAsync();

        var sections = new List<ReportSection>();
        var schemaVersion = 0;
        foreach (var row in stored)
        {
            var section = TryReadSection(row.ReportJson!);
            // Bozuk/eski bir bölüm sessizce atlanır — bir hesabın kaydı
            // diğerlerinin raporunu engellemez.
            if (section is null) continue;
            sections.Add(section);
            if (schemaVersion == 0) schemaVersion = row.SchemaVersion;
        }

        if (sections.Count == 0) return null;

        var company = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.Company)
            .FirstOrDefaultAsync();

        return new ReportPayload(schemaVersion, title, preparedBy, company, date, sections);
    }

    // Kaydedilmiş bölüm istemcinin ürettiği camelCase JSON'dur; `JsonSerializer`
    // web varsayılanlarıyla okunur (uçların gövde ayrıştırmasıyla aynı kural).
    // Bozuk JSON istisna fırlatmaz, `null` döner: tek bozuk kayıt bütün raporu
    // düşürmemeli.
    private static ReportSection? TryReadSection(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ReportSection>(json, SectionJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions SectionJsonOptions = new(JsonSerializerDefaults.Web);

    private const string PdfContentType = "application/pdf";

    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static ApiError? Validate(ReportPayload payload)
    {
        if (payload.Sections.Count == 0) return new ApiError("EMPTY_PAYLOAD");
        if (string.IsNullOrWhiteSpace(payload.Title)) return new ApiError("MISSING_FIELDS", new { field = "title" });
        if (string.IsNullOrWhiteSpace(payload.PreparedBy)) return new ApiError("MISSING_FIELDS", new { field = "preparedBy" });
        return null;
    }

    // Belge diske yazılmaz, yalnızca kütüğe geçer. `FileSize` üretilen belgenin
    // boyutudur ve saklanmasının nedeni ölçüm/kütük: kullanıcı ne kadar rapor
    // üretti, hangi boyutta — bir dosyayı bulmak için değil.
    private static async Task<Report> LogReport(
        AppDbContext db, string userId,
        ReportPayload payload, ReportFormat format, byte[] bytes, Guid? projectId = null)
    {
        var report = new Report
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            UserId = userId,
            Title = payload.Title,
            PreparedBy = payload.PreparedBy,
            Revision = 1,
            Format = format,
            FileSize = bytes.LongLength,
            GeneratedAt = DateTimeOffset.UtcNow,
        };
        db.Reports.Add(report);
        await db.SaveChangesAsync();
        return report;
    }

    private static string? CurrentUserId(HttpContext http) =>
        http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;

    // ---- İndirilen dosyanın adı ----
    //
    // Eskiden ham GUID basılıyordu ("6545be68-….pdf"). İndirilen dosyanın hangi
    // rapor olduğu kayboluyordu; kullanıcı indirmenin gerçekleştiğini bile fark
    // etmiyordu.
    //
    // Ad için ÜÇ kaynak sırayla denenir:
    //   1. Proje adı — proje raporunda tek bir araç yoktur, ayırt eden projedir.
    //   2. Tek bölümlü raporda araç adı. Başlık KULLANILMAZ: `payload.Title`
    //      bütün araçlarda aynı sabittir ("DONANIM RAPORU"), ondan türetilen ad
    //      hiçbir aracı ayırt etmezdi.
    //   3. Çok bölümlü ve projesiz raporda ayırt edecek tek ad başlıktır.
    private static string DownloadName(ReportPayload payload, string? projectName, Report record, string ext)
    {
        var basis = !string.IsNullOrWhiteSpace(projectName) ? projectName
            : payload.Sections.Count == 1 ? payload.Sections[0].ToolName
            : payload.Title;

        return $"{Slugify(basis)}-{FileDate(payload.Date, record.GeneratedAt)}.{ext}";
    }

    // Belgenin İÇİNE yazılan tarih tarayıcıdan gelir (kullanıcının yerel günü,
    // `reportDateStamp()` → dd.MM.yyyy). Dosya adı da onu kullanır: doğrudan
    // sunucunun UTC saatine düşülürse gece yarısı ile 03:00 arasında dosya adı
    // ile belgenin üstündeki tarih farklı gün gösterirdi. Ayrıştırılamayan bir
    // değer geldiğinde kayıt zamanına düşülür — ad her hâlükârda üretilir.
    private static string FileDate(string payloadDate, DateTimeOffset fallback) =>
        DateTime.TryParseExact(payloadDate, "dd.MM.yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed)
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : IsoDate(fallback);

    // ISO sıralanabilir: dosya yöneticisinde ada göre sıralama tarihe göre
    // sıralama demek olur.
    private static string IsoDate(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Türkçe harfler ASCII karşılığına katlanır, kalan ASCII olmayan her şey
    // ayraca döner. İki gerekçe:
    //   - `ToLowerInvariant` 'İ' harfini "i + birleşen nokta" (U+0307) olarak
    //     üretir; dosya adında bozuk görünür ve bazı sistemlerde eşleşmez.
    //   - ASCII ad e-postayla gönderildiğinde ya da Windows'a taşındığında
    //     sorun çıkarmaz.
    private static readonly Dictionary<char, char> AsciiFold = new()
    {
        ['ç'] = 'c', ['Ç'] = 'c',
        ['ğ'] = 'g', ['Ğ'] = 'g',
        ['ı'] = 'i', ['İ'] = 'i',
        ['ö'] = 'o', ['Ö'] = 'o',
        ['ş'] = 's', ['Ş'] = 's',
        ['ü'] = 'u', ['Ü'] = 'u',
    };

    // Dosya sistemi sınırlarına (255 bayt) değil okunurluğa göre: uzun ad
    // dosya yöneticisinde kırpılarak görünür, ayırt etmeyi yine zorlaştırır.
    private const int SlugMaxLength = 60;

    private static string Slugify(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (var ch in title)
        {
            if (AsciiFold.TryGetValue(ch, out var folded)) sb.Append(folded);
            else if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (ch is >= 'A' and <= 'Z') sb.Append((char)(ch + 32));
            else sb.Append('-');
        }

        var cleaned = sb.ToString();
        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
        cleaned = cleaned.Trim('-');
        if (cleaned.Length > SlugMaxLength) cleaned = cleaned[..SlugMaxLength].TrimEnd('-');

        return cleaned.Length > 0 ? cleaned : "rapor";
    }
}

public record ReportSummary(Guid Id, string Title, string PreparedBy, ReportFormat Format, long FileSize, DateTimeOffset GeneratedAt);
