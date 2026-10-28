using System.Security.Cryptography;
using System.Text;
using EasyTolet.Shared.Exceptions;
using EasyTolet.UserService.Repositories;
using Microsoft.AspNetCore.WebUtilities;

namespace EasyTolet.UserService.Authentication.Tokens;

public sealed class LoginService(ISsoUserRepository users, ILoginUserRepository accounts, ILoginSessionRepository sessions,
    IAccessTokenIssuer issuer, TimeProvider clock) : ILoginService
{
    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320 || string.IsNullOrEmpty(password) || password.Length > 128)
            throw new ApiException("Invalid email or password.", 401);
        var user = await accounts.FindByEmailAsync(email.Trim(), ct);
        var validPassword = PasswordHashing.Verify(user?.PasswordHash, password);
        if (user is null || !validPassword || !user.IsActive) throw new ApiException("Invalid email or password.", 401);
        return await StartAsync(user, ct);
    }

    public async Task<LoginResult> LoginSsoAsync(SsoIdentity identity, CancellationToken ct)
    {
        var profile = await users.FindSsoAsync(identity, ct) ?? throw new ApiException("A registered SSO user is required.", 401);
        return await StartAsync(await UserAsync(profile.Id, ct), ct);
    }

    private async Task<LoginResult> StartAsync(LoginUser user, CancellationToken ct)
    {
        var access = await sessions.GetAccessAsync(user.Id, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var session = new LoginSession(Guid.NewGuid(), user.Id, now, now.AddHours(8), Hash(user.PasswordHash ?? "sso-only"));
        var refreshToken = NewToken();
        // Sign first: invalid signing configuration must not leave a committed unusable session.
        var token = issuer.Issue(user.Id, user.UserId, session.Id, access);
        await sessions.CreateAsync(session, Hash(refreshToken), ct);
        return new(token, refreshToken, session.ExpiresAtUtc, user.Id);
    }

    public async Task<LoginResult> RefreshAsync(long userId, string refreshToken, CancellationToken ct)
    {
        ValidateToken(refreshToken);
        var user = await UserAsync(userId, ct);
        var access = await sessions.GetAccessAsync(user.Id, ct);
        var replacement = NewToken();
        var existing = await sessions.GetSessionAsync(user.Id, Hash(refreshToken), ct)
            ?? throw new ApiException("The login session is invalid.", 401);
        // Signing happens before rotation; a signing failure leaves the current refresh token usable.
        var token = issuer.Issue(user.Id, user.UserId, existing.Id, access);
        var session = await sessions.RotateAsync(user.Id, Hash(refreshToken), Hash(replacement),
            Hash(user.PasswordHash ?? "sso-only"), clock.GetUtcNow().UtcDateTime, ct)
            ?? throw new ApiException("The login session is expired, revoked, or the refresh token was already used.", 401);
        return new(token, replacement, session.ExpiresAtUtc, user.Id);
    }

    public async Task LogoutAsync(long userId, string refreshToken, CancellationToken ct)
    {
        ValidateToken(refreshToken);
        // Logout may revoke an inactive user's session too.
        await sessions.RevokeAsync(userId, Hash(refreshToken), clock.GetUtcNow().UtcDateTime, ct);
    }

    private async Task<LoginUser> UserAsync(long userId, CancellationToken ct)
    {
        var user = await accounts.GetAsync(userId, ct) ?? throw new ApiException("A registered user is required.", 401);
        if (!user.IsActive) throw new ApiException("This user is inactive.", 403);
        return user;
    }
    private static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static void ValidateToken(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 86 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ApiException("A valid refresh token is required.", 401);
    }
}
