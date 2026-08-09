namespace Alp.Api.Comm;

public record CommProjectSummary(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int SchemaCount
);

public record CommProjectListResponse(IReadOnlyList<CommProjectSummary> Projects);

public record CreateCommProjectRequest(string Name, string? Description);

// `Name`/`Description` null ya da atlanmışsa değişmez; `Description` boş dize
// olarak GÖNDERİLMİŞSE alan temizlenir (null'a döner). `Name` gönderilmişse
// trim sonrası boş olamaz — ProjectEndpoints'teki kuralla aynı.
public record UpdateCommProjectRequest(string? Name, string? Description);

public record ProtocolSchemaSummary(
    Guid Id,
    string Name,
    string Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

// `DefinitionJson` sunucu için opak dizedir — yalnızca sözdizimi doğrulanır,
// içerik yorumlanmaz (protocol-core'un alanı, bkz. alp-comm-toolkit).
public record ProtocolSchemaDto(
    Guid Id,
    string Name,
    string Version,
    string DefinitionJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record CommProjectDetailResponse(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ProtocolSchemaSummary> Schemas
);

public record CreateProtocolSchemaRequest(string Name, string Version, string DefinitionJson);

// Sağlanan her alan TAM DEĞİŞİM'dir (birleştirme değil); null/atlanmış alan
// değişmeden kalır — UpdateCalculationRequest'teki kuralla aynı.
public record UpdateProtocolSchemaRequest(string? Name, string? Version, string? DefinitionJson);

public record ProtocolSchemaDetailResponse(
    ProtocolSchemaDto Schema,
    Guid CommProjectId,
    string CommProjectName
);
