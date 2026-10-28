using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EasyTolet.Shared.Exceptions;

public static class ExceptionHandlingExtensions
{
    public static IServiceCollection AddGlobalExceptionHandling(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ExceptionLoggingOptions>().Bind(configuration.GetSection("ExceptionLogging"))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Directory), "Exception log directory is required.")
            .Validate(options => options.SinkTimeoutSeconds is >= 1 and <= 30, "Exception sink timeout must be between 1 and 30 seconds.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ExceptionLogEntryFactory>();
        services.TryAddSingleton<IExceptionReporter, ExceptionReporter>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionLogSink, DailyTextExceptionLogSink>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionLogSink, MySqlExceptionLogSink>());
        return services;
    }

    public static IApplicationBuilder UseGlobalExceptionHandling(this IApplicationBuilder app) =>
        app.UseMiddleware<ErrorHandlingMiddleware>();
}
