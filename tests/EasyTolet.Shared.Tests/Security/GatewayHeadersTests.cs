using System.Net;
using System.Text.Json;
using EasyTolet.Shared.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace EasyTolet.Shared.Tests.Security;

public sealed class GatewayHeadersTests
{
    [Theory]
    [InlineData(true, "127.0.0.1", "gateway.test", true)]
    [InlineData(true, "192.0.2.42", "gateway.test", false)]
    [InlineData(false, "127.0.0.1", "gateway.test", false)]
    [InlineData(true, "127.0.0.1", "evil.test", false)]
    public async Task OnlyExplicitlyTrustedGatewayAndAllowedHostCanChangeRequestOrigin(bool configured, string remoteIp, string forwardedHost, bool accepted)
    {
        var values = new Dictionary<string, string?>();
        if (configured) { values["GatewayHeaders:KnownProxies:0"] = "127.0.0.1"; values["GatewayHeaders:AllowedHosts:0"] = "gateway.test"; }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => services.AddTrustedGatewayHeaders(configuration))
            .Configure(app =>
            {
                app.Use((context, next) => { context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp); return next(context); });
                app.UseForwardedHeaders();
                app.Run(context => context.Response.WriteAsJsonAsync(new { scheme = context.Request.Scheme, host = context.Request.Host.Value, ip = context.Connection.RemoteIpAddress!.ToString() }));
            })).StartAsync();
        using var client = host.GetTestClient(); client.BaseAddress = new Uri("http://backend.test");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", forwardedHost); client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.25");
        using var json = JsonDocument.Parse(await client.GetStringAsync("/"));
        Assert.Equal(accepted ? "gateway.test" : "backend.test", json.RootElement.GetProperty("host").GetString());
        if (accepted)
        {
            Assert.Equal("https", json.RootElement.GetProperty("scheme").GetString());
            Assert.Equal("203.0.113.25", json.RootElement.GetProperty("ip").GetString());
        }
        await host.StopAsync();
    }

    [Fact]
    public void TrustConfigurationRequiresExplicitPublicHostNames()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["GatewayHeaders:KnownProxies:0"] = "127.0.0.1", ["GatewayHeaders:AllowedHosts:0"] = "*" }).Build();
        using var provider = new ServiceCollection().AddTrustedGatewayHeaders(configuration).BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value);
    }
}
