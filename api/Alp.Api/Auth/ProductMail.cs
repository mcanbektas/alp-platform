namespace Alp.Api.Auth;

// Kimlik postalarının ürün başına markası, ön yüz adresi ve bağlantı yolları.
// CLAUDE.md "Bilinen borç" notundaki plan: eskiden AuthEmailText içinde PCB'ye
// sabitlenmiş TEK tablo vardı, Comm eklenince yetmedi — "auth mail yolları
// ürün başına yapılandırmaya taşınacak" (Faz 3).
//
// Değerler appsettings App:Products:<ürün> altından okunur. Hiçbiri
// verilmemişse PCB için ESKİ davranışla birebir aynı sonuca düşülür (önce
// App:FrontendBaseUrl, sonra localhost:3000) — mevcut üretim yapılandırması
// hiç değişmeden çalışmaya devam eder. Comm için karşılığı yoksa makul
// varsayılana (localhost:3001, kendi rota adları) düşülür; kırık bağlantı
// üretmez ama Comm'un gerçek rotaları netleşince appsettings'ten override
// edilmesi beklenir.
internal static class ProductMail
{
    public const string Pcb = "pcb";
    public const string Comm = "comm";

    public static string NormalizeProduct(string? product) =>
        string.Equals(product?.Trim(), Comm, StringComparison.OrdinalIgnoreCase) ? Comm : Pcb;

    public sealed record Branding(
        string BaseUrl, string Brand, string ConfirmEmailPath, string ResetPasswordPath, string UnlockAccountPath);

    private static readonly Dictionary<string, string> DefaultBrand = new()
    {
        [Pcb] = "ALP PCB Toolkit",
        [Comm] = "ALP Comm Toolkit",
    };

    private static readonly Dictionary<string, string> DefaultBaseUrl = new()
    {
        [Pcb] = "http://localhost:3000",
        [Comm] = "http://localhost:3001",
    };

    // Yol tablolarının varsayılanı — PCB'deki üç eski sabit dizi (eskiden
    // AuthEmailText'teydi) buraya taşındı; Comm için aynı yol adları
    // varsayıldı, kendi SPA'sı kurulunca appsettings'ten değişir.
    private static readonly Dictionary<string, Dictionary<string, string>> DefaultConfirmEmailPath = new()
    {
        [Pcb] = new() { ["tr"] = "/e-posta-dogrula", ["en"] = "/en/confirm-email" },
        [Comm] = new() { ["tr"] = "/e-posta-dogrula", ["en"] = "/en/confirm-email" },
    };

    private static readonly Dictionary<string, Dictionary<string, string>> DefaultResetPasswordPath = new()
    {
        [Pcb] = new() { ["tr"] = "/parola-sifirla", ["en"] = "/en/reset-password" },
        [Comm] = new() { ["tr"] = "/parola-sifirla", ["en"] = "/en/reset-password" },
    };

    private static readonly Dictionary<string, Dictionary<string, string>> DefaultUnlockAccountPath = new()
    {
        [Pcb] = new() { ["tr"] = "/kilit-ac", ["en"] = "/en/unlock-account" },
        [Comm] = new() { ["tr"] = "/kilit-ac", ["en"] = "/en/unlock-account" },
    };

    public static Branding Resolve(IConfiguration config, string? product, string lang)
    {
        var key = NormalizeProduct(product);
        // Savunmacı: çağıranların hepsi zaten AuthEmailText.Normalize'dan
        // geçirilmiş `lang` verir, ama sözlük araması KeyNotFoundException'a
        // düşmesin diye burada da normalize edilir.
        lang = AuthEmailText.Normalize(lang);
        var section = config.GetSection($"App:Products:{key}");

        // PCB'nin ön yüz adresi eskiden TEK kaynaktan geliyordu
        // (App:FrontendBaseUrl, hem CORS hem posta bağlantıları). Yeni
        // App:Products:pcb:FrontendBaseUrl verilmişse ona öncelik tanınır,
        // yoksa eski anahtar aynen okunur — davranış değişmez.
        var legacyPcbUrl = key == Pcb ? NonEmpty(config["App:FrontendBaseUrl"]) : null;
        var baseUrl = NonEmpty(section["FrontendBaseUrl"]) ?? legacyPcbUrl ?? DefaultBaseUrl[key];
        var brand = NonEmpty(section["Brand"]) ?? DefaultBrand[key];
        var confirmPath = NonEmpty(section[$"ConfirmEmailPath:{lang}"]) ?? DefaultConfirmEmailPath[key][lang];
        var resetPath = NonEmpty(section[$"ResetPasswordPath:{lang}"]) ?? DefaultResetPasswordPath[key][lang];
        var unlockPath = NonEmpty(section[$"UnlockAccountPath:{lang}"]) ?? DefaultUnlockAccountPath[key][lang];

        return new Branding(baseUrl, brand, confirmPath, resetPath, unlockPath);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
