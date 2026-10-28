using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using EasyTolet.Shared.Exceptions;
using EasyTolet.UserService.Authentication;
using EasyTolet.UserService.Authentication.Tokens;
using EasyTolet.UserService.Contracts;
using EasyTolet.UserService.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EasyTolet.UserService.Tests.Authentication;

public sealed class LoginServiceTests
{
    private const string Password = "Test-only-password-123!";
    private static readonly string PasswordHash = PasswordHashing.Hash(Password);
    private readonly Clock _clock = new();
    private readonly Accounts _accounts = new();
    private readonly Sessions _sessions = new();
    private readonly Issuer _issuer = new();
    private LoginService Service() => new(new SsoUsers(), _accounts, _sessions, _issuer, _clock);

    [Fact]
    public async Task PasswordLoginHashesRefreshTokenAndReturnsOnlyApprovedClaimsToIssuer()
    {
        var result = await Service().LoginAsync("user@example.com", Password, default);
        Assert.Equal(86, result.RefreshToken.Length);
        Assert.Equal(LoginService.Hash(result.RefreshToken), _sessions.TokenHash);
        Assert.DoesNotContain(result.RefreshToken, _sessions.TokenHash!);
        Assert.Equal(_clock.Now.AddHours(8).UtcDateTime, _sessions.Session!.ExpiresAtUtc);
        Assert.Equal(new[] { "TENANT" }, _issuer.Access!.Roles);
        Assert.Equal(new[] { "properties.read" }, _issuer.Access.Permissions);
    }

