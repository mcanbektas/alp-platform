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
    public const string DefaultLang = "tr";

    // Bağlantı yolları, marka adı ve ön yüz adresi artık ürün başına
    // ProductMail'de çözülür (Faz 3: "auth mail yolları ürün başına
    // yapılandırmaya taşınacak", CLAUDE.md). Bu sınıfta yalnızca dil
    // normalizasyonu ve konu/gövde METNİ kalır — hangi ürün için üretildiği
    // çağıran tarafından `brand` parametresiyle verilir.

    // Tanınmayan, boş ya da null dil Türkçeye düşer (istemcideki DEFAULT_LANG
    // ile aynı kural). Bölge eki kabul edilir: dil kodu tarayıcıdan geçerken
    // `en-US` biçimine girebiliyor.
    public static string Normalize(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return DefaultLang;
        var primary = lang.Trim().Split('-')[0].ToLowerInvariant();
        return primary == "en" ? "en" : DefaultLang;
    }

    public static string DuplicateRegistrationSubject(string lang) =>
        Normalize(lang) == "en" ? "Registration attempt" : "Kayıt denemesi";

    public static string DuplicateRegistrationBody(string lang, string brand) =>
        Normalize(lang) == "en"
            ? $"Someone tried to open a new account on {brand} with this e-mail address. "
              + "You already have an account — if this was not you, you can ignore this message. "
              + "If you forgot your password, use the password reset page."
            : $"Bu e-posta adresiyle {brand} üzerinde yeni bir hesap açılmaya "
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

    public static string AccountLockedSubject(string lang) =>
        Normalize(lang) == "en" ? "Your account has been locked" : "Hesabın kilitlendi";

    // Kilitlenme anında giden bilgilendirme. İki bağlantı taşır ve ikisinin
    // FARKI açıkça yazılır: kilidi açmak parolayı korur, sıfırlamak değiştirir.
    // Denemeleri yapan gerçekten kullanıcının kendisiyse ikisi de gereksizdir —
    // kilit zaten kendiliğinden açılacak — ve bu da söylenir, yoksa posta
    // gereksiz bir parola değişimine itekler.
    public static string AccountLockedBody(
        string lang, string brand, int attempts, int minutes, string unlockLink, string resetLink) =>
        Normalize(lang) == "en"
            ? $"Your {brand} account was locked after {attempts} failed sign-in attempts. "
              + $"It unlocks by itself in {minutes} minutes — if those attempts were yours, "
              + "you do not have to do anything.<br><br>"
              + "If they were not, use one of these links:<br>"
              + $"Unlock now, keeping your current password: <a href=\"{unlockLink}\">{unlockLink}</a><br>"
              + $"Reset your password: <a href=\"{resetLink}\">{resetLink}</a>"
            : $"{brand} hesabın {attempts} başarısız giriş denemesinden sonra kilitlendi. "
              + $"Kilit {minutes} dakika içinde kendiliğinden açılır — bu denemeleri sen "
              + "yaptıysan bir şey yapmana gerek yok.<br><br>"
              + "Sen değilsen bağlantılardan birini kullan:<br>"
              + $"Kilidi hemen aç, parolan değişmez: <a href=\"{unlockLink}\">{unlockLink}</a><br>"
              + $"Parolanı sıfırla: <a href=\"{resetLink}\">{resetLink}</a>";
}
