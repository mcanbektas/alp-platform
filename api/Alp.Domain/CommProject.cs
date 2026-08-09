namespace Alp.Domain;

// Comm modülünün proje kapsayıcısı — Project (PCB) ile aynı sahiplik deseni,
// ayrı tablo: modüller birbirini çağırmaz, aynı şekli paylaşsalar bile
// (CLAUDE.md "Ürün modülü kuralları").
public class CommProject
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 2000;

    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<ProtocolSchema> ProtocolSchemas { get; set; } = [];
}
