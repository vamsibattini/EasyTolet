using EasyTolet.Shared.OpenApi;
using EasyTolet.UserService.Authentication;
using EasyTolet.UserService.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace EasyTolet.UserService;

public static class UserApiDocumentation
{
    public static IServiceCollection AddUserApiDocumentation(this IServiceCollection services)
    {
        services.AddServiceApiDocumentation("EasyTolet User API", new Dictionary<string, string>
        {
            [LoginController.SessionScheme] = "__Host-EasyTolet.LoginSession",
            [SsoSchemes.External] = "__Host-EasyTolet.SsoExternal",
            [SsoSchemes.Session] = "__Host-EasyTolet.SsoSession"
        });
        services.AddSwaggerGen(options => options.OperationFilter<UserCsrfDocumentation>());
        return services;
    }
}

public sealed class UserCsrfDocumentation : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<ServiceFilterAttribute>()
            .Any(filter => filter.ServiceType == typeof(SsoAntiforgeryFilter))) return;
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-CSRF-TOKEN", In = ParameterLocation.Header, Required = true,
            Schema = new OpenApiSchema { Type = "string" },
            Description = "Use the latest csrfToken from login/SSO and retain its matching cookies. Before password login, obtain it from GET /api/login/csrf."
        });
        operation.Responses.TryAdd("400", new OpenApiResponse { Description = "Invalid input or missing/invalid CSRF token and cookie." });
    }
}
