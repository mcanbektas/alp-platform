using System.Text.Json;
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

    // Alan uzunlukları — kaynak Alp.Domain'deki sabitler; şemadaki HasMaxLength
    // aynı sabitleri okur (AppDbContext). Gövde sınırı tek başına yetmez:
    // 16 KB'lık gövdeye 15 KB'lık tek bir ad sığar ve o ad listede, raporda,
    // dosya adında dolaşır. Aşan istek DB'ye düşmeden TOO_LONG döner; şema
    // sınırı yalnızca son savunmadır.
    private const int ProjectNameMax = Project.NameMaxLength;
    private const int ProjectDescriptionMax = Project.DescriptionMaxLength;
    private const int ToolKeyMax = Calculation.ToolKeyMaxLength;
    private const int ToolModeMax = Calculation.ToolModeMaxLength;
    private const int EngineVersionMax = Calculation.EngineVersionMaxLength;

    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/projects").RequireAuthorization();

        // Bütün yazma uçları "writes" kovasına girer (kullanıcı başına, bkz.
        // Program.cs). Politika baştan beri vardı ama yalnızca PATCH /api/me'ye
        // bağlıydı — 2 MB'lık hesap POST'u dahil buradaki uçlar sınırsızdı.
        projects.MapGet("/", ListProjects);
        projects.MapPost("/", CreateProject).RequireRateLimiting("writes").LimitBodySize(ProjectBodyLimitBytes);
        projects.MapGet("/{id:guid}", GetProject);
        projects.MapPatch("/{id:guid}", UpdateProject).RequireRateLimiting("writes").LimitBodySize(ProjectBodyLimitBytes);
        projects.MapDelete("/{id:guid}", DeleteProject).RequireRateLimiting("writes");

        projects.MapPost("/{id:guid}/calculations", CreateCalculation)
            .RequireRateLimiting("writes").LimitBodySize(CalculationBodyLimitBytes);
        projects.MapPost("/{id:guid}/calculations/reorder", ReorderCalculations)
            .RequireRateLimiting("writes").LimitBodySize(ProjectBodyLimitBytes);

        // Tekil hesap uçları /api/projects/{id} altında değil, kendi kökünde
        // yaşar — hesap güncelleme/silme üst projeyi URL'de tekrar etmeden
        // doğrudan kendi kimliğiyle adreslenir. Sahiplik yine de her zaman
        // Calculation.Project.UserId üzerinden doğrulanır (aşağıya bkz.).
        var calculations = app.MapGroup("/api/calculations").RequireAuthorization();

        calculations.MapGet("/{id:guid}", GetCalculation);
        calculations.MapPatch("/{id:guid}", UpdateCalculation)
            .RequireRateLimiting("writes").LimitBodySize(CalculationBodyLimitBytes);
        calculations.MapDelete("/{id:guid}", DeleteCalculation).RequireRateLimiting("writes");
    }

    private static async Task<IResult> ListProjects(AppDbContext db, HttpContext http)
    {
        var userId = RequireUserId(http, out var error);
        if (error is not null) return error;

        var projects = await db.Projects
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new ProjectSummary(p.Id, p.Name, p.Description, p.CreatedAt, p.UpdatedAt, p.Calculations.Count))
            .ToListAsync();

        return Results.Ok(new ProjectListResponse(projects));
    }

    private static async Task<IResult> CreateProject(CreateProjectRequest req, AppDbContext db, HttpContext http)
    {
        var userId = RequireUserId(http, out var error);
        if (error is not null) return error;

        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new ApiError("MISSING_FIELDS"));
        if (TooLong(req.Name.Trim(), ProjectNameMax, "name", out var tooLong)
            || TooLong(req.Description, ProjectDescriptionMax, "description", out tooLong))
        {
            return tooLong!;
        }

        var now = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = userId!, // guard geçildi: kimlik null değil
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

    // `lang`: önizleme satırları kayıttaki dil haritasından seçilir (bkz.
    // StoredSection). Verilmezse Türkçeye düşer — eski istemci kırılmaz.
    internal static async Task<IResult> GetProject(Guid id, AppDbContext db, HttpContext http, string? lang = null)
    {
        var (owned, error) = await LoadOwnedProject(db, http, id);
        if (error is not null) return error;
        var project = owned!; // guard geçildi: sahiplenilen proje null değil

        // `ReportJson` yanıta girmez ama önizleme satırları ondan türetildiği
        // için okunur. Satır içi SVG bu yüzden sunucuda kalır: 60 hesaplı bir
        // projede yanıt ~955 KB'dan ~30 KB'a iner.
        var stored = await db.Calculations
            .Where(c => c.ProjectId == id)
            .OrderBy(c => c.SortOrder)
            .Select(c => new
            {
                c.Id, c.ToolKey, c.ToolMode, c.SortOrder, c.ReportJson,
                c.EngineVersion, c.SchemaVersion, c.CreatedAt, c.UpdatedAt,
            })
            .ToListAsync();

        var calculations = stored.Select(c =>
        {
            var (preview, mode) = ReportPreview.From(c.ReportJson, lang ?? "tr");
            return new CalculationSummaryDto(
                c.Id, c.ToolKey, c.ToolMode, c.SortOrder,
                preview, mode, c.ReportJson is not null,
                c.EngineVersion, c.SchemaVersion, c.CreatedAt, c.UpdatedAt);
        }).ToList();

        return Results.Ok(new ProjectDetailResponse(
            project.Id, project.Name, project.Description, project.CreatedAt, project.UpdatedAt, calculations));
    }

    internal static async Task<IResult> UpdateProject(Guid id, UpdateProjectRequest req, AppDbContext db, HttpContext http)
    {
        // `LoadOwnedProject` `Include(Calculations)` yapmaz: eskiden yalnızca
        // dönüş yükündeki `Count` için tüm hesapları (ReportJson'larıyla, gömülü
        // SVG'leriyle) belleğe çekiyordu — proje adını değiştirmek 60 hesaplı
        // projede ~950 KB taşımak demekti. Sayı aşağıda ayrı skaler `CountAsync`.
        var (owned, error) = await LoadOwnedProject(db, http, id);
        if (error is not null) return error;
        var project = owned!; // guard geçildi: sahiplenilen proje null değil

        var changed = false;

        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new ApiError("MISSING_FIELDS"));
            if (TooLong(req.Name.Trim(), ProjectNameMax, "name", out var nameTooLong)) return nameTooLong!;
            project.Name = req.Name.Trim();
            changed = true;
        }

        if (req.Description is not null)
        {
            if (TooLong(req.Description, ProjectDescriptionMax, "description", out var descTooLong)) return descTooLong!;
            // Boş dize açıkça gönderilmişse alan temizlenir (null'a döner);
            // atlanmış/null gönderilmiş olsaydı bu bloğa hiç girilmezdi.
            project.Description = req.Description.Length == 0 ? null : req.Description;
            changed = true;
        }

        if (changed) project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var calculationCount = await db.Calculations.CountAsync(c => c.ProjectId == id);
        return Results.Ok(new ProjectSummary(
            project.Id, project.Name, project.Description, project.CreatedAt, project.UpdatedAt, calculationCount));
    }

    internal static async Task<IResult> DeleteProject(Guid id, AppDbContext db, HttpContext http)
    {
        var (project, error) = await LoadOwnedProject(db, http, id);
        if (error is not null) return error;

        // Cascade delete (AppDbContext: Project -> Calculation) hesapları da
        // temizler — elle silme gerekmez.
        db.Projects.Remove(project!);
        await db.SaveChangesAsync();

        return Results.NoContent();
    }

    internal static async Task<IResult> CreateCalculation(Guid id, CreateCalculationRequest req, AppDbContext db, HttpContext http)
    {
        var (project, error) = await LoadOwnedProject(db, http, id);
        if (error is not null) return error;

        if (string.IsNullOrWhiteSpace(req.ToolKey)
            || string.IsNullOrWhiteSpace(req.InputsJson)
            || string.IsNullOrWhiteSpace(req.ResultJson)
            || string.IsNullOrWhiteSpace(req.EngineVersion)
            || req.SchemaVersion < 1)
        {
            return Results.BadRequest(new ApiError("MISSING_FIELDS"));
        }

        if (TooLong(req.ToolKey, ToolKeyMax, "toolKey", out var tooLong)
            || TooLong(req.ToolMode, ToolModeMax, "toolMode", out tooLong)
            || TooLong(req.EngineVersion, EngineVersionMax, "engineVersion", out tooLong))
        {
            return tooLong!;
        }

        // JSON alanları yazılırken ayrıştırılmaz ama en azından GEÇERLİ JSON
        // olduğu doğrulanır. Bozuk içerik eskiden sessizce kabul ediliyor,
        // aylar sonra StoredSection.Read'te null'a düşüp o satırı her rapordan
        // kalıcı ve sessiz şekilde düşürüyordu — kullanıcıya hiçbir işaret
        // gitmeden. Hata anında ve yazana dönmeli.
        if (BadJson(req.InputsJson, "inputsJson", out var badJson)
            || BadJson(req.ResultJson, "resultJson", out badJson)
            || BadJson(req.ReportJson, "reportJson", out badJson))
        {
            return badJson!;
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
        project!.UpdatedAt = now;
        await db.SaveChangesAsync();

        return Results.Created($"/api/calculations/{calculation.Id}", ToDto(calculation));
    }

    // Kaydedilmiş bir hesabı araç ekranına geri yüklemek için tek kayıt okuma.
    // Proje detayı üzerinden de okunabilirdi ama araç ekranı yalnızca `?hesap=`
    // parametresindeki kimliği bilir — üst projeyi bilmediği için o yolu
    // kullanamaz. Sahiplik yine Project.UserId üzerinden doğrulanır.
    internal static async Task<IResult> GetCalculation(Guid id, AppDbContext db, HttpContext http)
    {
        var (calculation, error) = await LoadOwnedCalculation(db, http, id);
        if (error is not null) return error;

        return Results.Ok(new CalculationDetailResponse(
            ToDto(calculation!), calculation!.Project!.Id, calculation.Project.Name));
    }

    internal static async Task<IResult> UpdateCalculation(Guid id, UpdateCalculationRequest req, AppDbContext db, HttpContext http)
    {
        var (calculation, error) = await LoadOwnedCalculation(db, http, id);
        if (error is not null) return error;

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

        // CreateCalculation ile aynı uzunluk ve JSON sözleşmesi — PATCH bu
        // kapıların yan yolu olamaz.
        if (TooLong(req.ToolMode, ToolModeMax, "toolMode", out var tooLong)
            || TooLong(req.EngineVersion, EngineVersionMax, "engineVersion", out tooLong))
        {
            return tooLong!;
        }

        if (BadJson(req.InputsJson, "inputsJson", out var badJson)
            || BadJson(req.ResultJson, "resultJson", out badJson)
            || BadJson(req.ReportJson, "reportJson", out badJson))
        {
            return badJson!;
        }

        var changed = false;

        if (req.ToolMode is not null) { calculation!.ToolMode = req.ToolMode; changed = true; }
        if (req.InputsJson is not null) { calculation!.InputsJson = req.InputsJson; changed = true; }
        if (req.ResultJson is not null) { calculation!.ResultJson = req.ResultJson; changed = true; }
        if (req.ReportJson is not null) { calculation!.ReportJson = req.ReportJson; changed = true; }
        if (req.EngineVersion is not null) { calculation!.EngineVersion = req.EngineVersion; changed = true; }
        if (req.SchemaVersion is not null) { calculation!.SchemaVersion = req.SchemaVersion.Value; changed = true; }

        if (changed)
        {
            var now = DateTimeOffset.UtcNow;
            calculation!.UpdatedAt = now;
            calculation.Project!.UpdatedAt = now;
        }

        await db.SaveChangesAsync();

        return Results.Ok(ToDto(calculation!));
    }

    internal static async Task<IResult> DeleteCalculation(Guid id, AppDbContext db, HttpContext http)
    {
        var userId = RequireUserId(http, out var error);
        if (error is not null) return error!;

        // Silmek için satırın İÇERİĞİ gerekmez: LoadOwnedCalculation tam
        // varlığı (ReportJson + gömülü SVG, 2 MB'a dek) belleğe çekiyordu.
        // Sahiplik yalnızca kimliklerle doğrulanır; 404 şekli diğer uçlarla
        // birebir aynı kalır (yok / başkasının — ayrım sızmaz).
        var owned = await db.Calculations
            .Where(c => c.Id == id && c.Project!.UserId == userId)
            .Select(c => new { c.Id, c.ProjectId })
            .FirstOrDefaultAsync();
        if (owned is null) return Results.NotFound(new ApiError("CALCULATION_NOT_FOUND"));

        // `UtcNow` sorgu içinde çağrılmaz: SQLite sağlayıcısı (testler) onu
        // çeviremiyor; değer önce yakalanır, sorguya sabit girer.
        var now = DateTimeOffset.UtcNow;
        await db.Calculations.Where(c => c.Id == owned.Id).ExecuteDeleteAsync();
        await db.Projects
            .Where(p => p.Id == owned.ProjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, now));

        return Results.NoContent();
    }

    // `internal`: küme-eşitliği kontrolü bir güvenlik kuralıdır (yabancı hesap
    // kimliği sızamaz) ve doğrudan test edilir — GetProject'teki kalıpla aynı.
    internal static async Task<IResult> ReorderCalculations(Guid id, ReorderCalculationsRequest req, AppDbContext db, HttpContext http)
    {
        var (project, error) = await LoadOwnedProject(db, http, id);
        if (error is not null) return error;

        if (req.OrderedIds is null) return Results.BadRequest(new ApiError("MISSING_FIELDS"));

        // Yalnızca kimlikler çekilir: eskiden tam `Calculation` varlıkları
        // (ReportJson + gömülü SVG dahil) belleğe geliyordu, oysa değişen tek
        // alan `SortOrder`. Sıralama, satır içeriğine hiç dokunmadan yapılabilir.
        var currentIds = await db.Calculations
            .Where(c => c.ProjectId == id)
            .Select(c => c.Id)
            .ToListAsync();

        // Verilen kimlik kümesi bu projenin mevcut hesap kümesiyle TAM olarak
        // eşleşmeli — ne eksik ne fazla, tekrar da yok. Bu kontrol istemciye
        // güvenmez: başka bir projenin hesap kimliğinin sızması ya da bir
        // satırın sessizce düşürülmesi burada engellenir.
        var givenIds = new HashSet<Guid>(req.OrderedIds);
        if (req.OrderedIds.Count != currentIds.Count
            || givenIds.Count != req.OrderedIds.Count
            || !givenIds.SetEquals(currentIds))
        {
            return Results.BadRequest(new ApiError("INVALID_ORDER"));
        }

        // Her satır için yalnızca `SortOrder` kolonunu yazan bir stub eklenir;
        // öbür kolonlar `IsModified` olmadığı için UPDATE'e girmez, dolayısıyla
        // InputsJson/ResultJson/ReportJson okunmadan ve yeniden yazılmadan kalır.
        for (var index = 0; index < req.OrderedIds.Count; index++)
        {
            var stub = new Calculation { Id = req.OrderedIds[index], SortOrder = index };
            db.Calculations.Attach(stub);
            db.Entry(stub).Property(c => c.SortOrder).IsModified = true;
        }

        project!.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return Results.Ok(new OkResponse(true));
    }

    // ---- Girdi doğrulama yardımcıları ----

    // `value` null ise alan gönderilmemiştir ve sınır uygulanmaz (PATCH
    // sözleşmesi). Hata gövdesi UpdateMe'deki TOO_LONG şekliyle birebir aynı.
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

    // Yalnızca sözdizimi doğrulanır, şema değil — içerik istemcinin motoruna
    // aittir ve sunucu onu yorumlamaz. `JsonDocument.Parse` DOM'u kurar ve
    // atar; 2 MB'lık gövdede maliyeti yazma isteğinin kendisinden küçüktür.
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

    private static CalculationDto ToDto(Calculation c) => new(
        c.Id, c.ToolKey, c.ToolMode, c.SortOrder, c.InputsJson, c.ResultJson,
        c.EngineVersion, c.SchemaVersion, c.CreatedAt, c.UpdatedAt);

    private static string? CurrentUserId(HttpContext http) =>
        http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;

    // ---- Sahiplik yardımcıları ----
    //
    // Her uçta tekrarlanan iki desen buraya iner: (1) kimlik oku, yoksa 401;
    // (2) kaynağı yükle, yok/başkasının ise AYNI 404. İkinci nokta güvenlik
    // sözleşmesidir (numaralandırma karşıtı) — 11. uç eklenip bu adım
    // unutulursa test yakalamaz, o yüzden tek yerde durur.
    //
    // Dönüş `(entity, error)`: `error` doluysa uç onu doğrudan döndürür,
    // `entity` doluysa iş mantığı onu kullanır. C#'ta `out`+bool yerine tuple:
    // çağıran taraf tek satırda ayrıştırır ve entity'nin null-olmadığı hemen
    // görünür.

    private static string? RequireUserId(HttpContext http, out IResult? error)
    {
        var userId = CurrentUserId(http);
        error = userId is null ? Results.Unauthorized() : null;
        return userId;
    }

    private static async Task<(Project? Project, IResult? Error)> LoadOwnedProject(
        AppDbContext db, HttpContext http, Guid id)
    {
        var userId = RequireUserId(http, out var error);
        if (error is not null) return (null, error);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id);
        // Proje yok / başka kullanıcıya ait — AYNI 404 şekli döner, hangisi
        // olduğunu dışarı sızdırmaz.
        return project is null || project.UserId != userId
            ? (null, Results.NotFound(new ApiError("PROJECT_NOT_FOUND")))
            : (project, null);
    }

    private static async Task<(Calculation? Calculation, IResult? Error)> LoadOwnedCalculation(
        AppDbContext db, HttpContext http, Guid id)
    {
        var userId = RequireUserId(http, out var error);
        if (error is not null) return (null, error);

        // Calculation'ın kendi UserId'si yok — sahiplik üst Project üzerinden
        // dolaylı doğrulanır, o yüzden Project include edilir.
        var calculation = await db.Calculations.Include(c => c.Project).FirstOrDefaultAsync(c => c.Id == id);
        return calculation is null || calculation.Project is null || calculation.Project.UserId != userId
            ? (null, Results.NotFound(new ApiError("CALCULATION_NOT_FOUND")))
            : (calculation, null);
    }
}
