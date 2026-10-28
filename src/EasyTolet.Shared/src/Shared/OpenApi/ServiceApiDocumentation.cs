using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace EasyTolet.Shared.OpenApi;

public static class ServiceApiDocumentation
{
    public static IServiceCollection AddServiceApiDocumentation(this IServiceCollection services, string title,
        IReadOnlyDictionary<string, string>? cookieSchemes = null)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = title, Version = "v1" });
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
                Description = "Paste the access token returned by login. Required permissions are enforced by the API."
            });
            if (cookieSchemes is not null)
                foreach (var (scheme, cookie) in cookieSchemes)
                    options.AddSecurityDefinition(scheme, new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = cookie,
                        Description = "Issued by the login/SSO flow. The browser must retain this secure HttpOnly cookie; pasting a JWT does not replace it."
                    });
            options.OperationFilter<EndpointSecurityDocumentation>();
        });
        return services;
    }
}

public sealed class EndpointSecurityDocumentation : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        operation.Security = [];
        if (metadata.OfType<IAllowAnonymous>().Any()) return;
        var authorization = metadata.OfType<IAuthorizeData>().ToArray();
        var schemes = authorization.SelectMany(a => (a.AuthenticationSchemes ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal).ToArray();
        // The service's fallback policy also protects endpoints without an explicit attribute.
        if (schemes.Length == 0) schemes = ["Bearer"];
        foreach (var scheme in schemes)
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = scheme } }] = []
            });
        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Authentication is missing or invalid." });
        operation.Responses.TryAdd("403", new OpenApiResponse { Description = "The authenticated user is not authorized for this operation." });
        var policies = authorization.Where(a => !string.IsNullOrEmpty(a.Policy)).Select(a => a.Policy).Distinct();
        if (policies.Any()) operation.Description = (operation.Description + "\nAuthorization policies: " + string.Join(", ", policies) + ".").Trim();
    }
}
