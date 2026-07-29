using System.Globalization;
using System.Text;
using Alp.Api.Common;
using Alp.Api.Http;
using Alp.Data;
using Alp.Domain;
using Alp.Reports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Alp.Api.Reports;

public static class ReportEndpoints
{
    // §4.4: "Rapor yükü boyut sınırı (varsayılan 5 MB); SVG dizeleri şişebilir."
    private const long ReportBodyLimitBytes = 5 * 1024 * 1024;

    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").RequireAuthorization();

        group.MapPost("/pdf", GeneratePdf).RequireRateLimiting("reports").LimitBodySize(ReportBodyLimitBytes);
        group.MapPost("/xlsx", GenerateXlsx).RequireRateLimiting("reports").LimitBodySize(ReportBodyLimitBytes);
        group.MapGet("/", ListReports);
        group.MapGet("/{id:guid}/download", Download);
    }

    private static async Task<IResult> GeneratePdf(
        ReportPayload payload,
        [FromQuery] Guid? projectId,
        PdfReportBuilder builder,
        AppDbContext db,
        HttpContext http,
        IOptions<StorageOptions> storage)
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
        var record = await Persist(db, storage.Value, userId, payload, ReportFormat.Pdf, bytes, "pdf", projectId);

        return Results.File(bytes, "application/pdf", DownloadName(payload, projectName, record, "pdf"));
    }

    private static async Task<IResult> GenerateXlsx(
        ReportPayload payload,
        [FromQuery] Guid? projectId,
        XlsxReportBuilder builder,
        AppDbContext db,
        HttpContext http,
        IOptions<StorageOptions> storage)
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
        var record = await Persist(db, storage.Value, userId, payload, ReportFormat.Xlsx, bytes, "xlsx", projectId);

        const string xlsxContentType =
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        return Results.File(bytes, xlsxContentType, DownloadName(payload, projectName, record, "xlsx"));
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

    private static async Task<IResult> Download(Guid id, AppDbContext db, HttpContext http, IOptions<StorageOptions> storage)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == id);
        // Var olmayan ve başkasına ait rapor AYNI yanıtı verir — hangisi
        // olduğunu dışarı sızdırmaz.
        if (report is null || report.UserId != userId) return Results.NotFound();

        var path = Path.Combine(ResolveReportsPath(storage.Value), report.FilePath);
        if (!File.Exists(path)) return Results.NotFound();

        var contentType = report.Format == ReportFormat.Pdf
            ? "application/pdf"
            : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        var ext = report.Format == ReportFormat.Pdf ? "pdf" : "xlsx";

        // Geçmişten indirmede yükün kendisi elde yok; ad kayıttan kurulur.
        // Kayıt araç adını tutmadığı için burada başlığa düşülür — bu uç henüz
        // arayüzden çağrılmıyor, çağrılacaksa Report'a araç adı eklenmeli.
        var name = $"{Slugify(report.Title)}-{IsoDate(report.GeneratedAt)}.{ext}";
        return Results.File(await File.ReadAllBytesAsync(path), contentType, name);
    }

    private static ApiError? Validate(ReportPayload payload)
    {
        if (payload.Sections.Count == 0) return new ApiError("EMPTY_PAYLOAD");
        if (string.IsNullOrWhiteSpace(payload.Title)) return new ApiError("MISSING_FIELDS", new { field = "title" });
        if (string.IsNullOrWhiteSpace(payload.PreparedBy)) return new ApiError("MISSING_FIELDS", new { field = "preparedBy" });
        return null;
    }

    private static async Task<Report> Persist(
        AppDbContext db, StorageOptions storage, string userId,
        ReportPayload payload, ReportFormat format, byte[] bytes, string ext, Guid? projectId = null)
    {
        var dir = ResolveReportsPath(storage);
        Directory.CreateDirectory(dir);

        var id = Guid.NewGuid();
        var fileName = $"{id}.{ext}";
        await File.WriteAllBytesAsync(Path.Combine(dir, fileName), bytes);

        var report = new Report
        {
            Id = id,
            ProjectId = projectId,
            UserId = userId,
            Title = payload.Title,
            PreparedBy = payload.PreparedBy,
            Revision = 1,
            Format = format,
            FilePath = fileName,
            FileSize = bytes.LongLength,
            GeneratedAt = DateTimeOffset.UtcNow,
        };
        db.Reports.Add(report);
        await db.SaveChangesAsync();
        return report;
    }

    private static string ResolveReportsPath(StorageOptions storage) =>
        Path.IsPathRooted(storage.ReportsPath)
            ? storage.ReportsPath
            : Path.Combine(AppContext.BaseDirectory, storage.ReportsPath);

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
