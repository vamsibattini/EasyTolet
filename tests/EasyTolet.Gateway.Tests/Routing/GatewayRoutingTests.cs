using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using EasyTolet.Gateway.Routing;
using EasyTolet.Shared.Hosting;
using EasyTolet.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace EasyTolet.Gateway.Tests.Routing;

public sealed class GatewayRoutingTests : IAsyncLifetime
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly Dictionary<string, IHost> _backends = [];
    private readonly Dictionary<string, HttpMessageInvoker> _clients = [];
    private IHost _gateway = null!;
    private HttpClient _client = null!;
    private int _backendCalls;

    public async Task InitializeAsync()
    {
        foreach (var service in GatewayRoutes.Services)
        {
            var config = SecurityConfiguration();
            var backend = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
            {
                services.AddRouting(); services.AddServiceSecurity(config); services.AddTrustedGatewayHeaders(config);
            }).Configure(app =>
            {
                app.Use((context, next) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; return next(context); });
                app.UseForwardedHeaders(); app.UseRouting(); app.UseServiceSecurity();
                app.Use((context, next) => { Interlocked.Increment(ref _backendCalls); return next(context); });
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/api/health", context => context.Response.WriteAsync(service.Id)).AllowAnonymous();
                    endpoints.MapGet("/api/admin/roles", context => context.Response.WriteAsync("roles"))
                        .RequireAuthorization(policy => policy.RequireClaim("permission", "admin.roles.manage"));
                    endpoints.MapPost("/api/users/register", async context =>
                    {
                        context.Response.StatusCode = 201;
                        context.Response.Headers.Location = $"{context.Request.Scheme}://{context.Request.Host}/api/users/7";
                        await context.Response.WriteAsync("registered");
                    }).AllowAnonymous();
                    endpoints.MapPost("/api/login", async context =>
                    {
                        if (context.Request.Headers["X-CSRF-TOKEN"] != "csrf-token") { context.Response.StatusCode = 400; return; }
                        context.Response.Headers.Append("Set-Cookie", "__Host-EasyTolet.LoginSession=session; Path=/; Secure; HttpOnly; SameSite=Lax");
                        context.Response.Headers.Append("Set-Cookie", "__Host-EasyTolet.Refresh=refresh; Path=/; Secure; HttpOnly; SameSite=Strict");
                        await context.Response.WriteAsync("logged-in");
                    }).AllowAnonymous();
                    endpoints.MapPost("/api/login/refresh", context =>
                    {
                        context.Response.StatusCode = context.Request.Cookies.ContainsKey("__Host-EasyTolet.LoginSession") &&
                            context.Request.Cookies.ContainsKey("__Host-EasyTolet.Refresh") && context.Request.Headers["X-CSRF-TOKEN"] == "csrf-token" ? 204 : 401;
                        return Task.CompletedTask;
                    }).AllowAnonymous();
                    endpoints.MapGet("/signin-google", context =>
                    {
                        context.Response.Redirect($"{context.Request.Scheme}://{context.Request.Host}/api/users/sso/complete");
                        return Task.CompletedTask;
                    }).AllowAnonymous();
                    endpoints.MapGet("/api/users/sso/session", context => { context.Response.StatusCode = 401; return Task.CompletedTask; }).AllowAnonymous();
                    endpoints.MapPost("/api/account/password/forgot", context => { context.Response.StatusCode = 202; return Task.CompletedTask; }).AllowAnonymous();
                    endpoints.MapPost("/api/account/password/reset", context => { context.Response.StatusCode = 400; return Task.CompletedTask; }).AllowAnonymous();
                    endpoints.MapGet("/swagger/v1/swagger.json", context => context.Response.WriteAsJsonAsync(new { openapi = "3.0.1", service = service.Id })).AllowAnonymous();
                    endpoints.Map("/api/echo", async context =>
                    {
                        using var reader = new StreamReader(context.Request.Body);
                        await context.Response.WriteAsJsonAsync(new
                        {
                            service = service.Id, method = context.Request.Method, path = context.Request.Path.Value,
                            query = context.Request.QueryString.Value, body = await reader.ReadToEndAsync(),
                            authorization = context.Request.Headers.Authorization.ToString(), scheme = context.Request.Scheme,
                            host = context.Request.Host.Value, ip = context.Connection.RemoteIpAddress?.ToString(),
                            forwarded = context.Request.Headers["Forwarded"].ToString()
                        });
                    });
                });
            })).StartAsync();
            _backends.Add(service.Id, backend); _clients.Add(service.Id, new HttpMessageInvoker(backend.GetTestServer().CreateHandler()));
        }
        var gatewayConfig = SecurityConfiguration();
        foreach (var service in GatewayRoutes.Services) gatewayConfig[$"DownstreamServices:{service.Id}:Address"] = $"http://{service.Id}.internal/";
        _gateway = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddServiceSecurity(gatewayConfig);
            services.AddGatewayRouting(gatewayConfig, development: true);
            services.AddSingleton<IForwarderHttpClientFactory>(new Factory(_clients));
        }).Configure(app =>
        {
            app.Use((context, next) => { context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.25"); return next(context); });
            app.UseRouting(); app.UseServiceSecurity(); app.UseEndpoints(endpoints => endpoints.MapReverseProxy());
        })).StartAsync();
        _client = _gateway.GetTestClient(); _client.BaseAddress = new Uri("https://gateway.test");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose(); await _gateway.StopAsync(); _gateway.Dispose();
        foreach (var client in _clients.Values) client.Dispose();
        foreach (var host in _backends.Values) { await host.StopAsync(); host.Dispose(); }
        _rsa.Dispose();
    }

    [Fact]
    public async Task EveryServiceHealthRouteReachesOnlyItsConfiguredDestination()
    {
        foreach (var service in GatewayRoutes.Services)
            Assert.Equal(service.Id, await _client.GetStringAsync("/health/" + service.Id));
    }

    [Fact]
    public async Task GenericServiceRoutesPreserveMethodBodyQueryAndBearerHeader()
    {
        Authenticate(permission: true);
        foreach (var service in GatewayRoutes.Services)
        {
            using var response = await _client.PostAsync($"/services/{service.Id}/api/echo?page=2&sort=name%20asc", new StringContent("request-body"));
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = json.RootElement;
            Assert.Equal(service.Id, root.GetProperty("service").GetString()); Assert.Equal("POST", root.GetProperty("method").GetString());
            Assert.Equal("/api/echo", root.GetProperty("path").GetString()); Assert.Equal("request-body", root.GetProperty("body").GetString());
            Assert.Contains("page=2", root.GetProperty("query").GetString());
            Assert.Equal(_client.DefaultRequestHeaders.Authorization!.ToString(), root.GetProperty("authorization").GetString());
        }
    }

    [Theory]
    [InlineData("/api/admin/roles")]
    [InlineData("/api/users/7")]
    [InlineData("/api/account/profile")]
    [InlineData("/services/property/api/echo")]
    public async Task ProtectedApisRejectAnonymousAndInvalidTokensBeforeForwarding(string path)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(path)).StatusCode);
        _client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid.jwt");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(path)).StatusCode); Assert.Equal(0, _backendCalls);
    }

    [Fact]
    public async Task DownstreamPermissionChecksStillEnforceForbiddenAndAllowApprovedUser()
    {
        Authenticate(permission: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/admin/roles")).StatusCode);
        Authenticate(permission: true);
        Assert.Equal("roles", await _client.GetStringAsync("/api/admin/roles"));
    }

    [Fact]
    public async Task AnonymousRegistrationAndRecoveryReachUserServiceAndRetainStatusAndPublicLocation()
    {
        var registration = await _client.PostAsync("/api/users/register", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        Assert.Equal("https://gateway.test/api/users/7", registration.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsync("/api/account/password/forgot", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/account/password/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/users/register")).StatusCode);
    }

    [Fact]
    public async Task LoginRefreshAndSsoPassCookiesCsrfAndRedirectsWithoutRequiringBearer()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/login", null)).StatusCode);
        _client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "csrf-token");
        var login = await _client.PostAsync("/api/login", new StringContent("{}")); login.EnsureSuccessStatusCode();
        Assert.Equal(2, login.Headers.GetValues("Set-Cookie").Count());
        Assert.All(login.Headers.GetValues("Set-Cookie"), cookie => { Assert.Contains("Secure", cookie); Assert.Contains("HttpOnly", cookie); });
        _client.DefaultRequestHeaders.Add("Cookie", "__Host-EasyTolet.LoginSession=session; __Host-EasyTolet.Refresh=refresh");
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync("/api/login/refresh", null)).StatusCode);
        Assert.Equal("https://gateway.test/api/users/sso/complete", (await _client.GetAsync("/signin-google?state=test")).Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/users/sso/session")).StatusCode);
    }

    [Fact]
    public async Task SpoofedForwardingHeadersAreReplacedWithActualGatewayRequestMetadata()
    {
        Authenticate(true);
        _client.DefaultRequestHeaders.Add("X-Forwarded-Host", "evil.example");
        _client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");
        _client.DefaultRequestHeaders.Add("X-Forwarded-For", "1.2.3.4");
        _client.DefaultRequestHeaders.Add("Forwarded", "host=evil.example;proto=http");
        using var json = JsonDocument.Parse(await _client.GetStringAsync("/services/user/api/echo"));
        var root = json.RootElement;
        Assert.Equal("gateway.test", root.GetProperty("host").GetString()); Assert.Equal("https", root.GetProperty("scheme").GetString());
        Assert.Equal("203.0.113.25", root.GetProperty("ip").GetString()); Assert.Equal("", root.GetProperty("forwarded").GetString());
    }

    [Fact]
    public async Task SwaggerDocumentsProxyThroughTheGatewayAndUnavailableBackendReturns502()
    {
        using var json = JsonDocument.Parse(await _client.GetStringAsync("/docs/admin/swagger.json"));
        Assert.Equal("admin", json.RootElement.GetProperty("service").GetString());
        Authenticate(true);
        Assert.Equal(HttpStatusCode.BadGateway, (await _client.GetAsync("/api/properties/offline")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/services/not-configured/api/echo")).StatusCode);
    }

    private IConfiguration SecurityConfiguration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Jwt:Issuer"] = "https://issuer.test", ["Jwt:Audience"] = "EasyTolet.Api", ["Jwt:PublicKeyPem"] = _rsa.ExportSubjectPublicKeyInfoPem(),
        ["GatewayHeaders:KnownProxies:0"] = "127.0.0.1", ["GatewayHeaders:AllowedHosts:0"] = "gateway.test"
    }).Build();
    private void Authenticate(bool permission)
    {
        var claims = new List<Claim> { new("user_id", "7"), new("sub", "U7") };
        if (permission) claims.Add(new("permission", "admin.roles.manage"));
        var token = new JwtSecurityToken("https://issuer.test", "EasyTolet.Api", claims, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new(new RsaSecurityKey(_rsa), SecurityAlgorithms.RsaSha256));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
    private sealed class Factory(Dictionary<string, HttpMessageInvoker> clients) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(new Handler(clients));
    }
    private sealed class Handler(Dictionary<string, HttpMessageInvoker> clients) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/offline")) throw new HttpRequestException("Simulated unreachable backend");
            return clients[request.RequestUri.Host.Split('.')[0]].SendAsync(request, ct);
        }
    }
}
