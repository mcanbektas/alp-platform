using Alp.Api.Common;
using Alp.Api.Http;
using Alp.Data;
using Alp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Alp.Api.Projects;

public static class ProjectEndpoints
{
    // Proje/hesap gövdeleri düz JSON metaverisi taşır (isim, açıklama, sıra
    // listesi) — Auth uçlarıyla aynı küçük üst sınır yeterli.
    private const long ProjectBodyLimitBytes = 16 * 1024;

    // Hesap gövdesi InputsJson/ResultJson/ReportJson taşır; ReportJson içine
    // gömülü SVG şema/grafik olabilir. Rapor uçlarının 5 MB'ından küçük ama
    // düz form verisinden büyük bir üst sınır — bkz. görev tanımı.
    private const long CalculationBodyLimitBytes = 2 * 1024 * 1024;

    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/projects").RequireAuthorization();

        projects.MapGet("/", ListProjects);
        projects.MapPost("/", CreateProject).LimitBodySize(ProjectBodyLimitBytes);
        projects.MapGet("/{id:guid}", GetProject);
        projects.MapPatch("/{id:guid}", UpdateProject).LimitBodySize(ProjectBodyLimitBytes);
        projects.MapDelete("/{id:guid}", DeleteProject);

        projects.MapPost("/{id:guid}/calculations", CreateCalculation).LimitBodySize(CalculationBodyLimitBytes);
        projects.MapPost("/{id:guid}/calculations/reorder", ReorderCalculations).LimitBodySize(ProjectBodyLimitBytes);

        // Tekil hesap uçları /api/projects/{id} altında değil, kendi kökünde
        // yaşar — hesap güncelleme/silme üst projeyi URL'de tekrar etmeden
        // doğrudan kendi kimliğiyle adreslenir. Sahiplik yine de her zaman
        // Calculation.Project.UserId üzerinden doğrulanır (aşağıya bkz.).
        var calculations = app.MapGroup("/api/calculations").RequireAuthorization();

