using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EasyTolet.Shared.Hosting;

public static class GatewayHeaders
{
    public static IServiceCollection AddTrustedGatewayHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
            options.ForwardLimit = 1;
            options.RequireHeaderSymmetry = true;
            var addresses = configuration.GetSection("GatewayHeaders:KnownProxies").Get<string[]>() ?? [];
            // No configured gateway means no forwarding trust, rather than trusting every sender.
            if (addresses.Length == 0) { options.ForwardedHeaders = ForwardedHeaders.None; return; }
            foreach (var address in addresses)
            {
                if (!IPAddress.TryParse(address, out var ip)) throw new InvalidOperationException("GatewayHeaders:KnownProxies must contain IP addresses.");
                options.KnownProxies.Add(ip);
            }
            var hosts = configuration.GetSection("GatewayHeaders:AllowedHosts").Get<string[]>() ?? [];
            if (hosts.Length == 0 || hosts.Any(host => string.IsNullOrWhiteSpace(host) || host.Contains('*')))
                throw new InvalidOperationException("GatewayHeaders:AllowedHosts must list the gateway's public host names explicitly.");
            options.AllowedHosts = hosts;
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
        });
        return services;
    }
}
