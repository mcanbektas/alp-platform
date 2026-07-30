using Microsoft.AspNetCore.Identity;

namespace Alp.Domain;

public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
    public string? Company { get; set; }

    // Firma logosu veritabanında durur, diskte DEĞİL. Rapor belgeleri de
    // saklanmadığı için (bkz. ReportEndpoints) sunucuda ikinci bir dosya
    // yüzeyi açmanın karşılığı yok: logo küçük (≤ 512 KB), tek satır, yedeği
    // veritabanı yedeğiyle birlikte gelir ve kullanıcı silinince kaskatla
    // gider — yetim dosya kalmaz.
    //
    // `LogoContentType` yüklemede doğrulanan türdür (image/png | image/jpeg);
    // indirme yanıtında aynen kullanılır, istemcinin tahminine bırakılmaz.
    public byte[]? LogoBytes { get; set; }
    public string? LogoContentType { get; set; }

    // İleride abonelik eklenirse kullanılacak; bugün her zaman "free".
    // docs/uyelik-ve-rapor-plani.md §1 — ödeme kapsam dışı.
    public string Plan { get; set; } = "free";

    public DateTimeOffset CreatedAt { get; set; }
}
