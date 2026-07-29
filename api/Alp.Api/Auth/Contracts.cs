namespace Alp.Api.Auth;

public record RegisterRequest(string Email, string Password, string DisplayName);
public record LoginRequest(string Email, string Password);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Email, string Token, string NewPassword);

public record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt);
public record MeResponse(string Id, string Email, string DisplayName, string? Company, string Plan);