    [Theory]
    [InlineData("wrong-password", true, true)]
    [InlineData(Password, false, true)]
    [InlineData(Password, true, false)]
    public async Task InvalidCredentialsOrInactiveAccountCannotCreateSession(string password, bool exists, bool active)
    {
        _accounts.User = exists ? new(42, "U42", PasswordHash, active) : null;
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => Service().LoginAsync("user@example.com", password, default))).StatusCode);
        Assert.Null(_sessions.Session);
    }

    [Fact]
    public async Task SsoOnlyAccountCannotUsePasswordLogin()
    {
        _accounts.User = new(42, "U42", null, true);
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => Service().LoginAsync("user@example.com", Password, default))).StatusCode);
    }

    [Fact]
    public async Task VerifiedSsoLoginUsesTheSameApprovedGrantsAndSessionStore()
    {
        _accounts.User = new(42, "U42", null, true);
        var result = await Service().LoginSsoAsync(new("Google", "provider-subject", null, null), default);
        Assert.Equal(42, result.UserId);
        Assert.Equal(LoginService.Hash(result.RefreshToken), _sessions.TokenHash);
        Assert.Equal(new[] { "TENANT" }, _issuer.Access!.Roles);
    }

    [Fact]
    public async Task RefreshRotatesTokenAndReloadsApprovedPermissions()
    {
        var service = Service();
        var login = await service.LoginAsync("user@example.com", Password, default);
        _sessions.Access = new(["OWNER"], ["properties.write"]);
        var refreshed = await service.RefreshAsync(42, login.RefreshToken, default);
        Assert.NotEqual(login.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(LoginService.Hash(refreshed.RefreshToken), _sessions.TokenHash);
        Assert.Equal(new[] { "OWNER" }, _issuer.Access!.Roles);
        Assert.Equal(login.SessionExpiresAtUtc, refreshed.SessionExpiresAtUtc);
    }

    [Fact]
    public async Task RejectedRotationDoesNotReturnToken()
    {
        var service = Service();
        var login = await service.LoginAsync("user@example.com", Password, default);
        _sessions.RejectRotation = true;
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => service.RefreshAsync(42, login.RefreshToken, default))).StatusCode);
    }

    [Fact]
    public async Task SigningFailureDoesNotConsumeRefreshToken()
    {
        var service = Service();
        var login = await service.LoginAsync("user@example.com", Password, default);
        _issuer.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshAsync(42, login.RefreshToken, default));
        Assert.Equal(LoginService.Hash(login.RefreshToken), _sessions.TokenHash);
        Assert.Equal(0, _sessions.Rotations);
    }

    [Fact]
    public async Task PersistenceFailureDoesNotReturnSuccessfulLogin()
    {
        _sessions.FailCreate = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().LoginAsync("user@example.com", Password, default));
    }

    [Fact]
    public async Task LogoutRevokesServerSession()
    {
        var service = Service();
        var login = await service.LoginAsync("user@example.com", Password, default);
        await service.LogoutAsync(42, login.RefreshToken, default);
        Assert.True(_sessions.Revoked);
    }

    [Fact]
    public void JwtIsRsaSignedAndExpiresExactlyTwentyMinutesAfterIssuance()
    {
        using var rsa = RSA.Create(2048);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "https://issuer.test", ["Jwt:Audience"] = "EasyTolet.Api",
            ["Jwt:SigningPrivateKeyPem"] = rsa.ExportPkcs8PrivateKeyPem()
        }).Build();
        var response = new JwtAccessTokenIssuer(config, _clock).Issue(42, "U42", Guid.NewGuid(), new(["ADMIN"], ["users.manage"]));
        var token = new JwtSecurityTokenHandler().ReadJwtToken(response.AccessToken);
        Assert.Equal(1200, response.ExpiresIn);
        Assert.Equal(_clock.Now.UtcDateTime.AddMinutes(20), token.ValidTo);
        Assert.Equal(TimeSpan.FromMinutes(20), token.ValidTo - token.ValidFrom);
        Assert.Equal("RS256", token.Header.Alg);
        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(response.AccessToken,
            new TokenValidationParameters
            {
                ValidateLifetime = false, ValidateIssuer = true, ValidIssuer = "https://issuer.test",
                ValidateAudience = true, ValidAudience = "EasyTolet.Api", ValidateIssuerSigningKey = true,
                IssuerSigningKey = new RsaSecurityKey(rsa.ExportParameters(false)), RoleClaimType = "role"
            }, out _);
        Assert.True(principal.IsInRole("ADMIN"));
        Assert.Equal("42", principal.FindFirst("user_id")!.Value);
        Assert.DoesNotContain("Password", response.AccessToken);
    }

    [Fact]
    public void PasswordHashUsesRandomSaltAndRejectsWrongPassword()
    {
        var hash = PasswordHashing.Hash(Password);
        Assert.NotEqual(Password, hash);
        Assert.NotEqual(PasswordHash, hash);
        Assert.True(PasswordHashing.Verify(hash, Password));
        Assert.False(PasswordHashing.Verify(hash, "incorrect"));
        Assert.False(PasswordHashing.Verify(null, Password));
        Assert.False(PasswordHashing.Verify("not-a-hash", Password));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Accounts : ILoginUserRepository
    {
        public LoginUser? User = new(42, "U42", PasswordHash, true);
        public Task<LoginUser?> FindByEmailAsync(string email, CancellationToken ct) => Task.FromResult(User);
        public Task<LoginUser?> GetAsync(long id, CancellationToken ct) => Task.FromResult(User);
    }
    private sealed class Issuer : IAccessTokenIssuer
    {
        public ApprovedAccess? Access; public bool Fail;
        public AccessTokenResponse Issue(long id, string code, Guid session, ApprovedAccess access)
        { if (Fail) throw new InvalidOperationException("signing failed"); Access = access; return new("jwt", "Bearer", 1200, DateTime.UtcNow.AddMinutes(20)); }
    }
    private sealed class Sessions : ILoginSessionRepository
    {
        public LoginSession? Session; public string? TokenHash;
        public bool RejectRotation, FailCreate, Revoked; public int Rotations;
        public ApprovedAccess Access = new(["TENANT"], ["properties.read"]);
        public Task<ApprovedAccess> GetAccessAsync(long userId, CancellationToken ct) => Task.FromResult(Access);
        public Task CreateAsync(LoginSession session, string hash, CancellationToken ct)
        { if (FailCreate) throw new InvalidOperationException("database failure"); Session = session; TokenHash = hash; return Task.CompletedTask; }
        public Task<LoginSession?> GetSessionAsync(long userId, string hash, CancellationToken ct) => Task.FromResult(Session);
        public Task<LoginSession?> RotateAsync(long userId, string hash, string replacement, string stamp, DateTime now, CancellationToken ct)
        { Rotations++; if (RejectRotation) return Task.FromResult<LoginSession?>(null); TokenHash = replacement; return Task.FromResult(Session); }
        public Task RevokeAsync(long userId, string hash, DateTime now, CancellationToken ct) { Revoked = true; return Task.CompletedTask; }
    }
    private sealed class SsoUsers : ISsoUserRepository
    {
        public Task<UserResponse?> FindSsoAsync(SsoIdentity identity, CancellationToken ct) => Task.FromResult<UserResponse?>(new() { Id = 42, UserId = "U42", IsActive = true });
        public Task<UserResponse> RegisterSsoAsync(RegisterUserRequest request, SsoIdentity identity, CancellationToken ct) => throw new NotSupportedException();
    }
}
