namespace Alp.Domain;

// Bir CommProject altında saklanan tek protokol şeması. `DefinitionJson`
// katalogdaki alan/kayıt tanımını taşır (alp-comm-toolkit protocol-core ile
// aynı şekil) — sunucu içeriğini yorumlamaz, yalnızca geçerli JSON olduğunu
// doğrular (CommEndpoints.BadJson). Postgres'te jsonb olarak saklanır.
public class ProtocolSchema
{
    public const int NameMaxLength = 200;
    public const int VersionMaxLength = 50;

    public Guid Id { get; set; }
    public Guid CommProjectId { get; set; }
    public CommProject? CommProject { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
