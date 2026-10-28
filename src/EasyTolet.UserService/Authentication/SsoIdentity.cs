using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using EasyTolet.Shared.Exceptions;

namespace EasyTolet.UserService.Authentication;

public static class SsoSchemes
{
    public const string External = "UserSsoExternal";
    public const string Session = "UserSsoSession";
    public const string ProviderClaim = "easytolet:sso_provider";
    public static readonly IReadOnlyList<string> Providers = Array.AsReadOnly(new[] { "Google", "Facebook", "Microsoft" });
}

// Constructed only from the protected external cookie issued after a successful OAuth callback.
// Never bind this type from an HTTP request body.
public sealed record SsoIdentity(string Provider, string Subject, string? Email, string? DisplayName)
{
    public static SsoIdentity FromPrincipal(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
            throw new ApiException("An authenticated SSO identity is required.", 401);
        var identity = new SsoIdentity(principal.FindFirstValue(SsoSchemes.ProviderClaim) ?? "",
            principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
            principal.FindFirstValue(ClaimTypes.Email), principal.FindFirstValue(ClaimTypes.Name));
        identity.Validate();
        return identity;
    }

    public void Validate()
    {
        if (!SsoSchemes.Providers.Contains(Provider, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(Subject) ||
            Subject.Length > 255 || Subject.Any(char.IsWhiteSpace) || Subject.Any(char.IsControl) ||
            (Email is not null && (Email.Length > 320 || !new EmailAddressAttribute().IsValid(Email))) ||
            DisplayName?.Length > 300)
            throw new ApiException("The provider returned an invalid identity.", 401);
    }

    // Explicit allowlist: no provider access/refresh tokens or arbitrary raw provider payloads.
    public string ProfileJson() => JsonSerializer.Serialize(new { provider = Provider, displayName = DisplayName });
}
