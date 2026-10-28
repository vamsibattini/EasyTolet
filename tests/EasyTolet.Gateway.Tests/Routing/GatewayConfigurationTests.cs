using EasyTolet.Gateway.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EasyTolet.Gateway.Tests.Routing;

public sealed class GatewayConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:password@backend.test/")]
    [InlineData("https://backend.test/api/")]
    [InlineData("https://backend.test/?target=other")]
    public void InvalidDestinationConfigurationFailsBeforeProxyStarts(string? address)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["DownstreamServices:identity:Address"] = address }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddGatewayRouting(configuration, true));
    }

    [Fact]
    public void ProductionDoesNotExposeAnonymousSwaggerProxyRoutes()
    {
        var routes = GatewayRoutes.CreateRoutes(false);
        Assert.DoesNotContain(routes, route => route.RouteId.EndsWith("-swagger"));
        Assert.All(routes.Where(route => route.RouteId.EndsWith("-service")), route => Assert.Equal("default", route.AuthorizationPolicy));
    }
}
