using Microsoft.AspNetCore.Identity;

namespace Alp.Domain;

public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
    public string? Company { get; set; }
    public string? LogoPath { get; set; }

    // İleride abonelik eklenirse kullanılacak; bugün her zaman "free".
    // docs/uyelik-ve-rapor-plani.md §1 — ödeme kapsam dışı.
    public string Plan { get; set; } = "free";

    public DateTimeOffset CreatedAt { get; set; }
}
