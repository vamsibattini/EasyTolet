using System.Text.Json;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using EasyTolet.AdminService.Controllers;
using EasyTolet.Shared.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EasyTolet.AdminService.Tests.Controllers;

public sealed class AdminApiDocumentationTests
{
    [Fact]
    public async Task OpenApiExposesRoleCrudWithBearerSecurityAndAuthorizationFailures()
    {
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddAuthentication("Bearer").AddJwtBearer("Bearer", _ => { });
            services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
            services.AddControllers().AddApplicationPart(typeof(AdminController).Assembly);
            services.AddServiceApiDocumentation("EasyTolet Admin API");
        }).Configure(app => { app.UseSwagger(); app.UseSwaggerUI(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); })).StartAsync();
        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        foreach (var (route, method) in new[] { ("/api/admin/roles", "get"), ("/api/admin/roles", "post"),
            ("/api/admin/roles/{id}", "get"), ("/api/admin/roles/{id}", "put"), ("/api/admin/roles/{id}", "delete") })
        {
            var operation = paths.GetProperty(route).GetProperty(method);
            Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
            Assert.Contains("AdminRoles", operation.GetProperty("description").GetString());
            Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _));
            Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));
        }
        Assert.Equal("bearer", document.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer").GetProperty("scheme").GetString());
        Assert.True((await client.GetAsync("/swagger/index.html")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/not-a-public-route")).StatusCode);
        await host.StopAsync();
    }
}
