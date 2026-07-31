namespace Alp.Api.Auth;

public record RegisterRequest(string Email, string Password, string DisplayName);
// RememberMe isteğe bağlıdır ve varsayılanı false: alan gönderilmezse oturum
// tarayıcı kapanınca biter. Eski istemci gövdesi bu yüzden kırılmaz.
public record LoginRequest(string Email, string Password, bool RememberMe = false);
public record ForgotPasswordRequest(string Email);
// Doğrulama postası kaybolduğunda kendi kendine kurtarma. Bu uç olmadan
// akış çıkmaz sokaktı: RequireConfirmedEmail açıkken giriş yapılamıyor,
// ForgotPassword da doğrulanmış e-posta şartı koşuyor.
public record ResendConfirmationRequest(string Email);
// Oturum açmış kullanıcının kendi parolasını değiştirmesi. Mevcut parola
// ZORUNLUDUR: erişim token'ı ele geçirilmiş bir oturum, parolayı da
// değiştirip hesabı büsbütün devralamasın diye.
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record ResetPasswordRequest(string Email, string Token, string NewPassword);

public record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt);
// E-posta yanıtta var ama DEĞİŞTİRİLEMEZ: `UpdateMeRequest` böyle bir alan
// taşımıyor, yani uç doğrudan çağrılsa bile kayıt e-postası kalıcıdır. Kimlik
// doğrulaması ve parola sıfırlama o adrese bağlı.
public record MeResponse(string Id, string Email, string DisplayName, string? Company, string Plan);

// Verilmeyen alan DEĞİŞMEZ. `Company` boş dize olarak GÖNDERİLMİŞSE alan
// temizlenir (null'a döner) — proje güncellemesindeki kuralın aynısı.
// `DisplayName` gönderilmişse trim sonrası boş olamaz: rapordaki "Hazırlayan"
// varsayılanı odur, boşalırsa kullanıcı her raporda elle doldurmak zorunda kalır.
public record UpdateMeRequest(string? DisplayName, string? Company);
