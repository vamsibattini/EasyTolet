using EasyTolet.UserService.Authentication.Tokens;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EasyTolet.Shared.Exceptions;
using EasyTolet.UserService.Authentication;
using EasyTolet.UserService.Contracts;
using EasyTolet.UserService.Controllers;
using EasyTolet.UserService.Repositories;
using Microsoft.AspNetCore.Authentication.Facebook;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using static EasyTolet.UserService.Tests.Repositories.UserBusinessRepositoryTests;

namespace EasyTolet.UserService.Tests.Authentication;

// Real OAuth handlers/cookies/antiforgery middleware; only remote HTTP and data persistence are faked.
public sealed class SsoHttpTests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _client = null!;
    private readonly CookieContainer _cookies = new();
    private readonly FakeSsoUsers _users = new();
    private readonly ProviderBackchannel _backchannel = new();
    private readonly Reporter _reporter = new();

    public async Task InitializeAsync()
    {
        var settings = new Dictionary<string, string?>();
        foreach (var provider in SsoSchemes.Providers)
        {
            settings[$"Authentication:{provider}:Enabled"] = "true";
            settings[$"Authentication:{provider}:ClientId"] = "test-client";
            settings[$"Authentication:{provider}:ClientSecret"] = "test-secret";
        }
        settings["Authentication:Facebook:GraphApiVersion"] = "v23.0";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        _host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(SsoController).Assembly);
            services.AddScoped(_ => new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().Options));
            services.AddSingleton<ISsoUserRepository>(_users);
            services.AddSingleton<ILoginService, SsoLoginStub>();
            services.AddGlobalExceptionHandling(configuration);
            services.AddSingleton<IExceptionReporter>(_reporter);
            services.AddAuthentication("Bearer").AddJwtBearer("Bearer", _ => { });
            services.AddUserAuthorization();
            services.AddUserSso(configuration);
            services.PostConfigure<GoogleOptions>("Google", options => Backchannel(options));
            services.PostConfigure<FacebookOptions>("Facebook", options => Backchannel(options));
            services.PostConfigure<MicrosoftAccountOptions>("Microsoft", options => Backchannel(options));
        }).Configure(app =>
        {
            app.UseGlobalExceptionHandling(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        })).StartAsync();
        _client = _host.GetTestClient();
        _client.BaseAddress = new Uri("https://localhost");
    }
    private void Backchannel(OAuthOptions options) => options.Backchannel = new HttpClient(_backchannel, disposeHandler: false);
    public async Task DisposeAsync() { _client.Dispose(); await _host.StopAsync(); _host.Dispose(); }

    [Theory]
    [InlineData("Google", "/signin-google")]
    [InlineData("Facebook", "/signin-facebook")]
    [InlineData("Microsoft", "/signin-microsoft")]
    public async Task VerifiedProviderFlowRegistersIdentityThenCreatesSession(string provider, string callback)
    {
        var callbackResponse = await Callback(provider, callback);
        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        Assert.Equal("/api/users/sso/complete", callbackResponse.Headers.Location!.OriginalString);
        var complete = await Send(HttpMethod.Get, "/api/users/sso/complete");
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var onboarding = await complete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(onboarding.GetProperty("registrationRequired").GetBoolean());
        Assert.Equal(provider, onboarding.GetProperty("provider").GetString());
        var register = await Send(HttpMethod.Post, "/api/users/sso/register", Input(), onboarding.GetProperty("csrfToken").GetString());
        Assert.True(register.StatusCode == HttpStatusCode.OK, _reporter.Details);
        Assert.Equal(provider, _users.RegisteredIdentity!.Provider);
        Assert.Equal("provider-subject", _users.RegisteredIdentity.Subject);
        Assert.Equal("provider@example.com", _users.RegisteredIdentity.Email);
        Assert.Equal(2, _users.RegisteredRole);
        Assert.Equal(1, _users.Registrations);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/users/sso/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/users/sso/complete")).StatusCode);
        var setCookies = string.Join(";", register.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", setCookies, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookies, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-access-token", await register.Content.ReadAsStringAsync());
        Assert.Equal(0, _reporter.Calls);
    }

    [Fact]
    public async Task ReturningUserSignsInWithoutRegisteringAndLogoutRequiresAntiforgery()
    {
        _users.Existing = FakeSsoUsers.ActiveUser();
        await Callback("Google", "/signin-google");
        var complete = await Send(HttpMethod.Get, "/api/users/sso/complete");
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var body = await complete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("registrationRequired").GetBoolean());
        Assert.Equal(0, _users.Registrations);
        // A local SSO session is not a bearer token for management or downstream APIs.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/users/42")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/users/sso/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, "/api/users/sso/logout", csrf: body.GetProperty("csrfToken").GetString())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/users/sso/session")).StatusCode);
    }

    [Fact]
    public async Task RegistrationRequiresPendingIdentityValidProfileAndAntiforgery()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/users/sso/register", Input())).StatusCode);
        await Callback("Google", "/signin-google");
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/users/sso/register", Input())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/users/sso/register", Input(), "forged")).StatusCode);
        var onboarding = await (await Send(HttpMethod.Get, "/api/users/sso/complete")).Content.ReadFromJsonAsync<JsonElement>();
        var input = Input(); input.EmailId = "invalid";
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/users/sso/register", input, onboarding.GetProperty("csrfToken").GetString())).StatusCode);
        Assert.Equal(0, _users.Registrations);
    }

    [Fact]
    public async Task ForgedProfileCannotReplaceVerifiedProviderIdentity()
    {
        await Callback("Google", "/signin-google");
        var onboarding = await (await Send(HttpMethod.Get, "/api/users/sso/complete")).Content.ReadFromJsonAsync<JsonElement>();
        var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(Input()))!;
        payload["SSOProvider"] = "Facebook";
        payload["SSOProviderUserId"] = "victim";
        payload["SSOEmailId"] = "victim@example.com";
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/api/users/sso/register", payload, onboarding.GetProperty("csrfToken").GetString())).StatusCode);
        Assert.Equal("Google", _users.RegisteredIdentity!.Provider);
        Assert.Equal("provider-subject", _users.RegisteredIdentity.Subject);
    }

    [Theory]
    [InlineData("/signin-google")]
    [InlineData("/signin-facebook")]
    [InlineData("/signin-microsoft")]
    public async Task ForgedCallbackIsRejectedWithoutRemoteOrDatabaseAccess(string callback)
    {
        var response = await Send(HttpMethod.Get, callback + "?code=attacker&state=forged");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _backchannel.Calls); Assert.Equal(0, _users.Lookups);
        Assert.Equal(1, _reporter.Calls);
    }

    [Fact]
    public async Task CallbackReplayIsRejectedAfterCorrelationCookieIsConsumed()
    {
        var login = await Send(HttpMethod.Get, "/api/users/sso/google/login");
        var state = QueryHelpers.ParseQuery(login.Headers.Location!.Query)["state"].ToString();
        var path = "/signin-google?code=test-code&state=" + Uri.EscapeDataString(state);
        Assert.Equal(HttpStatusCode.Redirect, (await Send(HttpMethod.Get, path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, path)).StatusCode);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(500)]
    public async Task FailedRegistrationNeverIssuesSessionCookie(int status)
    {
        await Callback("Google", "/signin-google");
        var body = await (await Send(HttpMethod.Get, "/api/users/sso/complete")).Content.ReadFromJsonAsync<JsonElement>();
        _users.RegistrationError = new ApiException("Registration failed", status);
        Assert.Equal(status, (int)(await Send(HttpMethod.Post, "/api/users/sso/register", Input(), body.GetProperty("csrfToken").GetString())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/users/sso/session")).StatusCode);
        Assert.Equal(1, _reporter.Calls);
    }

    [Fact]
    public async Task ProviderErrorDoesNotLeakTokensOrCreateSession()
    {
        _backchannel.Fail = true;
        var response = await Callback("Google", "/signin-google");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("sensitive-provider-error", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("sensitive-provider-error", _reporter.Details ?? "");
        Assert.Equal(0, _users.Lookups);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/users/sso/session")).StatusCode);
    }

    [Fact]
    public async Task DisabledOrUnknownProviderCannotBeChallenged()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, "/api/users/sso/instagram/login")).StatusCode);
        var providers = await (await Send(HttpMethod.Get, "/api/users/sso/providers")).Content.ReadFromJsonAsync<string[]>();
        Assert.Equal(3, providers!.Length);
        Assert.DoesNotContain("Instagram", providers);
    }

    [Fact]
    public async Task DeactivatedUserCannotContinueUsingSession()
    {
        _users.Existing = FakeSsoUsers.ActiveUser();
        await Callback("Google", "/signin-google");
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/users/sso/complete")).StatusCode);
        _users.Existing.IsActive = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/api/users/sso/session")).StatusCode);
    }

    [Fact]
    public async Task VerifiedSsoSessionCanExchangeForJwtSessionWithCsrfProtection()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/login/sso")).StatusCode);
        _users.Existing = FakeSsoUsers.ActiveUser();
        await Callback("Google", "/signin-google");
        var complete = await (await Send(HttpMethod.Get, "/api/users/sso/complete")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/login/sso")).StatusCode);
        var response = await Send(HttpMethod.Post, "/api/login/sso", csrf: complete.GetProperty("csrfToken").GetString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1200, body.GetProperty("expiresIn").GetInt32());
        Assert.False(body.TryGetProperty("refreshToken", out _));
    }

    private sealed class SsoLoginStub : ILoginService
    {
        public Task<LoginResult> LoginSsoAsync(SsoIdentity identity, CancellationToken ct)
        {
            Assert.Equal("Google", identity.Provider);
            Assert.Equal("provider-subject", identity.Subject);
            return Task.FromResult(new LoginResult(new("test-jwt", "Bearer", 1200, DateTime.UtcNow.AddMinutes(20)),
                new string('a', 86), DateTime.UtcNow.AddHours(8), 42));
        }
        public Task<LoginResult> LoginAsync(string email, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task<LoginResult> RefreshAsync(long userId, string token, CancellationToken ct) => throw new NotSupportedException();
        public Task LogoutAsync(long userId, string token, CancellationToken ct) => throw new NotSupportedException();
    }

    private async Task<HttpResponseMessage> Callback(string provider, string path)
    {
        var login = await Send(HttpMethod.Get, $"/api/users/sso/{provider}/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var query = QueryHelpers.ParseQuery(login.Headers.Location!.Query);
        Assert.Equal("test-client", query["client_id"].ToString());
        if (provider != "Facebook") Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
        return await Send(HttpMethod.Get, path + "?code=test-code&state=" + Uri.EscapeDataString(query["state"].ToString()));
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, string? csrf = null)
    {
        using var request = new HttpRequestMessage(method, path);
        var uri = new Uri(_client.BaseAddress!, path);
        var cookies = _cookies.GetCookieHeader(uri);
        if (cookies.Length > 0) request.Headers.Add("Cookie", cookies);
        if (csrf is not null) request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await _client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (var cookie in values) _cookies.SetCookies(uri, cookie);
        return response;
    }

    private sealed class FakeSsoUsers : ISsoUserRepository
    {
        public UserResponse? Existing;
        public SsoIdentity? RegisteredIdentity;
        public Exception? RegistrationError;
        public int Registrations, Lookups, RegisteredRole;
        public static UserResponse ActiveUser() => new() { Id = 42, UserId = "U42", RoleId = 2, IsActive = true };
        public Task<UserResponse?> FindSsoAsync(SsoIdentity identity, CancellationToken ct)
        {
            Lookups++;
            if (Existing is { IsActive: false }) throw new ApiException("Inactive", 403);
            return Task.FromResult(Existing);
        }
        public Task<UserResponse> RegisterSsoAsync(RegisterUserRequest request, SsoIdentity identity, CancellationToken ct)
        {
            Registrations++; RegisteredIdentity = identity; RegisteredRole = request.RoleId;
            if (RegistrationError is not null) throw RegistrationError;
            Existing = ActiveUser(); return Task.FromResult(Existing);
        }
    }
    private sealed class Reporter : IExceptionReporter
    {
        public int Calls; public string? Details;
        public Task ReportAsync(ExceptionLogEntry entry) { Calls++; Details = entry.ExceptionDetails; return Task.CompletedTask; }
    }
    private sealed class ProviderBackchannel : HttpMessageHandler
    {
        public bool Fail;
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Fail) return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"sensitive-provider-error\"}") };
            if (request.RequestUri!.AbsolutePath.Contains("token", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Contains("test-code", await request.Content!.ReadAsStringAsync(ct));
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"test-access-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}") };
            }
            // Fields consumed by Google's, Microsoft's and Facebook's official handlers.
            return new(HttpStatusCode.OK) { Content = new StringContent("""
                {"sub":"provider-subject","id":"provider-subject","name":"Provider Name","displayName":"Provider Name",
                 "given_name":"Provider","family_name":"Name","email":"provider@example.com","email_verified":true,
                 "mail":"provider@example.com","userPrincipalName":"provider@example.com"}
                """) };
        }
    }
}
