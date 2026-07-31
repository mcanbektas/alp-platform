namespace Alp.Domain;

public class Project
{
    // Şema (HasMaxLength) ve uç doğrulaması (TOO_LONG) aynı sayıyı buradan
    // okur — ikisi ayrı yazılsaydı biri değişip öbürü unutulurdu.
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 2000;

    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Calculation> Calculations { get; set; } = [];
}
