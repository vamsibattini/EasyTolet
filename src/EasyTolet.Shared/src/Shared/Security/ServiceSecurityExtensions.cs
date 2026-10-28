using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace EasyTolet.Shared.Security;

public static class ServiceSecurityExtensions
{
    public static IServiceCollection AddServiceSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = true;
            options.IncludeErrorDetails = false;
            var audience = configuration["Jwt:Audience"];
            if (string.IsNullOrWhiteSpace(audience)) throw new InvalidOperationException("Jwt:Audience is required.");
            options.Audience = audience;
            var validation = options.TokenValidationParameters;
            validation.ValidateIssuer = true;
            validation.ValidateAudience = true;
            validation.ValidateLifetime = true;
            validation.ValidateIssuerSigningKey = true;
            validation.RequireSignedTokens = true;
            validation.RequireExpirationTime = true;
            validation.ClockSkew = TimeSpan.Zero;
            validation.NameClaimType = "sub";
            validation.RoleClaimType = "role";
            validation.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
            var publicKey = configuration["Jwt:PublicKeyPem"];
            if (!string.IsNullOrWhiteSpace(publicKey))
            {
                var issuer = configuration["Jwt:Issuer"];
                if (string.IsNullOrWhiteSpace(issuer)) throw new InvalidOperationException("Jwt:Issuer is required with Jwt:PublicKeyPem.");
                using var rsa = RSA.Create();
                rsa.ImportFromPem(publicKey);
                if (rsa.KeySize < 2048) throw new InvalidOperationException("JWT RSA keys must be at least 2048 bits.");
                // Validation services receive only the public key. No private signing material is needed.
                validation.IssuerSigningKey = new RsaSecurityKey(rsa.ExportParameters(false));
                validation.ValidIssuer = issuer;
                validation.ValidAudience = audience;
            }
            else
            {
                var authority = configuration["Jwt:Authority"];
                if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("Configure Jwt:PublicKeyPem and Jwt:Issuer, or an HTTPS Jwt:Authority.");
                options.Authority = authority;
            }
        });
        services.AddRolePermissionAuthorization();
        return services;
    }

    public static IServiceCollection AddRolePermissionAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser().Build());
        services.Replace(ServiceDescriptor.Singleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>());
        return services;
    }

    public static IApplicationBuilder UseServiceSecurity(this IApplicationBuilder app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }
}
