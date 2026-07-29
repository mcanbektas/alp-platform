namespace Alp.Domain;

// Rotating refresh token: her yenilemede eskisi geçersizleşir.
// Yalnızca hash saklanır — çalınan bir veritabanı yedeği token'ı vermez.
public class RefreshToken
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByHash { get; set; }
    public string? CreatedByIp { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // "Beni kaydet" seçildi mi. Çerezin oturum çerezi mi (tarayıcı kapanınca
    // silinir) yoksa son kullanma tarihli mi yazılacağını belirler. Sunucuda
    // tutulur çünkü token her yenilemede döndürülür ve o anda isteğin ilk
    // girişte ne seçildiğini bilmesinin başka yolu yok — bilinmezse oturum
    // çerezi ilk yenilemede sessizce kalıcıya döner.
    public bool Persistent { get; set; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTimeOffset.UtcNow;
}
