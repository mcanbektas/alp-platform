namespace Alp.Api.Auth;

public record RegisterRequest(string Email, string Password, string DisplayName);
// RememberMe isteğe bağlıdır ve varsayılanı false: alan gönderilmezse oturum
// tarayıcı kapanınca biter. Eski istemci gövdesi bu yüzden kırılmaz.
public record LoginRequest(string Email, string Password, bool RememberMe = false);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Email, string Token, string NewPassword);

public record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt);
// `HasLogo`: logonun kendisi bu yanıtta taşınmaz (her sayfa yüklemesinde
// yüzlerce KB'lık bayt demek olurdu). Arayüz bayrağa bakıp `GET /api/me/logo`
// adresini gösterir ya da hiç göstermez.
public record MeResponse(string Id, string Email, string DisplayName, string? Company, string Plan, bool HasLogo);

// Verilmeyen alan DEĞİŞMEZ. `Company` boş dize olarak GÖNDERİLMİŞSE alan
// temizlenir (null'a döner) — proje güncellemesindeki kuralın aynısı.
// `DisplayName` gönderilmişse trim sonrası boş olamaz: rapordaki "Hazırlayan"
// varsayılanı odur, boşalırsa kullanıcı her raporda elle doldurmak zorunda kalır.
public record UpdateMeRequest(string? DisplayName, string? Company);
