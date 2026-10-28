using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EasyTolet.Shared.Controllers;
using EasyTolet.Shared.Exceptions;

namespace EasyTolet.BookingService.Controllers;

[AllowAnonymous]
[ApiController]
[Route("api/[controller]")]
public class HealthController : ServiceControllerBase<AppDbContext>
{
    public HealthController(AppDbContext database, IExceptionReporter exceptionReporter)
        : base(database, exceptionReporter)
    {
    }

    [HttpGet]
    public IActionResult Get() => Ok(new { status = "Healthy", service = "EasyTolet.BookingService" });
}
