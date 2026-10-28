using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EasyTolet.Shared.Exceptions;

public sealed class ExceptionLoggingOptions
{
    public string Directory { get; set; } = "logs/exceptions";
    public bool DatabaseEnabled { get; set; } = true;
    public int SinkTimeoutSeconds { get; set; } = 5;
}

public sealed record ExceptionLogEntry(
    string Id, DateTime OccurredAtUtc, string Service, string Environment, string Instance,
    string CorrelationId, string TraceId, string Method, string Path, string? Controller,
    string? Action, string? Repository, int StatusCode, bool ResponseStarted, string ExceptionType,
    string Message, string ExceptionDetails);

public interface IExceptionLogSink
{
    Task WriteAsync(ExceptionLogEntry entry, CancellationToken cancellationToken);
}

public interface IExceptionReporter
{
    Task ReportAsync(ExceptionLogEntry entry);
}

/// <summary>Each destination is attempted independently; sink failures never replace the application error.</summary>
public sealed class ExceptionReporter(IEnumerable<IExceptionLogSink> sinks,
    IOptions<ExceptionLoggingOptions> options, ILogger<ExceptionReporter> logger) : IExceptionReporter
{
    public Task ReportAsync(ExceptionLogEntry entry) => Task.WhenAll(sinks.Select(sink => WriteSafelyAsync(sink, entry)));

    private async Task WriteSafelyAsync(IExceptionLogSink sink, ExceptionLogEntry entry)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.SinkTimeoutSeconds));
        try
        {
            await sink.WriteAsync(entry, timeout.Token);
        }
        catch (Exception failure)
        {
            // Do not send the failed provider exception/connection string to console logging.
            logger.LogError("Exception log {LogId}: sink {Sink} failed ({FailureType}). Other destinations were attempted.",
                entry.Id, sink.GetType().Name, failure.GetType().Name);
        }
    }
}

public sealed class ExceptionLogEntryFactory(IHostEnvironment environment, TimeProvider timeProvider)
{
    // Separate files per process avoid append collisions between replicas sharing a volume.
    public string Instance { get; } = $"{System.Environment.ProcessId}-{Guid.NewGuid():N}";

    public ExceptionLogEntry Create(HttpContext context, Exception exception, string correlationId, int statusCode)
    {
        var descriptor = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        return new ExceptionLogEntry(Guid.NewGuid().ToString("N"), timeProvider.GetUtcNow().UtcDateTime,
            Limit(environment.ApplicationName, 150), Limit(environment.EnvironmentName, 50), Instance,
            correlationId, Activity.Current?.TraceId.ToString() ?? correlationId,
            Limit(context.Request.Method, 16), Redact(Limit(context.Request.Path.Value ?? "", 2048)),
            descriptor is null ? null : Limit(descriptor.ControllerTypeInfo.FullName ?? descriptor.ControllerName, 512),
            descriptor is null ? null : Limit(descriptor.MethodInfo.Name, 256),
            RepositoryNames(exception), statusCode, context.Response.HasStarted, Limit(exception.GetType().FullName ?? exception.GetType().Name, 512),
            Redact(exception.Message), Redact(exception.ToString()));
    }

    private static string? RepositoryNames(Exception exception)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Visit(Exception error)
        {
            foreach (var frame in new StackTrace(error).GetFrames())
            {
                var type = frame.GetMethod()?.DeclaringType;
                while (type?.DeclaringType is not null) type = type.DeclaringType;
                if (type?.Name.Contains("Repository", StringComparison.Ordinal) == true)
                    names.Add(type.FullName ?? type.Name);
            }
            if (error is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) Visit(inner);
            else if (error.InnerException is not null) Visit(error.InnerException);
        }
        Visit(exception);
        return names.Count == 0 ? null : string.Join("\n", names.Order(StringComparer.Ordinal));
    }

    // Defense in depth for common secret formats; applications must never put arbitrary secrets in exception messages.
    public static string Redact(string value)
    {
        try
        {
            value = Regex.Replace(value,
                "(?i)(\\b(?:password|pwd|token|access_token|refresh_token|api[_-]?key|secret)\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^;\\s,]+)",
                "$1[REDACTED]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return Regex.Replace(value, "(?i)\\bBearer\\s+[A-Za-z0-9._~+/-]+=*", "Bearer [REDACTED]",
                RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }
        catch (RegexMatchTimeoutException)
        {
            return "[Details omitted: secret redaction exceeded its time limit.]";
        }
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
}
