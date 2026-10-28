using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EasyTolet.UserService.Authentication;

// API-only equivalent of the MVC view filter; does not require registering Razor/view services.
public sealed class SsoAntiforgeryFilter(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!await antiforgery.IsRequestValidAsync(context.HttpContext))
            context.Result = new BadRequestObjectResult(new ProblemDetails
            {
                Status = 400, Title = "Invalid antiforgery token.",
                Detail = "Refresh the SSO page and send its CSRF token in X-CSRF-TOKEN."
            });
    }
}
