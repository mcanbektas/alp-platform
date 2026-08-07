namespace Alp.Api.Auth;

// Hesap kilitlenmesinin eşiği ve süresi. Önceden Identity varsayılanları
// (5 deneme / 5 dk) kullanılıyordu ve hiçbir yerde yazmıyordu; ikisi de artık
// ortam başına ayarlanır — sıkı bir kurulum 3/10 ister, yoğun bir ortam
// başkasını.
//
// Okuma AuditCleanupService.RetentionDays ile BİREBİR aynı kalıptadır:
// ayrıştırılamayan ya da pozitif olmayan değer sessizce VARSAYILANA düşer.
// Fail-fast BİLİNÇLİ OLARAK seçilmedi (Jwt:Key ya da KnownProxyNetworks'ün
// aksine): yanlış yazılmış tek bir env satırı yüzünden uygulamanın hiç
// açılmaması, kilit eşiğinin varsayılanda kalmasından ağır bir arızadır —
// güvenlik açığı da doğurmaz, çünkü düşülen değer sıkı olandır.
//
// Sıfır ya da eksi eşik ayrıca Identity'de anlamsızdır: `MaxFailedAccessAttempts
// = 0` her başarısız denemede kilitler, negatif değer hiç kilitlemez. İkisi de
// sessizce elenir.
//
// Değerler Program.cs'te AddIdentityCore zincirine bağlanır. `internal`:
// yapılandırma okumasını test doğrudan çağırarak sınar — uçlardaki kalıpla aynı.
internal static class LockoutSettings
{
    public const string MaxAttemptsKey = "App:LockoutMaxAttempts";
    public const string MinutesKey = "App:LockoutMinutes";

    public const int DefaultMaxAttempts = 3;
    public const int DefaultMinutes = 10;

    public static int MaxAttempts(IConfiguration config) =>
        int.TryParse(config[MaxAttemptsKey], out var value) && value > 0 ? value : DefaultMaxAttempts;

    public static int Minutes(IConfiguration config) =>
        int.TryParse(config[MinutesKey], out var value) && value > 0 ? value : DefaultMinutes;

    public static TimeSpan Duration(IConfiguration config) => TimeSpan.FromMinutes(Minutes(config));
}
