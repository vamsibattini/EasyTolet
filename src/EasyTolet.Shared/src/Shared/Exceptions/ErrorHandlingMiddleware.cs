using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace EasyTolet.Shared.Exceptions;

/// <summary>
/// Converts unhandled exceptions into a consistent RFC 7807-style response.
/// </summary>
public sealed class ErrorHandlingMiddleware
{
    public const string CorrelationIdHeaderName = "X-Correlation-ID";
    public const string CorrelationIdItemKey = "CorrelationId";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly RequestDelegate _next;
    private readonly IExceptionReporter _reporter;
    private readonly ExceptionLogEntryFactory _entryFactory;

    public ErrorHandlingMiddleware(RequestDelegate next, IExceptionReporter reporter, ExceptionLogEntryFactory entryFactory)
    {
        _next = next;
        _reporter = reporter;
        _entryFactory = entryFactory;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = GetOrCreateCorrelationId(context);
        context.Items[CorrelationIdItemKey] = correlationId;
        context.TraceIdentifier = correlationId;
        context.Response.Headers[CorrelationIdHeaderName] = correlationId;

        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // A disconnected client cannot receive a problem response.
            throw;
        }
        catch (Exception exception)
        {
            var statusCode = exception is ApiException api && api.StatusCode is >= 400 and <= 599
                ? api.StatusCode : StatusCodes.Status500InternalServerError;
            await _reporter.ReportAsync(_entryFactory.Create(context, exception, correlationId, statusCode));
            if (context.Response.HasStarted) throw;
            if (context.RequestAborted.IsCancellationRequested) return;
            await HandleExceptionAsync(context, statusCode, correlationId);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, int statusCode, string correlationId)
    {
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json; charset=utf-8";
        context.Response.Headers[CorrelationIdHeaderName] = correlationId;

        var response = new ErrorResponse(
            Type: $"https://httpstatuses.com/{statusCode}",
            Title: GetTitle(statusCode),
            Status: statusCode,
            Detail: GetSafeDetail(statusCode),
            Instance: context.Request.Path.Value,
            CorrelationId: correlationId);

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            response,
            SerializerOptions,
            context.RequestAborted);
    }

    private static string GetOrCreateCorrelationId(HttpContext context)
    {
        var candidate = context.Request.Headers[CorrelationIdHeaderName].ToString();

        return IsValidCorrelationId(candidate)
            ? candidate
            : Guid.NewGuid().ToString("N");
    }

    private static bool IsValidCorrelationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private static string GetTitle(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "Bad request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status422UnprocessableEntity => "Unprocessable entity",
        StatusCodes.Status429TooManyRequests => "Too many requests",
        _ when statusCode >= StatusCodes.Status400BadRequest && statusCode < StatusCodes.Status500InternalServerError => "Request could not be completed",
        _ => "An unexpected error occurred"
    };

    private static string GetSafeDetail(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "The request is invalid.",
        StatusCodes.Status401Unauthorized => "Authentication is required.",
        StatusCodes.Status403Forbidden => "You do not have permission to perform this action.",
        StatusCodes.Status404NotFound => "The requested resource was not found.",
        StatusCodes.Status409Conflict => "The request conflicts with the current state of the resource.",
        StatusCodes.Status422UnprocessableEntity => "The request could not be processed.",
        StatusCodes.Status429TooManyRequests => "Too many requests were received. Please try again later.",
        _ when statusCode >= StatusCodes.Status400BadRequest && statusCode < StatusCodes.Status500InternalServerError => "The request could not be completed.",
        _ => "An unexpected error occurred while processing the request."
    };

    private sealed record ErrorResponse(
        string Type,
        string Title,
        int Status,
        string Detail,
        string? Instance,
        string CorrelationId);
}
