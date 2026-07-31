namespace Alp.Api.Auth;

// Kimlik akışının postaları — konu, gövde ve bağlantı yolu, iki dilde.
//
// BİLİNÇLİ KURAL İSTİSNASI. CLAUDE.md "sunucu kullanıcı metni tanımaz" der ve
// rapor çerçevesi bu yüzden yükle birlikte gider (`reportLabels`). Postalarda
// aynı yol izlenemez: gövdeyi istemci belirlerse kayıt ve parola sıfırlama
// uçları, KENDİ alan adımızdan çıkan ve markamızı taşıyan serbest metni
// istenen adrese gönderen bir kimlik avı yüzeyine döner. Rapor çerçevesinde
// böyle bir risk yok — o metin yalnızca isteği yapanın indirdiği dosyaya
// girer, üçüncü bir tarafa postalanmaz. İstemciden gelen tek şey DİL KODUDUR.
// Gerekçenin tamamı: docs/eposta-dili-karari.md §2.
internal static class AuthEmailText
{
    // Marka adı çevrilmez.
    private const string Brand = "ALP PCB Toolkit";

    public const string DefaultLang = "tr";

    // Bağlantı yolları. Bu tablo istemcideki `web/src/lib/routes.js`
    // (`STATIC_ROUTES`) sözlüğünün İKİNCİ kopyasıdır ve ayrıştığı gün postadaki
    // bağlantı 404'e gider — kullanıcı hesabını doğrulayamaz. Kopyayı
    // `web/src/lib/authMailPaths.guard.test.js` bekler: bu dosyayı metin olarak
    // okur, yolları çıkarır ve `staticPath` ile karşılaştırır.
    private static readonly Dictionary<string, string> ConfirmEmailPaths = new()
    {
        ["tr"] = "/e-posta-dogrula",
        ["en"] = "/en/confirm-email",
    };

    private static readonly Dictionary<string, string> ResetPasswordPaths = new()
    {
        ["tr"] = "/parola-sifirla",
        ["en"] = "/en/reset-password",
    };

    // Tanınmayan, boş ya da null dil Türkçeye düşer (istemcideki DEFAULT_LANG
    // ile aynı kural). Bölge eki kabul edilir: dil kodu tarayıcıdan geçerken
    // `en-US` biçimine girebiliyor.
    public static string Normalize(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return DefaultLang;
        var primary = lang.Trim().Split('-')[0].ToLowerInvariant();
        return primary == "en" ? "en" : DefaultLang;
    }

    public static string ConfirmEmailPath(string lang) => ConfirmEmailPaths[Normalize(lang)];

    public static string ResetPasswordPath(string lang) => ResetPasswordPaths[Normalize(lang)];

    public static string DuplicateRegistrationSubject(string lang) =>
        Normalize(lang) == "en" ? "Registration attempt" : "Kayıt denemesi";

    public static string DuplicateRegistrationBody(string lang) =>
        Normalize(lang) == "en"
            ? $"Someone tried to open a new account on {Brand} with this e-mail address. "
              + "You already have an account — if this was not you, you can ignore this message. "
              + "If you forgot your password, use the password reset page."
            : $"Bu e-posta adresiyle {Brand} üzerinde yeni bir hesap açılmaya "
              + "çalışıldı. Zaten bir hesabın var — bu sen değilsen görmezden gelebilirsin. "
              + "Parolanı unuttuysan parola sıfırlama sayfasını kullan.";

    public static string ConfirmEmailSubject(string lang) =>
        Normalize(lang) == "en" ? "Confirm your e-mail address" : "E-posta adresini doğrula";

    public static string ConfirmEmailBody(string lang, string link) =>
        Normalize(lang) == "en"
            ? $"To confirm your account: <a href=\"{link}\">{link}</a>"
            : $"Hesabını doğrulamak için: <a href=\"{link}\">{link}</a>";

    public static string ResetPasswordSubject(string lang) =>
        Normalize(lang) == "en" ? "Password reset" : "Parola sıfırlama";

    public static string ResetPasswordBody(string lang, string link) =>
        Normalize(lang) == "en"
            ? $"To reset your password: <a href=\"{link}\">{link}</a>"
            : $"Parolanı sıfırlamak için: <a href=\"{link}\">{link}</a>";
}
