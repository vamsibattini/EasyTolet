using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using EasyTolet.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EasyTolet.Shared.Tests.Security;

public sealed class ServiceSecurityTests : IAsyncLifetime
{
    private readonly RSA _rsa = RSA.Create(2048);
    private IHost _host = null!;
    private HttpClient _client = null!;
    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Jwt:Issuer"] = "https://issuer.test", ["Jwt:Audience"] = "EasyTolet.Api", ["Jwt:PublicKeyPem"] = _rsa.ExportSubjectPublicKeyInfoPem() }).Build();
        _host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        { services.AddRouting(); services.AddServiceSecurity(configuration); }).Configure(app =>
        {
            app.UseRouting(); app.UseServiceSecurity();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/public", context => context.Response.WriteAsync("public")).AllowAnonymous();
                endpoints.MapGet("/default", context => context.Response.WriteAsync("authenticated"));
                endpoints.MapGet("/role", context => context.Response.WriteAsync("admin"))
                    .RequireAuthorization(new AuthorizeAttribute { Roles = "ADMIN" });
                endpoints.MapGet("/permission", context => context.Response.WriteAsync("permitted"))
                    .RequireAuthorization(new RequirePermissionAttribute("users.manage"));
                endpoints.MapGet("/both", context => context.Response.WriteAsync("both"))
                    .RequireAuthorization(new AuthorizeAttribute { Roles = "ADMIN" }, new RequirePermissionAttribute("users.manage"));
            });
        })).StartAsync();
        _client = _host.GetTestClient();
    }
    public async Task DisposeAsync() { _client.Dispose(); await _host.StopAsync(); _host.Dispose(); _rsa.Dispose(); }

    [Fact]
    public async Task AnonymousHealthIsAllowedButUnannotatedEndpointsAreProtected()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/public")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/default")).StatusCode);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("signature")]
    public async Task InvalidTokensAreRejectedBeforeEndpointExecution(string invalid)
    {
        Authenticate(Token(invalid: invalid));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/default")).StatusCode);
    }

    [Fact]
    public async Task RolesAndPermissionsAreEnforcedIndependentlyAndTogether()
    {
        Authenticate(Token());
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/default")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/role")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/permission")).StatusCode);
        Authenticate(Token(role: "ADMIN"));
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/role")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/both")).StatusCode);
        Authenticate(Token(role: "ADMIN", permission: "users.manage"));
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/both")).StatusCode);
        Authenticate(Token(role: "admin", permission: "Users.Manage"));
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/both")).StatusCode);
    }
    private void Authenticate(string token) => _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    private string Token(string? role = null, string? permission = null, string? invalid = null)
    {
        var claims = new List<Claim> { new("sub", "U42"), new("user_id", "42") };
        if (role is not null) claims.Add(new("role", role));
        if (permission is not null) claims.Add(new("permission", permission));
        using var otherKey = RSA.Create(2048);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(invalid == "issuer" ? "https://other.test" : "https://issuer.test",
            invalid == "audience" ? "other-api" : "EasyTolet.Api", claims,
            invalid == "future" ? now.AddMinutes(1) : now.AddMinutes(-30),
            invalid == "expired" ? now.AddSeconds(-1) : now.AddMinutes(20),
            new SigningCredentials(new RsaSecurityKey(invalid == "signature" ? otherKey : _rsa), SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
