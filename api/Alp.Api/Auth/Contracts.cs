namespace Alp.Api.Auth;

// `Lang` isteğe bağlıdır ve verilmezse Türkçedir: postanın dili, postayı
// tetikleyen isteğin — yani kullanıcının o an gördüğü arayüzün — dilidir.
// Alan gövdede taşınır; gerekçe ve elenen seçenekler (Accept-Language, kalıcı
// hesap dili) docs/eposta-dili-karari.md §1. İstemci yalnızca DİL KODU
// gönderir, metni değil (§2).
public record RegisterRequest(string Email, string Password, string DisplayName, string? Lang = null);
// RememberMe isteğe bağlıdır ve varsayılanı false: alan gönderilmezse oturum
// tarayıcı kapanınca biter. Eski istemci gövdesi bu yüzden kırılmaz.
//
// `Lang` giriş isteğinde de var, çünkü giriş de posta ÜRETEBİLİR: eşiği aşan
// deneme hesabı kilitler ve kullanıcıya iki bağlantılı bir bilgilendirme
// gider (AuthEndpoints.Login). Postanın dili başka hiçbir kaynaktan
// okunamaz — kayıt/parola sıfırlama ile aynı kural (docs/eposta-dili-karari.md §1).
public record LoginRequest(string Email, string Password, bool RememberMe = false, string? Lang = null);
public record ForgotPasswordRequest(string Email, string? Lang = null);
// Doğrulama postası kaybolduğunda kendi kendine kurtarma. Bu uç olmadan
// akış çıkmaz sokaktı: RequireConfirmedEmail açıkken giriş yapılamıyor,
// ForgotPassword da doğrulanmış e-posta şartı koşuyor.
public record ResendConfirmationRequest(string Email, string? Lang = null);
// Oturum açmış kullanıcının kendi parolasını değiştirmesi. Mevcut parola
// ZORUNLUDUR: erişim token'ı ele geçirilmiş bir oturum, parolayı da
// değiştirip hesabı büsbütün devralamasın diye.
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
// Bir hesabın büsbütün silinmesi — YALNIZCA yönetim panelinden. Kullanıcının
// kendi hesabını silmesi kaldırıldı; silme talebi yasal metinlerdeki başvuru
// adresine gelir ve yönetici bu uçtan uygular.
//
// İsteyenin parolası aynı gerekçeyle ZORUNLUDUR ve burada bedeli daha ağırdır:
// parola değişimi geri alınabilir, silme alınamaz. Ele geçirilmiş bir yönetici
// oturumu, parolayı bilmeden kullanıcı silemesin.
public record AdminDeleteUserRequest(string CurrentPassword);
// Plan değişimi — DeleteUser'ın aksine parola İSTEMEZ: geri alınabilir, düşük
// riskli bir işlem (ödeme sistemi yok, admin elle yönetiyor). `Plan` yalnızca
// "free" ya da "pro" olabilir, uç doğrular.
public record AdminChangePlanRequest(string Plan);
public record ResetPasswordRequest(string Email, string Token, string NewPassword);
// Kilitlenme postasındaki "kilidi aç" bağlantısı. Oturum İSTEMEZ — kullanıcı
// zaten giremiyor, isteyebileceğimiz tek kanıt postaya giden token'dır.
// Kimlik `Email` ile değil `UserId` ile taşınır (ConfirmEmail'deki kalıp):
// bağlantı e-posta adresini URL'de gezdirmesin, posta iletildiğinde ya da
// tarayıcı geçmişinden okunduğunda adres açığa çıkmasın.
public record UnlockRequest(string UserId, string Token);

public record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt);
// E-posta yanıtta var ama DEĞİŞTİRİLEMEZ: `UpdateMeRequest` böyle bir alan
// taşımıyor, yani uç doğrudan çağrılsa bile kayıt e-postası kalıcıdır. Kimlik
// doğrulaması ve parola sıfırlama o adrese bağlı.
// `IsAdmin` arayüzün yönetim bağlantısını çizmesi içindir, yetki kararı değil —
// uçlar rolü kendileri doğrular (AdminEndpoints.RequireAdmin).
public record MeResponse(
    string Id, string Email, string DisplayName, string? Company, string Plan, bool IsAdmin);

// Yönetim panelindeki kullanıcı satırı. Parola özeti, güvenlik damgası ve
// token gibi hiçbir kimlik alanı BURAYA GİRMEZ: panel bir kullanıcı listesidir,
// kimlik deposunun kopyası değil.
// `LockoutEnd` ham hâliyle taşınır (Identity'nin kendi alanı, `IdentityUser`den
// miras) — türetilmiş bir `IsLockedOut` bool DEĞİL: panel yalnızca "kilitli mi"
// değil "ne zaman açılacak" bilgisini de göstermek istiyor, iki alan yerine tek
// alan yeter (istemci `lockoutEnd > şu an` ile kilitliyi kendi türetir; bkz.
// AuthEndpoints.Login → `userManager.IsLockedOutAsync` aynı karşılaştırmayı yapar).
public record AdminUserRow(
    string Id,
    string Email,
    string DisplayName,
    string? Company,
    string Plan,
    bool EmailConfirmed,
    DateTimeOffset? LockoutEnd,
    bool IsAdmin,
    DateTimeOffset CreatedAt,
    int ProjectCount,
    int ReportCount);

public record AdminUserPage(IReadOnlyList<AdminUserRow> Items, int Total, int Page, int PageSize);

// Denetim izi satırı — panel görünümü. `Event` ham koddur, cümleyi istemci
// sözlüğü kurar. `DetailJson` yapısal ve dilsizdir (docs/brifler/11-loglama.md
// §3); panel onu event'e göre kendi şablonuyla okur.
public record AuditEventRow(
    long Id,
    DateTimeOffset OccurredAt,
    string Event,
    string? ActorEmail,
    string? TargetEmail,
    string? DetailJson);

public record AuditPage(IReadOnlyList<AuditEventRow> Items, int Total, int Page, int PageSize);

// Verilmeyen alan DEĞİŞMEZ. `Company` boş dize olarak GÖNDERİLMİŞSE alan
// temizlenir (null'a döner) — proje güncellemesindeki kuralın aynısı.
// `DisplayName` gönderilmişse trim sonrası boş olamaz: rapordaki "Hazırlayan"
// varsayılanı odur, boşalırsa kullanıcı her raporda elle doldurmak zorunda kalır.
public record UpdateMeRequest(string? DisplayName, string? Company);
