using System.Text.Json;
using EasyTolet.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using EasyTolet.UserService.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace EasyTolet.UserService.Tests.Controllers;

[Trait("Category", "Unit")]
public sealed class HealthControllerTests
{
    [Fact]
    public void Get_ReturnsOkWithHealthyStatusAndCorrectServiceIdentity()
    {
        using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().Options);
        var controller = new HealthController(database, new UnexpectedExceptionReporter());

        var response = controller.Get();

        var result = Assert.IsType<OkObjectResult>(response);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.NotNull(result.Value);
        var payload = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal("Healthy", payload.GetProperty("status").GetString());
        Assert.Equal("EasyTolet.UserService", payload.GetProperty("service").GetString());
    }
    [Fact]
    public void EveryControllerRequiresItsServiceContextAndExceptionReporter()
    {
        var controllers = typeof(HealthController).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type)).ToArray();
        Assert.NotEmpty(controllers);
        foreach (var controller in controllers)
        {
            var constructors = controller.GetConstructors();
            Assert.NotEmpty(constructors);
            foreach (var constructor in constructors)
            {
                var dependencies = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
                Assert.Contains(typeof(AppDbContext), dependencies);
                Assert.Contains(typeof(IExceptionReporter), dependencies);
            }
        }
    }

    private sealed class UnexpectedExceptionReporter : IExceptionReporter
    {
        public Task ReportAsync(ExceptionLogEntry entry) =>
            throw new InvalidOperationException("A successful health request must not report an exception.");
    }
}
