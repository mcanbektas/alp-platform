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

        if (projectId is not null && !await OwnsProject(db, projectId.Value, userId))
        {
            return Results.NotFound(new ApiError("PROJECT_NOT_FOUND"));
        }

        var bytes = builder.Build(payload);
        var record = await Persist(db, storage.Value, userId, payload, ReportFormat.Pdf, bytes, "pdf", projectId);

        return Results.File(bytes, "application/pdf", $"{record.Id}.pdf");
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

        if (projectId is not null && !await OwnsProject(db, projectId.Value, userId))
        {
            return Results.NotFound(new ApiError("PROJECT_NOT_FOUND"));
        }

        var bytes = builder.Build(payload);
        var record = await Persist(db, storage.Value, userId, payload, ReportFormat.Xlsx, bytes, "xlsx", projectId);

        const string xlsxContentType =
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        return Results.File(bytes, xlsxContentType, $"{record.Id}.xlsx");
    }

    // Var olmayan ve başkasına ait proje AYNI 404'ü verir — anti-enumeration
    // kuralı burada da geçerli (bkz. Download).
    private static Task<bool> OwnsProject(AppDbContext db, Guid projectId, string userId) =>
        db.Projects.AnyAsync(p => p.Id == projectId && p.UserId == userId);

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

        return Results.File(await File.ReadAllBytesAsync(path), contentType, $"{Slugify(report.Title)}.{ext}");
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

    private static string Slugify(string title)
    {
        var cleaned = new string(title.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
        return cleaned.Trim('-').ToLowerInvariant() is { Length: > 0 } s ? s : "rapor";
    }
}

public record ReportSummary(Guid Id, string Title, string PreparedBy, ReportFormat Format, long FileSize, DateTimeOffset GeneratedAt);
