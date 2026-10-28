using EasyTolet.UserService.Controllers;
using System.Threading.RateLimiting;
using System.Security.Claims;
using System.Text.RegularExpressions;
using EasyTolet.Shared.Exceptions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace EasyTolet.UserService.Authentication;

public static class SsoServiceCollectionExtensions
{
    public static IServiceCollection AddUserSso(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<SsoAntiforgeryFilter>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("Login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "__Host-EasyTolet.SsoCsrf";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });
        // Keep Bearer as the default scheme. SSO cookies are accepted only by explicit SSO endpoints.
        var authentication = services.AddAuthentication()
            .AddCookie(SsoSchemes.External, options => Cookie(options, "__Host-EasyTolet.SsoExternal", TimeSpan.FromMinutes(10)))
            .AddCookie(SsoSchemes.Session, options => Cookie(options, "__Host-EasyTolet.SsoSession", TimeSpan.FromHours(8)))
            .AddCookie(LoginController.SessionScheme, options => Cookie(options, "__Host-EasyTolet.LoginSession", TimeSpan.FromHours(8)));

        if (Enabled(configuration, "Google"))
            authentication.AddGoogle("Google", options =>
            {
                Configure(options, configuration, "Google");
                options.UsePkce = true;
                options.CallbackPath = "/signin-google";
            });
        if (Enabled(configuration, "Microsoft"))
            authentication.AddMicrosoftAccount("Microsoft", options =>
            {
                Configure(options, configuration, "Microsoft");
                options.UsePkce = true;
                options.CallbackPath = "/signin-microsoft";
            });
        if (Enabled(configuration, "Facebook"))
            authentication.AddFacebook("Facebook", options =>
            {
                Configure(options, configuration, "Facebook");
                options.CallbackPath = "/signin-facebook";
                // Select the version enabled for the registered Meta app, not an obsolete package default.
                var version = Required(configuration, "Facebook", "GraphApiVersion");
                if (!Regex.IsMatch(version, "\\Av[0-9]{1,3}\\.[0-9]{1,2}\\z"))
                    throw new InvalidOperationException("Authentication:Facebook:GraphApiVersion must look like vNN.0.");
                options.AuthorizationEndpoint = $"https://www.facebook.com/{version}/dialog/oauth";
                options.TokenEndpoint = $"https://graph.facebook.com/{version}/oauth/access_token";
                options.UserInformationEndpoint = $"https://graph.facebook.com/{version}/me";
            });
        return services;
    }

    private static bool Enabled(IConfiguration configuration, string provider) =>
        configuration.GetValue<bool>($"Authentication:{provider}:Enabled");

    private static string Required(IConfiguration configuration, string provider, string name) =>
        !string.IsNullOrWhiteSpace(configuration[$"Authentication:{provider}:{name}"])
            ? configuration[$"Authentication:{provider}:{name}"]!
            : throw new InvalidOperationException($"Authentication:{provider}:{name} is required when the provider is enabled.");

    private static void Configure(OAuthOptions options, IConfiguration configuration, string provider)
    {
        options.ClientId = Required(configuration, provider, "ClientId");
        options.ClientSecret = Required(configuration, provider, "ClientSecret");
        options.SignInScheme = SsoSchemes.External;
        options.SaveTokens = false;
        options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(10);
        options.BackchannelTimeout = TimeSpan.FromSeconds(30);
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Events = new OAuthEvents
        {
            OnCreatingTicket = context =>
            {
                // These claims are produced by the provider handler after its server-side code exchange.
                context.Identity!.AddClaim(new Claim(SsoSchemes.ProviderClaim, provider));
                SsoIdentity.FromPrincipal(context.Principal!);
                return Task.CompletedTask;
            },
            OnRemoteFailure = context =>
            {
                // Never include the provider's raw token response or error_description in exception logs.
                // Throw a sanitized exception; the shared middleware reports it once.
                throw new ApiException("External sign-in failed. Start sign-in again.", 401);
            }
        };
    }

    private static void Cookie(CookieAuthenticationOptions options, string name, TimeSpan lifetime)
    {
        options.Cookie.Name = name;
        options.Cookie.Path = "/";
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = lifetime;
        options.SlidingExpiration = false;
        options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
    }
}
