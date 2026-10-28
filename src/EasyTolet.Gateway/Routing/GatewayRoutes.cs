using Yarp.ReverseProxy.Configuration;

namespace EasyTolet.Gateway.Routing;

public sealed record DownstreamService(string Id, string ApiPrefix, int Port);

public static class GatewayRoutes
{
    public static readonly IReadOnlyList<DownstreamService> Services = Array.AsReadOnly(new[]
    {
        new DownstreamService("identity", "/api/identity", 7002), new("user", "/api/users", 7003),
        new("property", "/api/properties", 7004), new("search", "/api/search", 7005),
        new("booking", "/api/bookings", 7006), new("payment", "/api/payments", 7007),
        new("agreement", "/api/agreements", 7008), new("notification", "/api/notifications", 7009),
        new("chat", "/api/chat", 7010), new("review", "/api/reviews", 7011),
        new("wishlist", "/api/wishlist", 7012), new("media", "/api/media", 7013),
        new("analytics", "/api/analytics", 7014), new("admin", "/api/admin", 7015)
    });

    public static IServiceCollection AddGatewayRouting(this IServiceCollection services, IConfiguration configuration, bool development)
    {
        var clusters = Services.Select(service => new ClusterConfig
        {
            ClusterId = service.Id,
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["primary"] = new() { Address = Destination(configuration, service.Id) }
            }
        }).ToArray();
        services.AddReverseProxy().LoadFromMemory(CreateRoutes(development), clusters);
        return services;
    }

    public static IReadOnlyList<RouteConfig> CreateRoutes(bool development)
    {
        var routes = new List<RouteConfig>();
        foreach (var service in Services)
        {
            routes.Add(Route(service.Id + "-api", service.Id, service.ApiPrefix + "/{**remainder}"));
            // A uniform path can reach any future downstream API without exposing an arbitrary target URL.
            routes.Add(Route(service.Id + "-service", service.Id, "/services/" + service.Id + "/{**remainder}",
                transform: new() { ["PathRemovePrefix"] = "/services/" + service.Id }));
            routes.Add(Route(service.Id + "-health", service.Id, "/health/" + service.Id, anonymous: true,
                methods: ["GET"], transform: new() { ["PathSet"] = "/api/health" }));
        }
        routes.Add(Route("user-account", "user", "/api/account/{**remainder}"));
        // These flows use downstream-owned cookies/CSRF or are explicitly anonymous. They must not require a JWT at the gateway.
        routes.Add(Route("user-login", "user", "/api/login/{**remainder}", anonymous: true));
        routes.Add(Route("user-sso", "user", "/api/users/sso/{**remainder}", anonymous: true, order: -10));
        routes.Add(Route("user-register", "user", "/api/users/register", anonymous: true, methods: ["POST"], order: -10));
        foreach (var action in new[] { "forgot", "reset" })
            routes.Add(Route("user-password-" + action, "user", "/api/account/password/" + action, anonymous: true, methods: ["POST"], order: -10));
        foreach (var provider in new[] { "google", "microsoft", "facebook" })
            routes.Add(Route("oauth-" + provider, "user", "/signin-" + provider, anonymous: true, methods: ["GET", "POST"]));
        if (development)
            foreach (var service in new[] { "admin", "user" })
                routes.Add(Route(service + "-swagger", service, "/docs/" + service + "/swagger.json", anonymous: true,
                    methods: ["GET"], transform: new() { ["PathSet"] = "/swagger/v1/swagger.json" }));
        return routes;
    }

    private static RouteConfig Route(string id, string cluster, string path, bool anonymous = false, string[]? methods = null,
        int order = 0, Dictionary<string, string>? transform = null)
    {
        var transforms = new List<IReadOnlyDictionary<string, string>>
        {
            // Replace client-supplied forwarded values; downstream trusts only this gateway's address.
            new Dictionary<string, string> { ["X-Forwarded"] = "Set" },
            new Dictionary<string, string> { ["RequestHeaderRemove"] = "Forwarded" },
            new Dictionary<string, string> { ["RequestHeaderRemove"] = "X-Original-For" },
            new Dictionary<string, string> { ["RequestHeaderRemove"] = "X-Original-Host" },
            new Dictionary<string, string> { ["RequestHeaderRemove"] = "X-Original-Proto" },
            new Dictionary<string, string> { ["RequestHeaderRemove"] = "X-Original-Prefix" }
        };
        if (transform is not null) transforms.Add(transform);
        return new()
        {
            RouteId = id, ClusterId = cluster, Order = order, Match = new() { Path = path, Methods = methods },
            AuthorizationPolicy = anonymous ? "anonymous" : "default", Transforms = transforms
        };
    }

    private static string Destination(IConfiguration configuration, string id)
    {
        var address = configuration[$"DownstreamServices:{id}:Address"];
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw new InvalidOperationException($"DownstreamServices:{id}:Address must be an HTTP(S) origin without a path, credentials, query or fragment.");
        return uri.AbsoluteUri;
    }
}
