using System.Text.Json;
using Alp.Api.Common;
using Alp.Api.Http;
using Alp.Data;
using Alp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Alp.Api.Comm;

public static class CommEndpoints
{
    // Proje gövdesi düz JSON metaverisi taşır (isim, açıklama) —
    // ProjectEndpoints'teki üst sınırla aynı.
    private const long CommProjectBodyLimitBytes = 16 * 1024;

    // Şema gövdesi DefinitionJson taşır — bir protokol tanımı (alan listesi,
    // CRC/uzunluk kuralları). Proje gövdesinden büyük ama hesap gövdesi kadar
    // (2 MB, gömülü SVG) ağır değil; 256 KB tek bir protokol tanımına bolca yer
    // bırakır.
    private const long ProtocolSchemaBodyLimitBytes = 256 * 1024;

    private const int CommProjectNameMax = CommProject.NameMaxLength;
    private const int CommProjectDescriptionMax = CommProject.DescriptionMaxLength;
    private const int ProtocolSchemaNameMax = ProtocolSchema.NameMaxLength;
    private const int ProtocolSchemaVersionMax = ProtocolSchema.VersionMaxLength;

    public static void MapCommEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/comm/projects").RequireAuthorization();

        projects.MapGet("/", ListCommProjects);
        projects.MapPost("/", CreateCommProject).RequireRateLimiting("writes").LimitBodySize(CommProjectBodyLimitBytes);
        projects.MapGet("/{id:guid}", GetCommProject);
        projects.MapPatch("/{id:guid}", UpdateCommProject).RequireRateLimiting("writes").LimitBodySize(CommProjectBodyLimitBytes);
        projects.MapDelete("/{id:guid}", DeleteCommProject).RequireRateLimiting("writes");

        projects.MapPost("/{id:guid}/schemas", CreateProtocolSchema)
            .RequireRateLimiting("writes").LimitBodySize(ProtocolSchemaBodyLimitBytes);

        // Tekil şema uçları /api/comm/projects/{id} altında değil kendi
        // kökünde yaşar — ProjectEndpoints'teki Calculation kalıbıyla aynı
        // gerekçe: güncelleme/silme üst projeyi URL'de tekrar etmeden kendi
        // kimliğiyle adreslenir. Sahiplik yine ProtocolSchema.CommProject.UserId
        // üzerinden doğrulanır (aşağıya bkz.).
        var schemas = app.MapGroup("/api/comm/schemas").RequireAuthorization();

