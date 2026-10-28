using EasyTolet.Shared.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EasyTolet.Shared.Controllers;

/// <summary>
/// Common controller dependencies. TContext must belong to the current service.
/// Unhandled exceptions are logged by middleware; report only intentionally handled failures here.
/// </summary>
public abstract class ServiceControllerBase<TContext> : ControllerBase where TContext : DbContext
{
    protected TContext Database { get; }
    protected IExceptionReporter ExceptionReporter { get; }

    protected ServiceControllerBase(TContext database, IExceptionReporter exceptionReporter)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(exceptionReporter);
        Database = database;
        ExceptionReporter = exceptionReporter;
    }
}
