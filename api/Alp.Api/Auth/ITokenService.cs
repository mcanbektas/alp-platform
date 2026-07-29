using Alp.Domain;

namespace Alp.Api.Auth;

public record AccessToken(string Value, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    AccessToken CreateAccessToken(ApplicationUser user);

    // Ham değeri çerez olarak taşınır, yalnızca özeti (hash) veritabanında saklanır —
    // veritabanı yedeği tek başına token'ı vermez.
    string CreateRefreshToken();

    string Hash(string rawToken);

    TimeSpan RefreshTokenLifetime { get; }
}
