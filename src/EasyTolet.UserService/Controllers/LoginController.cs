using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using EasyTolet.Shared.Controllers;
using EasyTolet.Shared.Exceptions;
using EasyTolet.UserService.Authentication;
using EasyTolet.UserService.Authentication.Tokens;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EasyTolet.UserService.Controllers;

[ApiController]
[Route("api/login")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LoginController(AppDbContext database, IExceptionReporter exceptionReporter,
    ILoginService login, IAntiforgery antiforgery) : ServiceControllerBase<AppDbContext>(database, exceptionReporter)
{
    public const string SessionScheme = "UserLoginSession";
    public const string RefreshCookie = "__Host-EasyTolet.Refresh";

    [Authorize(AuthenticationSchemes = SessionScheme)]
    [AllowAnonymous]
    [HttpGet("csrf")]
    public IActionResult Csrf() => Ok(new { csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });

    [Authorize(AuthenticationSchemes = SessionScheme)]
    [AllowAnonymous]
    [ServiceFilter(typeof(SsoAntiforgeryFilter))]
    [EnableRateLimiting("Login")]
    [HttpPost]
    public async Task<IActionResult> Login(PasswordLoginRequest request, CancellationToken ct) =>
        await RespondAsync(await login.LoginAsync(request.EmailId, request.Password, ct), createSession: true);

    [Authorize(AuthenticationSchemes = SsoSchemes.Session)]
    [ServiceFilter(typeof(SsoAntiforgeryFilter))]
    [EnableRateLimiting("Login")]
    [HttpPost("sso")]
    public async Task<IActionResult> LoginSso(CancellationToken ct) =>
        await RespondAsync(await login.LoginSsoAsync(SsoIdentity.FromPrincipal(User), ct), createSession: true);

    [Authorize(AuthenticationSchemes = SessionScheme)]
    [ServiceFilter(typeof(SsoAntiforgeryFilter))]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct) =>
        await RespondAsync(await login.RefreshAsync(ActorId(), Request.Cookies[RefreshCookie] ?? "", ct), createSession: false);

    [Authorize(AuthenticationSchemes = SessionScheme)]
    [ServiceFilter(typeof(SsoAntiforgeryFilter))]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (Request.Cookies.TryGetValue(RefreshCookie, out var refresh))
            await login.LogoutAsync(ActorId(), refresh, ct);
        Response.Cookies.Delete(RefreshCookie, CookieSettings());
        await HttpContext.SignOutAsync(SessionScheme);
        await HttpContext.SignOutAsync(SsoSchemes.Session);
        await HttpContext.SignOutAsync(SsoSchemes.External);
        return NoContent();
    }

    private async Task<IActionResult> RespondAsync(LoginResult result, bool createSession)
    {
        if (createSession)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, result.UserId.ToString(CultureInfo.InvariantCulture)),
                new Claim("user_id", result.UserId.ToString(CultureInfo.InvariantCulture))
            }, SessionScheme));
            await HttpContext.SignInAsync(SessionScheme, principal, new AuthenticationProperties
            { IsPersistent = false, ExpiresUtc = new DateTimeOffset(DateTime.SpecifyKind(result.SessionExpiresAtUtc, DateTimeKind.Utc)) });
            HttpContext.User = principal;
        }
        Response.Cookies.Append(RefreshCookie, result.RefreshToken, CookieSettings());
        return Ok(new
        {
            result.Access.AccessToken, result.Access.TokenType, result.Access.ExpiresIn, result.Access.ExpiresAtUtc,
            csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken
        });
    }
    private long ActorId() => long.TryParse(User.FindFirstValue("user_id"), out var id) && id > 0 ? id
        : throw new ApiException("The session is invalid.", 401);
    private static CookieOptions CookieSettings() => new()
    { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true };
}

public sealed class PasswordLoginRequest
{
    [Required, EmailAddress, StringLength(320)] public string EmailId { get; set; } = "";
    [Required, StringLength(128)] public string Password { get; set; } = "";
}
