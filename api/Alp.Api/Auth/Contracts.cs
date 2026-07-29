namespace Alp.Api.Auth;

public record RegisterRequest(string Email, string Password, string DisplayName);
// RememberMe isteğe bağlıdır ve varsayılanı false: alan gönderilmezse oturum
// tarayıcı kapanınca biter. Eski istemci gövdesi bu yüzden kırılmaz.
public record LoginRequest(string Email, string Password, bool RememberMe = false);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Email, string Token, string NewPassword);

public record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt);
public record MeResponse(string Id, string Email, string DisplayName, string? Company, string Plan);
