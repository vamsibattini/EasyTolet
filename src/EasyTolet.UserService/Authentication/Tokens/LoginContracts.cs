using EasyTolet.UserService.Authentication;

namespace EasyTolet.UserService.Authentication.Tokens;

public sealed record AccessTokenResponse(string AccessToken, string TokenType, int ExpiresIn, DateTime ExpiresAtUtc);
public sealed record LoginResult(AccessTokenResponse Access, string RefreshToken, DateTime SessionExpiresAtUtc, long UserId);
public sealed record ApprovedAccess(IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);
public sealed record LoginSession(Guid Id, long UserId, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, string CredentialStamp);

public interface ILoginService
{
    Task<LoginResult> LoginAsync(string email, string password, CancellationToken ct);
    Task<LoginResult> LoginSsoAsync(SsoIdentity identity, CancellationToken ct);
    Task<LoginResult> RefreshAsync(long userId, string refreshToken, CancellationToken ct);
    Task LogoutAsync(long userId, string refreshToken, CancellationToken ct);
}

public interface ILoginSessionRepository
{
    Task<ApprovedAccess> GetAccessAsync(long userId, CancellationToken ct);
    Task CreateAsync(LoginSession session, string tokenHash, CancellationToken ct);
    Task<LoginSession?> GetSessionAsync(long userId, string tokenHash, CancellationToken ct);
    // Atomically consumes a token and creates its replacement. Replay revocation commits before returning null.
    Task<LoginSession?> RotateAsync(long userId, string tokenHash, string replacementHash, string credentialStamp, DateTime now, CancellationToken ct);
    Task RevokeAsync(long userId, string tokenHash, DateTime now, CancellationToken ct);
}

public interface IAccessTokenIssuer
{
    AccessTokenResponse Issue(long userId, string userIdCode, Guid sessionId, ApprovedAccess access);
}

public sealed record LoginUser(long Id, string UserId, string? PasswordHash, bool IsActive);
public interface ILoginUserRepository
{
    Task<LoginUser?> FindByEmailAsync(string email, CancellationToken ct);
    Task<LoginUser?> GetAsync(long id, CancellationToken ct);
}
