using System.Text.Json;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using EasyTolet.UserService.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EasyTolet.UserService.Tests.Controllers;

public sealed class UserApiDocumentationTests
{
    [Fact]
    public async Task OpenApiPublishesRoutesAndDistinguishesBearerAnonymousAndCookieCsrfFlows()
    {
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddAuthentication("Bearer").AddJwtBearer("Bearer", _ => { });
            services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
            services.AddControllers().AddApplicationPart(typeof(UsersController).Assembly);
            services.AddUserApiDocumentation();
        }).Configure(app => { app.UseSwagger(); app.UseSwaggerUI(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); })).StartAsync();
        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        foreach (var route in new[] { "/api/users/register", "/api/users/{id}", "/api/login", "/api/login/refresh", "/api/account/profile",
            "/api/account/password/reset", "/api/account/verification/confirm", "/api/account/deletion/confirm", "/api/account/sessions/{sessionId}", "/api/users/sso/{provider}/login" })
            Assert.True(paths.TryGetProperty(route, out _), route);
        Assert.False(paths.GetProperty("/api/users/register").GetProperty("post").TryGetProperty("security", out var publicSecurity) && publicSecurity.GetArrayLength() > 0);
        Assert.False(paths.GetProperty("/api/account/password/reset").GetProperty("post").TryGetProperty("security", out var resetSecurity) && resetSecurity.GetArrayLength() > 0);
        Assert.True(paths.GetProperty("/api/account/profile").GetProperty("get").GetProperty("security")[0].TryGetProperty("Bearer", out _));
        var refresh = paths.GetProperty("/api/login/refresh").GetProperty("post");
        Assert.True(refresh.GetProperty("security")[0].TryGetProperty("UserLoginSession", out _));
        Assert.Contains(refresh.GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == "X-CSRF-TOKEN" && parameter.GetProperty("required").GetBoolean());
        var login = paths.GetProperty("/api/login").GetProperty("post");
        Assert.False(login.TryGetProperty("security", out var loginSecurity) && loginSecurity.GetArrayLength() > 0);
        Assert.Contains(login.GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == "X-CSRF-TOKEN");
        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        Assert.Equal("bearer", schemes.GetProperty("Bearer").GetProperty("scheme").GetString());
        Assert.Equal("__Host-EasyTolet.LoginSession", schemes.GetProperty("UserLoginSession").GetProperty("name").GetString());
        Assert.Contains("ManageUsers", paths.GetProperty("/api/users").GetProperty("get").GetProperty("description").GetString());
        Assert.True((await client.GetAsync("/swagger/index.html")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/not-a-public-route")).StatusCode);
        await host.StopAsync();
    }
}
