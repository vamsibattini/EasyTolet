using System.Security.Claims;
using EasyTolet.Shared.Exceptions;
using EasyTolet.UserService.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EasyTolet.UserService.Tests.Authentication;

public sealed class SsoConfigurationTests
{
    [Fact]
    public async Task ProvidersAreDisabledByDefaultAndBearerRemainsDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication("Bearer");
        services.AddUserSso(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var schemes = await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        Assert.DoesNotContain(schemes, scheme => SsoSchemes.Providers.Contains(scheme.Name));
        Assert.Equal("Bearer", provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value.DefaultScheme);
    }

    [Fact]
    public void EnabledProviderRejectsMissingCredentials()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUserSso(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Authentication:Google:Enabled"] = "true" }).Build());
        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptionsMonitor<GoogleOptions>>().Get("Google"));
    }

    [Fact]
    public void UnauthenticatedClaimsCannotBecomeSsoIdentity()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        { new Claim(SsoSchemes.ProviderClaim, "Google"), new Claim(ClaimTypes.NameIdentifier, "victim") }));
        Assert.Equal(401, Assert.Throws<ApiException>(() => SsoIdentity.FromPrincipal(principal)).StatusCode);
    }

    [Fact]
    public void MissingStableSubjectIsRejectedEvenWhenEmailIsPresent()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        { new Claim(SsoSchemes.ProviderClaim, "Google"), new Claim(ClaimTypes.Email, "victim@example.com") }, "Google"));
        Assert.Equal(401, Assert.Throws<ApiException>(() => SsoIdentity.FromPrincipal(principal)).StatusCode);
    }
}
