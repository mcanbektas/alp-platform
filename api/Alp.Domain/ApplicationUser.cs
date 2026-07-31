using Microsoft.AspNetCore.Identity;

namespace Alp.Domain;

public class ApplicationUser : IdentityUser
{
    // Uzunluk sınırı burada durur, uç noktalarda değil: iki ayrı uç doğruluyor
    // (profil güncelleme ve proje raporu künyesi) ve ayrı ayrı yazılsalardı
    // biri değişince öteki sessizce geride kalırdı. Report/Project'teki desenin
    // aynısı.
    public const int CompanyMaxLength = 120;

    public string DisplayName { get; set; } = string.Empty;
    public string? Company { get; set; }

    // KULLANILMIYOR. Firma logosu özelliği kaldırıldı: yükleme ekranı, üç uç
    // (`/api/me/logo`) ve raporun kullanıcı logosuna geçme davranışı düştü —
    // rapor başlığında artık daima uygulamanın kendi logosu var, firma yalnızca
    // künyede metin olarak görünüyor. Karar ve gerekçe:
    // docs/uyelik-ve-rapor-plani.md §26.
    //
    // Sütunlar bilinçle duruyor: düşürmek geri alınamaz bir migration ve şu an
    // hiçbir okuyucusu olmayan iki sütunun bedeli yok. Özellik geri istenirse
    // veri kaybı olmadan dönülür; kalıcı olarak silinmesine karar verilirse tek
    // bir migration yeter.
    public byte[]? LogoBytes { get; set; }
    public string? LogoContentType { get; set; }

    // İleride abonelik eklenirse kullanılacak; bugün her zaman "free".
    // docs/uyelik-ve-rapor-plani.md §1 — ödeme kapsam dışı.
    public string Plan { get; set; } = "free";

    public DateTimeOffset CreatedAt { get; set; }
}
