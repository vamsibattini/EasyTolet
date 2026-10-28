using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using EasyTolet.Shared.Exceptions;
using EasyTolet.UserService.Accounts;
using EasyTolet.UserService.Authentication;
using EasyTolet.UserService.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EasyTolet.UserService.Tests.Accounts;

public sealed class AccountHttpTests : IAsyncLifetime
{
    private const string Key = "Account-tests-only-signing-key-12345678901234567890";
    private readonly AccountHarness _h = new();
    private IHost _host = null!;
    private HttpClient _client = null!;
    public async Task InitializeAsync()
    {
        _host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(AccountController).Assembly);
            services.AddSingleton(_h.Db);
            services.AddSingleton<IAccountService>(_h.Service);
            services.AddGlobalExceptionHandling(new ConfigurationBuilder().Build());
            services.AddSingleton<IExceptionReporter, Reporter>();
            services.AddAuthentication("Bearer").AddJwtBearer("Bearer", options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new()
                {
                    ValidIssuer = "account-tests", ValidAudience = "account-tests", ValidateLifetime = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), ValidateIssuerSigningKey = true
                };
            });
            services.AddAuthorization();
            services.AddUserSso(new ConfigurationBuilder().Build());
        }).Configure(app =>
        {
            app.UseGlobalExceptionHandling(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        })).StartAsync();
        _client = _host.GetTestClient();
    }
    public async Task DisposeAsync() { _client.Dispose(); await _host.StopAsync(); _host.Dispose(); _h.Dispose(); }

    [Theory]
    [InlineData("/api/account/profile")]
    [InlineData("/api/account/verification")]
    [InlineData("/api/account/sessions")]
    public async Task PrivateRoutesRejectAnonymousAndMalformedBearer(string path)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(path)).StatusCode);
        _client.DefaultRequestHeaders.Authorization = new("Bearer", "malformed");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(path)).StatusCode);
        Assert.Empty(_h.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSessionClaimOrRevokedSessionCannotUseValidJwt(bool revoked)
    {
        Authenticate(includeSession: revoked); _h.ValidSession = false;
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/account/profile")).StatusCode);
        Assert.Empty(_h.Writes);
    }

    [Fact]
    public async Task OrdinaryUserCanEditOwnProfileAndCannotSubmitPrivilegeOrContactFields()
    {
        Authenticate();
        var input = new { firstName = "New", address1 = "Road", country = "India", state = "Telangana", city = "Hyderabad", pinNumber = "500001" };
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PutAsJsonAsync("/api/account/profile", input)).StatusCode);
        Assert.Equal(7, AccountHarness.Field<long>(Assert.Single(_h.Writes).Parameters, "id"));
        foreach (var field in new[] { "roleId", "isActive", "password", "emailId", "userId" })
        {
            var values = new Dictionary<string, object> { [field] = "attack", ["firstName"] = "Name" };
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync("/api/account/profile", values)).StatusCode);
        }
        Assert.Single(_h.Writes);
    }

    [Fact]
    public async Task RecoveryAlwaysReturnsSameAcceptedResponseWithoutExposingToken()
    {
        var existing = await _client.PostAsJsonAsync("/api/account/password/forgot", new { email = "user@example.com" });
        var body = await existing.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Accepted, existing.StatusCode);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        _h.User = null;
        var absent = await _client.PostAsJsonAsync("/api/account/password/forgot", new { email = "absent@example.com" });
        Assert.Equal(existing.StatusCode, absent.StatusCode); Assert.Equal(body, await absent.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InvalidRecoveryInputAndInvalidResetTokenReturnBadRequest()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/account/password/forgot", new { email = "bad" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/account/password/reset", new { token = new string('x', 43), newPassword = "Replacement-password-123!" })).StatusCode);
        Assert.Empty(_h.Writes);
    }

    [Fact]
    public async Task SessionRevocationScopesForeignSessionToAuthenticatedOwner()
    {
        Authenticate(); var foreign = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/account/sessions/{foreign}")).StatusCode);
        var write = Assert.Single(_h.Writes);
        Assert.Equal(7, AccountHarness.Field<long>(write.Parameters, "id"));
        Assert.Equal(foreign.ToString("D"), AccountHarness.Field<string>(write.Parameters, "session"));
    }

    [Fact]
    public async Task RecoveryIsRateLimited()
    {
        _h.User = null;
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsJsonAsync("/api/account/password/forgot", new { email = "absent@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _client.PostAsJsonAsync("/api/account/password/forgot", new { email = "absent@example.com" })).StatusCode);
    }

    [Fact]
    public async Task VerificationAndDeletionRequestRoutesQueueMessagesAndRequireConfirmation()
    {
        Authenticate();
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsJsonAsync("/api/account/verification/request", new { channel = "email" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsync("/api/account/deletion/request", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/account/deletion/confirm", new { token = "invalid" })).StatusCode);
        Assert.DoesNotContain(_h.Writes, w => w.Sql.Contains("IsActive=FALSE"));
    }

    private void Authenticate(bool includeSession = true)
    {
        var claims = new List<Claim> { new("user_id", "7") };
        if (includeSession) claims.Add(new("sid", _h.Actor.SessionId.ToString("D")));
        var token = new JwtSecurityToken("account-tests", "account-tests", claims, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
    private sealed class Reporter : IExceptionReporter { public Task ReportAsync(ExceptionLogEntry entry) => Task.CompletedTask; }
}
