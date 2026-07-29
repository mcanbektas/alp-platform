namespace Alp.Domain;

// localStorage'daki bakır kalınlığı kayıtlarının hesaba taşınmış hâli.
// src/lib/thicknessRecords.js ile aynı şema; DataJson o dosyadaki
// doğrulanmış kayıt yapısını olduğu gibi taşır.
public class ThicknessRecord
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public string DataJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}
