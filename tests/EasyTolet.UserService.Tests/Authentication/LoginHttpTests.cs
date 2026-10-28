using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using EasyTolet.Shared.Exceptions;
using EasyTolet.Shared.Security;
using EasyTolet.UserService.Authentication;
using EasyTolet.UserService.Authentication.Tokens;
using EasyTolet.UserService.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EasyTolet.UserService.Tests.Authentication;

public sealed class LoginHttpTests : IAsyncLifetime
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly Clock _clock = new();
    private readonly CookieContainer _cookies = new();
    private IHost _host = null!;
    private HttpClient _client = null!;
    private FakeLogin _login = null!;
    private readonly Reporter _reporter = new();
    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "https://issuer.test", ["Jwt:Audience"] = "EasyTolet.Api",
            ["Jwt:SigningPrivateKeyPem"] = _rsa.ExportPkcs8PrivateKeyPem(), ["Jwt:PublicKeyPem"] = _rsa.ExportSubjectPublicKeyInfoPem()
        }).Build();
        _login = new FakeLogin(new JwtAccessTokenIssuer(config, _clock));
        _host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(LoginController).Assembly);
            services.AddScoped(_ => new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().Options));
            services.AddSingleton<ILoginService>(_login);
            services.AddGlobalExceptionHandling(config);
            services.AddSingleton<IExceptionReporter>(_reporter);
            services.AddServiceSecurity(config);
            services.AddUserSso(config);
        }).Configure(app =>
        {
            app.UseGlobalExceptionHandling(); app.UseRouting(); app.UseServiceSecurity(); app.UseRateLimiter();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapGet("/protected", context => context.Response.WriteAsync("ok"))
                    .RequireAuthorization(new RequirePermissionAttribute("users.manage"));
            });
        })).StartAsync();
        _client = _host.GetTestClient(); _client.BaseAddress = new Uri("https://localhost");
    }
    public async Task DisposeAsync() { _client.Dispose(); await _host.StopAsync(); _host.Dispose(); _rsa.Dispose(); }

    [Fact]
    public async Task PasswordLoginIssuesJwtAndRotatesRefreshCookieThenLogoutEndsSession()
    {
        var csrf = await Csrf();
        var login = await Send(HttpMethod.Post, "/api/login", Credentials(), csrf);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var payload = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1200, payload.GetProperty("expiresIn").GetInt32());
        Assert.False(payload.TryGetProperty("refreshToken", out _));
        var originalCookie = _cookies.GetCookies(_client.BaseAddress!)[LoginController.RefreshCookie]!.Value;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/protected")).StatusCode);
        _client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/protected")).StatusCode);
        csrf = payload.GetProperty("csrfToken").GetString()!;
        var refresh = await Send(HttpMethod.Post, "/api/login/refresh", csrf: csrf);
        Assert.True(refresh.StatusCode == HttpStatusCode.OK, _reporter.Details);
        Assert.NotEqual(originalCookie, _cookies.GetCookies(_client.BaseAddress!)[LoginController.RefreshCookie]!.Value);
        Assert.Equal(1, _login.Refreshes);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, "/api/login/logout", csrf: csrf)).StatusCode);
        Assert.True(_login.Revoked);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/login/refresh", csrf: csrf)).StatusCode);
    }

    [Fact]
    public async Task ExpiredAccessTokenCanBeReplacedOnlyWithValidBrowserAndRefreshSession()
    {
        _clock.Now = DateTimeOffset.UtcNow.AddMinutes(-21);
        var response = await Send(HttpMethod.Post, "/api/login", Credentials(), await Csrf());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/protected")).StatusCode);
        _clock.Now = DateTimeOffset.UtcNow;
        var refresh = await Send(HttpMethod.Post, "/api/login/refresh", csrf: body.GetProperty("csrfToken").GetString());
        Assert.True(refresh.StatusCode == HttpStatusCode.OK, _reporter.Details);
        var refreshed = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/protected")).StatusCode);
    }

    [Fact]
    public async Task LoginAndRefreshRequireCsrfAndRefreshCannotBeAnonymous()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/login", Credentials())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/login/refresh")).StatusCode);
        Assert.Equal(0, _login.Logins);
        var login = await Send(HttpMethod.Post, "/api/login", Credentials(), await Csrf());
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/login/refresh", csrf: "forged")).StatusCode);
        Assert.Equal(0, _login.Refreshes);
    }

    [Fact]
    public async Task InvalidInputAndCredentialsDoNotCreateSession()
    {
        var csrf = await Csrf();
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/login", new { emailId = "invalid", password = "" }, csrf)).StatusCode);
        Assert.Equal(0, _login.Logins);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/login", new { emailId = "user@example.com", password = "wrong" }, csrf)).StatusCode);
        Assert.Null(_cookies.GetCookies(_client.BaseAddress!)[LoginController.RefreshCookie]);
    }

    [Fact]
    public async Task PasswordEndpointRateLimitsRepeatedAttempts()
    {
        var csrf = await Csrf();
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/login", new { emailId = "user@example.com", password = "wrong" }, csrf)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(HttpMethod.Post, "/api/login", Credentials(), csrf)).StatusCode);
    }

    private async Task<string> Csrf() => (await (await Send(HttpMethod.Get, "/api/login/csrf")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("csrfToken").GetString()!;
    private static object Credentials() => new { emailId = "user@example.com", password = "Test-only-password-123!" };
    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, string? csrf = null)
    {
        using var request = new HttpRequestMessage(method, path);
        var uri = new Uri(_client.BaseAddress!, path);
        var cookie = _cookies.GetCookieHeader(uri);
        if (cookie.Length > 0) request.Headers.Add("Cookie", cookie);
        if (csrf is not null) request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await _client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies)) foreach (var value in cookies) _cookies.SetCookies(uri, value);
        return response;
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Reporter : IExceptionReporter { public string? Details; public Task ReportAsync(ExceptionLogEntry entry) { Details = entry.ExceptionDetails; return Task.CompletedTask; } }
    private sealed class FakeLogin(IAccessTokenIssuer issuer) : ILoginService
    {
        public int Logins, Refreshes; public bool Revoked;
        private string _refresh = "";
        private readonly Guid _session = Guid.NewGuid();
        public Task<LoginResult> LoginAsync(string email, string password, CancellationToken ct)
        {
            Logins++;
            if (email != "user@example.com" || password != "Test-only-password-123!") throw new ApiException("Invalid credentials", 401);
            return Task.FromResult(Result());
        }
        public Task<LoginResult> LoginSsoAsync(SsoIdentity identity, CancellationToken ct) => Task.FromResult(Result());
        public Task<LoginResult> RefreshAsync(long userId, string refreshToken, CancellationToken ct)
        {
            Assert.Equal(42, userId); Assert.Equal(_refresh, refreshToken); Refreshes++; return Task.FromResult(Result());
        }
        public Task LogoutAsync(long userId, string refreshToken, CancellationToken ct)
        { Assert.Equal(42, userId); Revoked = true; return Task.CompletedTask; }
        private LoginResult Result()
        {
            _refresh = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
            return new(issuer.Issue(42, "U42", _session, new(["ADMIN"], ["users.manage"])), _refresh, DateTime.UtcNow.AddHours(8), 42);
        }
    }
}