        schemas.MapGet("/{id:guid}", GetProtocolSchema);
        schemas.MapPatch("/{id:guid}", UpdateProtocolSchema)
            .RequireRateLimiting("writes").LimitBodySize(ProtocolSchemaBodyLimitBytes);
        schemas.MapDelete("/{id:guid}", DeleteProtocolSchema).RequireRateLimiting("writes");
    }

    private static async Task<IResult> ListCommProjects(AppDbContext db, HttpContext http)
    {
        var userId = RequireUserId(http, out var error);
        if (error is not null) return error;

        var projects = await db.CommProjects
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new CommProjectSummary(p.Id, p.Name, p.Description, p.CreatedAt, p.UpdatedAt, p.ProtocolSchemas.Count))
            .ToListAsync();

        return Results.Ok(new CommProjectListResponse(projects));
    }

    private static async Task<IResult> CreateCommProject(CreateCommProjectRequest req, AppDbContext db, HttpContext http)
    {
        var userId = RequireUserId(http, out var error);
        if (error is not null) return error;

        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new ApiError("MISSING_FIELDS"));
        if (TooLong(req.Name.Trim(), CommProjectNameMax, "name", out var tooLong)
            || TooLong(req.Description, CommProjectDescriptionMax, "description", out tooLong))
        {
            return tooLong!;
        }

        var now = DateTimeOffset.UtcNow;
        var project = new CommProject
        {
            Id = Guid.NewGuid(),
            UserId = userId!, // guard geçildi: kimlik null değil
            Name = req.Name.Trim(),
            Description = req.Description,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.CommProjects.Add(project);
        await db.SaveChangesAsync();

        return Results.Created(
            $"/api/comm/projects/{project.Id}",
            new CommProjectSummary(project.Id, project.Name, project.Description, project.CreatedAt, project.UpdatedAt, 0));
    }

    internal static async Task<IResult> GetCommProject(Guid id, AppDbContext db, HttpContext http)
    {
        var (owned, error) = await LoadOwnedCommProject(db, http, id);
        if (error is not null) return error;
        var project = owned!; // guard geçildi: sahiplenilen proje null değil

        var schemas = await db.ProtocolSchemas
            .Where(s => s.CommProjectId == id)
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new ProtocolSchemaSummary(s.Id, s.Name, s.Version, s.CreatedAt, s.UpdatedAt))
            .ToListAsync();

        return Results.Ok(new CommProjectDetailResponse(
            project.Id, project.Name, project.Description, project.CreatedAt, project.UpdatedAt, schemas));
    }

    internal static async Task<IResult> UpdateCommProject(Guid id, UpdateCommProjectRequest req, AppDbContext db, HttpContext http)
    {
        var (owned, error) = await LoadOwnedCommProject(db, http, id);
        if (error is not null) return error;
        var project = owned!; // guard geçildi: sahiplenilen proje null değil

        var changed = false;

        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new ApiError("MISSING_FIELDS"));
            if (TooLong(req.Name.Trim(), CommProjectNameMax, "name", out var nameTooLong)) return nameTooLong!;
            project.Name = req.Name.Trim();
            changed = true;
        }

        if (req.Description is not null)
        {
            if (TooLong(req.Description, CommProjectDescriptionMax, "description", out var descTooLong)) return descTooLong!;
            // Boş dize açıkça gönderilmişse alan temizlenir (null'a döner);
            // atlanmış/null gönderilmiş olsaydı bu bloğa hiç girilmezdi.
            project.Description = req.Description.Length == 0 ? null : req.Description;
            changed = true;
        }

        if (changed) project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var schemaCount = await db.ProtocolSchemas.CountAsync(s => s.CommProjectId == id);
        return Results.Ok(new CommProjectSummary(
            project.Id, project.Name, project.Description, project.CreatedAt, project.UpdatedAt, schemaCount));
    }

    internal static async Task<IResult> DeleteCommProject(Guid id, AppDbContext db, HttpContext http)
    {
        var (project, error) = await LoadOwnedCommProject(db, http, id);
        if (error is not null) return error;

        // Cascade delete (AppDbContext: CommProject -> ProtocolSchema) şemaları
        // da temizler — elle silme gerekmez.
        db.CommProjects.Remove(project!);
        await db.SaveChangesAsync();

        return Results.NoContent();
    }

    internal static async Task<IResult> CreateProtocolSchema(Guid id, CreateProtocolSchemaRequest req, AppDbContext db, HttpContext http)
    {
        var (project, error) = await LoadOwnedCommProject(db, http, id);
        if (error is not null) return error;

        if (string.IsNullOrWhiteSpace(req.Name)
            || string.IsNullOrWhiteSpace(req.Version)
            || string.IsNullOrWhiteSpace(req.DefinitionJson))
        {
            return Results.BadRequest(new ApiError("MISSING_FIELDS"));
        }

        if (TooLong(req.Name.Trim(), ProtocolSchemaNameMax, "name", out var tooLong)
            || TooLong(req.Version.Trim(), ProtocolSchemaVersionMax, "version", out tooLong))
        {
            return tooLong!;
        }

        if (BadJson(req.DefinitionJson, "definitionJson", out var badJson)) return badJson!;

        var name = req.Name.Trim();
        var version = req.Version.Trim();

        // Aynı proje içinde aynı ad+sürüm ikinci kez oluşturulamaz (benzersiz
        // dizin, AppDbContext). Burada önden bakılır ki istek DbUpdateException
        // yerine anlaşılır bir hata alsın.
        var exists = await db.ProtocolSchemas
            .AnyAsync(s => s.CommProjectId == id && s.Name == name && s.Version == version);
        if (exists) return Results.BadRequest(new ApiError("SCHEMA_VERSION_EXISTS"));

        var now = DateTimeOffset.UtcNow;
        var schema = new ProtocolSchema
        {
            Id = Guid.NewGuid(),
            CommProjectId = id,
            Name = name,
            Version = version,
            DefinitionJson = req.DefinitionJson,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.ProtocolSchemas.Add(schema);
        project!.UpdatedAt = now;
        await db.SaveChangesAsync();

        return Results.Created($"/api/comm/schemas/{schema.Id}", ToDto(schema));
    }

    internal static async Task<IResult> GetProtocolSchema(Guid id, AppDbContext db, HttpContext http)
    {
        var (schema, error) = await LoadOwnedProtocolSchema(db, http, id);
        if (error is not null) return error;

        return Results.Ok(new ProtocolSchemaDetailResponse(
            ToDto(schema!), schema!.CommProject!.Id, schema.CommProject.Name));
    }

    internal static async Task<IResult> UpdateProtocolSchema(Guid id, UpdateProtocolSchemaRequest req, AppDbContext db, HttpContext http)
    {
        var (schema, error) = await LoadOwnedProtocolSchema(db, http, id);
        if (error is not null) return error;

        if ((req.Name is not null && string.IsNullOrWhiteSpace(req.Name))
            || (req.Version is not null && string.IsNullOrWhiteSpace(req.Version))
            || (req.DefinitionJson is not null && string.IsNullOrWhiteSpace(req.DefinitionJson)))
        {
            return Results.BadRequest(new ApiError("MISSING_FIELDS"));
        }

        if (TooLong(req.Name?.Trim(), ProtocolSchemaNameMax, "name", out var tooLong)
            || TooLong(req.Version?.Trim(), ProtocolSchemaVersionMax, "version", out tooLong))
        {
            return tooLong!;
        }

        if (BadJson(req.DefinitionJson, "definitionJson", out var badJson)) return badJson!;

        var newName = req.Name?.Trim() ?? schema!.Name;
        var newVersion = req.Version?.Trim() ?? schema!.Version;
        if ((req.Name is not null || req.Version is not null) && (newName != schema!.Name || newVersion != schema.Version))
        {
            var exists = await db.ProtocolSchemas
                .AnyAsync(s => s.Id != id && s.CommProjectId == schema.CommProjectId && s.Name == newName && s.Version == newVersion);
            if (exists) return Results.BadRequest(new ApiError("SCHEMA_VERSION_EXISTS"));
        }

        var changed = false;
        if (req.Name is not null) { schema!.Name = newName; changed = true; }
        if (req.Version is not null) { schema!.Version = newVersion; changed = true; }
        if (req.DefinitionJson is not null) { schema!.DefinitionJson = req.DefinitionJson; changed = true; }

        if (changed)
        {
            var now = DateTimeOffset.UtcNow;
            schema!.UpdatedAt = now;
            schema.CommProject!.UpdatedAt = now;
        }

        await db.SaveChangesAsync();

        return Results.Ok(ToDto(schema!));
    }

    internal static async Task<IResult> DeleteProtocolSchema(Guid id, AppDbContext db, HttpContext http)
    {
        var userId = RequireUserId(http, out var error);
        if (error is not null) return error!;

        var owned = await db.ProtocolSchemas
            .Where(s => s.Id == id && s.CommProject!.UserId == userId)
            .Select(s => new { s.Id, s.CommProjectId })
            .FirstOrDefaultAsync();
        if (owned is null) return Results.NotFound(new ApiError("SCHEMA_NOT_FOUND"));

        // `UtcNow` sorgu içinde çağrılmaz: SQLite sağlayıcısı (testler) onu
        // çeviremiyor; değer önce yakalanır, sorguya sabit girer.
        var now = DateTimeOffset.UtcNow;
        await db.ProtocolSchemas.Where(s => s.Id == owned.Id).ExecuteDeleteAsync();
        await db.CommProjects
            .Where(p => p.Id == owned.CommProjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, now));

        return Results.NoContent();
    }

    // ---- Girdi doğrulama yardımcıları ----
    // ProjectEndpoints'teki TooLong/BadJson ile birebir aynı — modüller
    // birbirini çağırmaz (CLAUDE.md), o yüzden kopya burada da durur.

    private static bool TooLong(string? value, int max, string field, out IResult? error)
    {
        if (value is not null && value.Length > max)
        {
            error = Results.BadRequest(new ApiError("TOO_LONG", new { field, max }));
            return true;
        }
        error = null;
        return false;
    }

    private static bool BadJson(string? value, string field, out IResult? error)
    {
        error = null;
        if (value is null) return false;
        try
        {
            using var _ = JsonDocument.Parse(value);
            return false;
        }
        catch (JsonException)
        {
            error = Results.BadRequest(new ApiError("INVALID_JSON", new { field }));
            return true;
        }
    }

    private static ProtocolSchemaDto ToDto(ProtocolSchema s) => new(
        s.Id, s.Name, s.Version, s.DefinitionJson, s.CreatedAt, s.UpdatedAt);

    private static string? CurrentUserId(HttpContext http) =>
        http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;

    // ---- Sahiplik yardımcıları ---- (ProjectEndpoints'teki desenle aynı)

    private static string? RequireUserId(HttpContext http, out IResult? error)
    {
        var userId = CurrentUserId(http);
        error = userId is null ? Results.Unauthorized() : null;
        return userId;
    }

    private static async Task<(CommProject? Project, IResult? Error)> LoadOwnedCommProject(
        AppDbContext db, HttpContext http, Guid id)
    {
        var userId = RequireUserId(http, out var error);
        if (error is not null) return (null, error);

        var project = await db.CommProjects.FirstOrDefaultAsync(p => p.Id == id);
        // Proje yok / başka kullanıcıya ait — AYNI 404 şekli döner, hangisi
        // olduğunu dışarı sızdırmaz.
        return project is null || project.UserId != userId
            ? (null, Results.NotFound(new ApiError("COMM_PROJECT_NOT_FOUND")))
            : (project, null);
    }

    private static async Task<(ProtocolSchema? Schema, IResult? Error)> LoadOwnedProtocolSchema(
        AppDbContext db, HttpContext http, Guid id)
    {
        var userId = RequireUserId(http, out var error);
        if (error is not null) return (null, error);

        var schema = await db.ProtocolSchemas.Include(s => s.CommProject).FirstOrDefaultAsync(s => s.Id == id);
        return schema is null || schema.CommProject is null || schema.CommProject.UserId != userId
            ? (null, Results.NotFound(new ApiError("SCHEMA_NOT_FOUND")))
            : (schema, null);
    }
}
