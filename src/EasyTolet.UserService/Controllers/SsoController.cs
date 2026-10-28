using System.Security.Claims;
using EasyTolet.Shared.Controllers;
using EasyTolet.Shared.Exceptions;
using EasyTolet.UserService.Authentication;
using EasyTolet.UserService.Contracts;
using EasyTolet.UserService.Repositories;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EasyTolet.UserService.Controllers;

[ApiController]
[Route("api/users/sso")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SsoController(AppDbContext database, IExceptionReporter exceptionReporter,
    ISsoUserRepository users, IAuthenticationSchemeProvider schemes, IAntiforgery antiforgery)
    : ServiceControllerBase<AppDbContext>(database, exceptionReporter)
{
    [AllowAnonymous]
    [HttpGet("providers")]
    public async Task<IActionResult> Providers() => Ok((await schemes.GetAllSchemesAsync())
        .Where(s => SsoSchemes.Providers.Contains(s.Name, StringComparer.Ordinal)).Select(s => s.Name));

    [AllowAnonymous]
    [HttpGet("{provider}/login")]
    public async Task<IActionResult> Login(string provider)
    {
        var name = SsoSchemes.Providers.FirstOrDefault(p => p.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (name is null || await schemes.GetSchemeAsync(name) is null)
            throw new ApiException("This SSO provider is not enabled.", 400);
        await HttpContext.SignOutAsync(SsoSchemes.External);
        // Only this fixed local destination is permitted. OAuth middleware protects state and correlation.
        return Challenge(new AuthenticationProperties { RedirectUri = Url.Action(nameof(Complete)) }, name);
    }

    [Authorize(AuthenticationSchemes = SsoSchemes.External)]
    [HttpGet("complete")]
    public async Task<IActionResult> Complete(CancellationToken ct)
    {
        var identity = SsoIdentity.FromPrincipal(User);
        var user = await users.FindSsoAsync(identity, ct);
        if (user is not null) return await SignInAsync(user, identity);
        return Ok(new
        {
            registrationRequired = true,
            provider = identity.Provider,
            displayName = identity.DisplayName,
            suggestedEmail = identity.Email,
            csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken
        });
    }

    [Authorize(AuthenticationSchemes = SsoSchemes.External)]
    [ServiceFilter(typeof(SsoAntiforgeryFilter))]
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterUserRequest request, CancellationToken ct)
    {
        var identity = SsoIdentity.FromPrincipal(User);
        var user = await users.RegisterSsoAsync(request, identity, ct);
        // Repository returns only after the entire registration transaction has committed.
        return await SignInAsync(user, identity);
    }

    [Authorize(AuthenticationSchemes = SsoSchemes.Session)]
    [HttpGet("session")]
    public async Task<IActionResult> Session(CancellationToken ct)
    {
        var identity = SsoIdentity.FromPrincipal(User);
        var user = await users.FindSsoAsync(identity, ct);
        if (user is null || User.FindFirstValue("user_id") != user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            await HttpContext.SignOutAsync(SsoSchemes.Session);
            throw new ApiException("The SSO session is no longer valid.", 401);
        }
        return Ok(new { registrationRequired = false, user, csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });
    }

    [Authorize(AuthenticationSchemes = SsoSchemes.Session)]
    [ServiceFilter(typeof(SsoAntiforgeryFilter))]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(SsoSchemes.External);
        await HttpContext.SignOutAsync(SsoSchemes.Session);
        return NoContent();
    }

    private async Task<IActionResult> SignInAsync(UserResponse user, SsoIdentity identity)
    {
        if (!user.IsActive) throw new ApiException("This user is inactive.", 403);
        // Deliberately no role/permission claims: selecting a role during registration does not grant API privileges.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, identity.Subject),
            new Claim(SsoSchemes.ProviderClaim, identity.Provider),
            new Claim("user_id", user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
        }, SsoSchemes.Session));
        await HttpContext.SignOutAsync(SsoSchemes.External);
        await HttpContext.SignInAsync(SsoSchemes.Session, principal, new AuthenticationProperties { IsPersistent = false });
        HttpContext.User = principal;
        return Ok(new { registrationRequired = false, user, csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });
    }
}