        calculations.MapGet("/{id:guid}", GetCalculation);
        calculations.MapPatch("/{id:guid}", UpdateCalculation).LimitBodySize(CalculationBodyLimitBytes);
        calculations.MapDelete("/{id:guid}", DeleteCalculation);
    }

    private static async Task<IResult> ListProjects(AppDbContext db, HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var projects = await db.Projects
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new ProjectSummary(p.Id, p.Name, p.Description, p.CreatedAt, p.UpdatedAt, p.Calculations.Count))
            .ToListAsync();

        return Results.Ok(new ProjectListResponse(projects));
    }

    private static async Task<IResult> CreateProject(CreateProjectRequest req, AppDbContext db, HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new ApiError("MISSING_FIELDS"));

        var now = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = req.Name.Trim(),
            Description = req.Description,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync();

        return Results.Created(
            $"/api/projects/{project.Id}",
            new ProjectSummary(project.Id, project.Name, project.Description, project.CreatedAt, project.UpdatedAt, 0));
    }

    private static async Task<IResult> GetProject(Guid id, AppDbContext db, HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id);
        // Proje yok / başka kullanıcıya ait — AYNI 404 şekli döner, hangisi
        // olduğunu dışarı sızdırmaz.
        if (project is null || project.UserId != userId) return Results.NotFound(new ApiError("PROJECT_NOT_FOUND"));

        var calculations = await db.Calculations
            .Where(c => c.ProjectId == id)
            .OrderBy(c => c.SortOrder)
            .Select(c => ToDto(c))
            .ToListAsync();

        return Results.Ok(new ProjectDetailResponse(
            project.Id, project.Name, project.Description, project.CreatedAt, project.UpdatedAt, calculations));
    }

    private static async Task<IResult> UpdateProject(Guid id, UpdateProjectRequest req, AppDbContext db, HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var project = await db.Projects.Include(p => p.Calculations).FirstOrDefaultAsync(p => p.Id == id);
        if (project is null || project.UserId != userId) return Results.NotFound(new ApiError("PROJECT_NOT_FOUND"));

        var changed = false;

        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new ApiError("MISSING_FIELDS"));
            project.Name = req.Name.Trim();
            changed = true;
        }

        if (req.Description is not null)
        {
            // Boş dize açıkça gönderilmişse alan temizlenir (null'a döner);
            // atlanmış/null gönderilmiş olsaydı bu bloğa hiç girilmezdi.
            project.Description = req.Description.Length == 0 ? null : req.Description;
            changed = true;
        }

        if (changed) project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return Results.Ok(new ProjectSummary(
            project.Id, project.Name, project.Description, project.CreatedAt, project.UpdatedAt, project.Calculations.Count));
    }

    private static async Task<IResult> DeleteProject(Guid id, AppDbContext db, HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id);
        if (project is null || project.UserId != userId) return Results.NotFound(new ApiError("PROJECT_NOT_FOUND"));

        // Cascade delete (AppDbContext: Project -> Calculation) hesapları da
        // temizler — elle silme gerekmez.
        db.Projects.Remove(project);
        await db.SaveChangesAsync();

        return Results.NoContent();
    }

    private static async Task<IResult> CreateCalculation(Guid id, CreateCalculationRequest req, AppDbContext db, HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id);
        if (project is null || project.UserId != userId) return Results.NotFound(new ApiError("PROJECT_NOT_FOUND"));

        if (string.IsNullOrWhiteSpace(req.ToolKey)
            || string.IsNullOrWhiteSpace(req.InputsJson)
            || string.IsNullOrWhiteSpace(req.ResultJson)
            || string.IsNullOrWhiteSpace(req.EngineVersion)
            || req.SchemaVersion < 1)
        {
            return Results.BadRequest(new ApiError("MISSING_FIELDS"));
        }

        var maxSortOrder = await db.Calculations
            .Where(c => c.ProjectId == id)
            .Select(c => (int?)c.SortOrder)
            .MaxAsync();

        var now = DateTimeOffset.UtcNow;
        var calculation = new Calculation
        {
            Id = Guid.NewGuid(),
            ProjectId = id,
            ToolKey = req.ToolKey,
            ToolMode = req.ToolMode,
            SortOrder = maxSortOrder is null ? 0 : maxSortOrder.Value + 1,
            InputsJson = req.InputsJson,
            ResultJson = req.ResultJson,
            ReportJson = req.ReportJson,
            EngineVersion = req.EngineVersion,
            SchemaVersion = req.SchemaVersion,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Calculations.Add(calculation);
        project.UpdatedAt = now;
        await db.SaveChangesAsync();

        return Results.Created($"/api/calculations/{calculation.Id}", ToDto(calculation));
    }

    // Kaydedilmiş bir hesabı araç ekranına geri yüklemek için tek kayıt okuma.
    // Proje detayı üzerinden de okunabilirdi ama araç ekranı yalnızca `?hesap=`
    // parametresindeki kimliği bilir — üst projeyi bilmediği için o yolu
    // kullanamaz. Sahiplik yine Project.UserId üzerinden doğrulanır.
    private static async Task<IResult> GetCalculation(Guid id, AppDbContext db, HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var calculation = await db.Calculations.Include(c => c.Project).FirstOrDefaultAsync(c => c.Id == id);
        if (calculation is null || calculation.Project is null || calculation.Project.UserId != userId)
        {
            return Results.NotFound(new ApiError("CALCULATION_NOT_FOUND"));
        }

        return Results.Ok(new CalculationDetailResponse(
            ToDto(calculation), calculation.Project.Id, calculation.Project.Name));
    }

    private static async Task<IResult> UpdateCalculation(Guid id, UpdateCalculationRequest req, AppDbContext db, HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var calculation = await db.Calculations.Include(c => c.Project).FirstOrDefaultAsync(c => c.Id == id);
        // Calculation'ın kendi UserId'si yok — sahiplik Project üzerinden
        // dolaylı doğrulanır. Yok / başkasının projesine ait — AYNI 404.
        if (calculation is null || calculation.Project is null || calculation.Project.UserId != userId)
        {
            return Results.NotFound(new ApiError("CALCULATION_NOT_FOUND"));
        }

        // CreateCalculation ile aynı zorunlu-alan sözleşmesi: sağlanan
        // InputsJson/ResultJson/EngineVersion boş/boşluk olamaz, SchemaVersion
        // sağlanmışsa >= 1 olmalı. Aksi halde PATCH, hesabın bu alanlarının
        // hiçbir zaman boş/geçersiz olmayacağı varsayımını (izlenebilirlik)
        // sessizce bozar.
        if ((req.InputsJson is not null && string.IsNullOrWhiteSpace(req.InputsJson))
            || (req.ResultJson is not null && string.IsNullOrWhiteSpace(req.ResultJson))
            || (req.EngineVersion is not null && string.IsNullOrWhiteSpace(req.EngineVersion))
            || (req.SchemaVersion is not null && req.SchemaVersion < 1))
        {
            return Results.BadRequest(new ApiError("MISSING_FIELDS"));
        }

        var changed = false;

        if (req.ToolMode is not null) { calculation.ToolMode = req.ToolMode; changed = true; }
        if (req.InputsJson is not null) { calculation.InputsJson = req.InputsJson; changed = true; }
        if (req.ResultJson is not null) { calculation.ResultJson = req.ResultJson; changed = true; }
        if (req.ReportJson is not null) { calculation.ReportJson = req.ReportJson; changed = true; }
        if (req.EngineVersion is not null) { calculation.EngineVersion = req.EngineVersion; changed = true; }
        if (req.SchemaVersion is not null) { calculation.SchemaVersion = req.SchemaVersion.Value; changed = true; }

        if (changed)
        {
            var now = DateTimeOffset.UtcNow;
            calculation.UpdatedAt = now;
            calculation.Project.UpdatedAt = now;
        }

        await db.SaveChangesAsync();

        return Results.Ok(ToDto(calculation));
    }

    private static async Task<IResult> DeleteCalculation(Guid id, AppDbContext db, HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var calculation = await db.Calculations.Include(c => c.Project).FirstOrDefaultAsync(c => c.Id == id);
        if (calculation is null || calculation.Project is null || calculation.Project.UserId != userId)
        {
            return Results.NotFound(new ApiError("CALCULATION_NOT_FOUND"));
        }

        calculation.Project.UpdatedAt = DateTimeOffset.UtcNow;
        db.Calculations.Remove(calculation);
        await db.SaveChangesAsync();

        return Results.NoContent();
    }

    private static async Task<IResult> ReorderCalculations(Guid id, ReorderCalculationsRequest req, AppDbContext db, HttpContext http)
    {
        var userId = CurrentUserId(http);
        if (userId is null) return Results.Unauthorized();

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id);
        if (project is null || project.UserId != userId) return Results.NotFound(new ApiError("PROJECT_NOT_FOUND"));

        if (req.OrderedIds is null) return Results.BadRequest(new ApiError("MISSING_FIELDS"));

        var calculations = await db.Calculations.Where(c => c.ProjectId == id).ToListAsync();

        // Verilen kimlik kümesi bu projenin mevcut hesap kümesiyle TAM olarak
        // eşleşmeli — ne eksik ne fazla, tekrar da yok. Bu kontrol istemciye
        // güvenmez: başka bir projenin hesap kimliğinin sızması ya da bir
        // satırın sessizce düşürülmesi burada engellenir.
        var givenIds = new HashSet<Guid>(req.OrderedIds);
        var currentIds = new HashSet<Guid>(calculations.Select(c => c.Id));
        if (req.OrderedIds.Count != calculations.Count
            || givenIds.Count != req.OrderedIds.Count
            || !givenIds.SetEquals(currentIds))
        {
            return Results.BadRequest(new ApiError("INVALID_ORDER"));
        }

        var byId = calculations.ToDictionary(c => c.Id);
        for (var index = 0; index < req.OrderedIds.Count; index++)
        {
            byId[req.OrderedIds[index]].SortOrder = index;
        }

        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return Results.Ok(new OkResponse(true));
    }

    private static CalculationDto ToDto(Calculation c) => new(
        c.Id, c.ToolKey, c.ToolMode, c.SortOrder, c.InputsJson, c.ResultJson, c.ReportJson,
        c.EngineVersion, c.SchemaVersion, c.CreatedAt, c.UpdatedAt);

    private static string? CurrentUserId(HttpContext http) =>
        http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
}
