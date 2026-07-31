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
        // GET DEĞİL POST: indirme kayıttan YENİDEN ÜRETİMDİR ve üretim artık
        // belgenin çerçeve metnini istemciden alıyor (§5.1: sunucuda kullanıcı
        // metni yok). Etiketler gövdeyle gelir; sorgu dizesine sığdırmak ya da
        // sunucuya ikinci bir sözlük koymak iki kötü seçenekti.
        group.MapPost("/{id:guid}/download", Download)
            .RequireRateLimiting("reports").LimitBodySize(ProjectReportBodyLimitBytes);

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

    // Proje raporunun kaynak bütçesi. Gövde sınırı burada işe yaramaz: yük
    // istemciden değil veritabanından kurulur ve hesap başına 2 MB'a dek
    // ReportJson birikebilir. Sınırsız bırakılırsa büyük bir proje, belge
    // dizgisi başlamadan bütün bölümleri belleğe çeker — PDF ancak bellek
    // harcandıktan sonra ReportLayoutException'la 422'ye düşüyordu, XLSX hiç
    // düşmüyordu (500). Bütçe aşılırsa hiçbir satır BELLEĞE OKUNMADAN
    // REPORT_TOO_LARGE döner; tek araçlık rapor uçlarının 5 MB gövde sınırıyla
    // aynı mertebede, çok bölümlü olduğu için biraz üstünde.
    private const long ProjectReportSourceBudgetChars = 8 * 1024 * 1024;

    // Kimlik denetimi delegede (GenerateProjectReport) — burada tekrarı
    // ölü koddu, XLSX kardeşi de zaten taşımıyor.
    private static Task<IResult> GenerateProjectPdf(
        Guid id, ProjectReportRequest req, AppDbContext db, HttpContext http, PdfReportBuilder builder) =>
        GenerateProjectReport(id, req, db, http, ReportFormat.Pdf, builder.Build);

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
        if (req.Title.Trim().Length > Report.TitleMaxLength)
        {
            return Results.BadRequest(new ApiError("TOO_LONG", new { field = "title", max = Report.TitleMaxLength }));
        }
        if (req.PreparedBy.Trim().Length > Report.PreparedByMaxLength)
        {
            return Results.BadRequest(new ApiError("TOO_LONG", new { field = "preparedBy", max = Report.PreparedByMaxLength }));
        }
        // Firma kütüğe yazılmaz ama belgeye girer: sınır profil ucundakiyle
        // AYNI kaynaktan okunur, yoksa profile sığmayan bir ad rapor yoluyla
        // belgeye kaçardı.
        if (req.Company is not null && req.Company.Trim().Length > ApplicationUser.CompanyMaxLength)
        {
            return Results.BadRequest(
                new ApiError("TOO_LONG", new { field = "company", max = ApplicationUser.CompanyMaxLength }));
        }

        // Var olmayan ve başkasına ait proje AYNI 404'ü verir.
        var projectName = await OwnedProjectName(db, id, userId);
        if (projectName is null) return Results.NotFound(new ApiError("PROJECT_NOT_FOUND"));

        var (payload, tooLarge) = await ProjectPayload(
            db, id, userId, req.Title.Trim(), req.PreparedBy.Trim(), req.Company?.Trim(),
            req.Date, req.Labels, req.Lang ?? "tr");
        if (tooLarge) return Results.UnprocessableEntity(new ApiError("REPORT_TOO_LARGE"));
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

        byte[] bytes;
        try
        {
            bytes = builder.Build(payload);
        }
        catch (ReportLayoutException)
        {
            // PDF dalıyla simetri. ClosedXML bugün bu istisnayı üretmiyor ama
            // XlsxReportBuilder ileride kendi sınırını koyarsa buradaki yanıt
            // sözleşmesi hazır olsun — 500 değil 422.
            return Results.UnprocessableEntity(new ApiError("REPORT_TOO_LARGE"));
        }
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
        ReportLabelsRequest req,
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

        // Firma `null` geçilir, yani PROFİLDEKİ değer okunur. Kütükte firma
        // saklanmıyor (Report yalnız başlık ve hazırlayanı taşır), dolayısıyla
        // o günkü tek seferlik düzenleme geri getirilemez — uydurmak yerine
        // bugünkü profil yazılır. Geçmişten indirme zaten yeniden ÜRETİMDİR.
        var (payload, tooLarge) = await ProjectPayload(
            db, report.ProjectId.Value, userId, report.Title, report.PreparedBy, null,
            report.GeneratedAt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), req.Labels, req.Lang ?? "tr");

        if (tooLarge) return Results.UnprocessableEntity(new ApiError("REPORT_TOO_LARGE"));
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
        var name = $"{Slugify(basis)}-{IsoDate(report.GeneratedAt)}{LangSuffix(req.Lang)}.{ext}";
        return Results.File(bytes, contentType, name);
    }

    // Projedeki kaydedilmiş hesaplardan rapor yükünü kurar. `null` dönmesi
    // "yeniden üretecek okunabilir bölüm yok" demektir.
    //
    // İki yol da buradan geçer: geçmişten indirme (Download) ve proje ekranının
    // rapor düğmesi (GenerateProjectReport). İkisi ayrı ayrı yazılsaydı,
    // bölümlerin sırası ya da bozuk kaydın atlanması gibi kurallar zamanla
    // ayrışır ve aynı proje iki yoldan farklı belge verirdi.
    // `company`: `null` ise profildeki firma okunur, verilmişse (boş dize dahil)
    // olduğu gibi kullanılır — üç durumun anlamı ProjectReportRequest'te yazılı.
    // `internal`: künye kuralları (özellikle firmanın üç durumu) doğrudan
    // sınanıyor — bkz. Alp.Api.Tests/ProjectReportCompanyTests.cs.
    internal static async Task<(ReportPayload? Payload, bool TooLarge)> ProjectPayload(
        AppDbContext db, Guid projectId, string userId, string title, string preparedBy,
        string? company, string date, ReportLabels labels, string lang)
    {
        // Bütçe kontrolü satırlar belleğe okunmadan, tek skaler sorguyla
        // yapılır — aşan projede bölümlerin kendisi hiç taşınmaz.
        var totalChars = await db.Calculations
            .Where(c => c.ProjectId == projectId && c.ReportJson != null)
            .SumAsync(c => (long)c.ReportJson!.Length);
        if (totalChars > ProjectReportSourceBudgetChars) return (null, true);

        var stored = await db.Calculations
            .Where(c => c.ProjectId == projectId && c.ReportJson != null)
            .OrderBy(c => c.SortOrder)
            .Select(c => new { c.ReportJson, c.SchemaVersion })
            .ToListAsync();

        var sections = new List<ReportSection>();
        var schemaVersion = 0;
        foreach (var row in stored)
        {
            var section = StoredSection.Read(row.ReportJson, lang);
            // Bozuk/eski bir bölüm sessizce atlanır — bir hesabın kaydı
            // diğerlerinin raporunu engellemez.
            if (section is null) continue;
            sections.Add(section);
            if (schemaVersion == 0) schemaVersion = row.SchemaVersion;
        }

        if (sections.Count == 0) return (null, false);

        // Profil YALNIZCA alan hiç gelmediğinde okunur. Sorgu da o zaman
        // çalışır: künyeyi zaten taşıyan istekte gereksiz bir tur atmaz.
        var effectiveCompany = company ?? await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.Company)
            .FirstOrDefaultAsync();

        // Boş dize belgeye "firma" satırı olarak girmesin: dizgici `null`
        // bekliyor, boş dize başlıkta boş bir satır bırakırdı.
        if (string.IsNullOrWhiteSpace(effectiveCompany)) effectiveCompany = null;

        return (new ReportPayload(schemaVersion, title, preparedBy, effectiveCompany, date, labels, lang, sections), false);
    }


    private const string PdfContentType = "application/pdf";

    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static ApiError? Validate(ReportPayload payload)
    {
        // `Sections` gövdede hiç gelmemiş olabilir; `IReadOnlyList` imzası bunu
        // engellemez, `JsonSerializer` alanı `null` bırakır. Sayısını okumadan
        // önce bakılır, yoksa geçersiz bir gövde 400 yerine 500 verirdi.
        if (payload.Sections is null) return new ApiError("EMPTY_PAYLOAD");
        if (payload.Labels is null) return new ApiError("MISSING_FIELDS", new { field = "labels" });
        if (payload.Sections.Count == 0) return new ApiError("EMPTY_PAYLOAD");
        if (string.IsNullOrWhiteSpace(payload.Title)) return new ApiError("MISSING_FIELDS", new { field = "title" });
        if (string.IsNullOrWhiteSpace(payload.PreparedBy)) return new ApiError("MISSING_FIELDS", new { field = "preparedBy" });
        // Kütük kolonlarının şema sınırı (HasMaxLength) — aşan değer belge
        // üretildikten SONRA LogReport'ta DB hatasıyla 500 verirdi.
        if (payload.Title.Length > Report.TitleMaxLength)
        {
            return new ApiError("TOO_LONG", new { field = "title", max = Report.TitleMaxLength });
        }
        if (payload.PreparedBy.Length > Report.PreparedByMaxLength)
        {
            return new ApiError("TOO_LONG", new { field = "preparedBy", max = Report.PreparedByMaxLength });
        }
        // Firma kütük kolonu değil, ama artık kullanıcının DÜZENLEDİĞİ bir alan
        // (eskiden yalnız profilden geliyordu ve orada sınırlanmıştı). Sınır
        // aynı kaynaktan okunur ki iki yol aynı şeyi kabul etsin.
        if (payload.Company is not null && payload.Company.Length > ApplicationUser.CompanyMaxLength)
        {
            return new ApiError("TOO_LONG", new { field = "company", max = ApplicationUser.CompanyMaxLength });
        }
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

        return $"{Slugify(basis)}-{FileDate(payload.Date, record.GeneratedAt)}{LangSuffix(payload.Lang)}.{ext}";
    }

    // Belgenin İÇİNE yazılan tarih tarayıcıdan gelir (kullanıcının yerel günü,
    // `reportDateStamp()` → dd.MM.yyyy). Dosya adı da onu kullanır: doğrudan
    // sunucunun UTC saatine düşülürse gece yarısı ile 03:00 arasında dosya adı
    // ile belgenin üstündeki tarih farklı gün gösterirdi. Ayrıştırılamayan bir
    // değer geldiğinde kayıt zamanına düşülür — ad her hâlükârda üretilir.
    internal static string FileDate(string payloadDate, DateTimeOffset fallback) =>
        DateTime.TryParseExact(payloadDate, "dd.MM.yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed)
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : IsoDate(fallback);

    // Dosya adının sonundaki dil eki. Aynı projenin Türkçe ve İngilizce
    // raporu aynı klasöre indirildiğinde ikincisi birincisini ezmesin ve
    // hangisinin hangisi olduğu adından okunsun diye. Yalnızca harf kabul
    // edilir: değer istemciden geliyor ve dosya adına giriyor.
    internal static string LangSuffix(string? lang) =>
        !string.IsNullOrWhiteSpace(lang) && lang.All(char.IsAsciiLetter)
            ? $"-{lang.ToLowerInvariant()}"
            : string.Empty;

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

    internal static string Slugify(string title)
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

// Kütükten yeniden indirme gövdesi — yalnız çerçeve metni taşır; künye
// (başlık, hazırlayan, tarih) kaydın kendisinden okunur.
public record ReportLabelsRequest(ReportLabels Labels, string? Lang);
