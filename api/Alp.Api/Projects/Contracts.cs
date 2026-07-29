namespace Alp.Api.Projects;

// Proje özeti — liste ve oluşturma/güncelleme yanıtlarında aynı şekil kullanılır.
public record ProjectSummary(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int CalculationCount
);

public record ProjectListResponse(IReadOnlyList<ProjectSummary> Projects);

public record CreateProjectRequest(string Name, string? Description);

// `Name`/`Description` null ya da atlanmışsa değişmez; `Description` boş dize
// olarak GÖNDERİLMİŞSE alan temizlenir (null'a döner). `Name` gönderilmişse
// trim sonrası boş olamaz.
public record UpdateProjectRequest(string? Name, string? Description);

// InputsJson/ResultJson/ReportJson sunucu için opak dizedir — burada asla
// ayrıştırılmaz/yeniden gömülmez. Sunucu hiçbir aracın içeriğini bilmez.
public record CalculationDto(
    Guid Id,
    string ToolKey,
    string? ToolMode,
    int SortOrder,
    string InputsJson,
    string ResultJson,
    string? ReportJson,
    string EngineVersion,
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

// Tekil hesap yanıtı. Araç ekranı kaydı geri yüklerken hangi projeye ait
// olduğunu da göstermek ister; ikinci bir proje isteği attırmamak için üst
// projenin kimliği ve adı yanıta katılır.
public record CalculationDetailResponse(
    CalculationDto Calculation,
    Guid ProjectId,
    string ProjectName
);

public record ProjectDetailResponse(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CalculationDto> Calculations
);

public record CreateCalculationRequest(
    string ToolKey,
    string? ToolMode,
    string InputsJson,
    string ResultJson,
    string? ReportJson,
    string EngineVersion,
    int SchemaVersion
);

// Sağlanan her alan TAM DEĞİŞİM'dir (birleştirme değil); null/atlanmış alan
// değişmeden kalır.
public record UpdateCalculationRequest(
    string? ToolMode,
    string? InputsJson,
    string? ResultJson,
    string? ReportJson,
    string? EngineVersion,
    int? SchemaVersion
);

public record ReorderCalculationsRequest(IReadOnlyList<Guid> OrderedIds);

public record OkResponse(bool Ok);
