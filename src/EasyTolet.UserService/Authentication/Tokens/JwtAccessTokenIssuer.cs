using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace EasyTolet.UserService.Authentication.Tokens;

public sealed class JwtAccessTokenIssuer(IConfiguration configuration, TimeProvider clock) : IAccessTokenIssuer
{
    public const int LifetimeSeconds = 20 * 60;

    public AccessTokenResponse Issue(long userId, string userIdCode, Guid sessionId, ApprovedAccess access)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(userIdCode)) throw new InvalidOperationException("A valid local user is required.");
        var issuer = Required("Jwt:Issuer");
        var audience = Required("Jwt:Audience");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(Required("Jwt:SigningPrivateKeyPem"));
        if (rsa.KeySize < 2048) throw new InvalidOperationException("JWT RSA keys must be at least 2048 bits.");
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa)
        { KeyId = configuration["Jwt:KeyId"] ?? "easytolet-v1" }, SecurityAlgorithms.RsaSha256)
        { CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } };
        var now = clock.GetUtcNow();
        var expires = now.AddSeconds(LifetimeSeconds);
        var claims = new List<Claim>
        {
            new("sub", userIdCode), new("user_id", userId.ToString(CultureInfo.InvariantCulture)),
            new("sid", sessionId.ToString("D")), new("jti", Guid.NewGuid().ToString("N")),
            new("iat", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
        };
        claims.AddRange(access.Roles.Distinct(StringComparer.Ordinal).Select(role => new Claim("role", role)));
        claims.AddRange(access.Permissions.Distinct(StringComparer.Ordinal).Select(permission => new Claim("permission", permission)));
        var token = new JwtSecurityToken(issuer, audience, claims, now.UtcDateTime, expires.UtcDateTime, credentials);
        token.Header["typ"] = "at+jwt";
        return new(new JwtSecurityTokenHandler().WriteToken(token), "Bearer", LifetimeSeconds, expires.UtcDateTime);
    }

    private string Required(string key) => !string.IsNullOrWhiteSpace(configuration[key]) ? configuration[key]!
        : throw new InvalidOperationException($"{key} is required for JWT issuance.");
}
