using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using EasyTolet.Shared.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EasyTolet.Shared.Tests;

public sealed class ExceptionHandlingTests
{
    [Fact]
    public async Task MiddlewareCapturesControllerAndRepositoryStackWithoutReturningDetails()
    {
        var reporter = new CaptureReporter();
        var context = Context();
        context.Request.Headers[ErrorHandlingMiddleware.CorrelationIdHeaderName] = "test-123";
        context.Request.QueryString = new QueryString("?password=do-not-log");
        context.Request.Headers.Authorization = "Bearer do-not-log";
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(
            new ControllerActionDescriptor
            {
                ControllerName = "Test", ControllerTypeInfo = typeof(ExceptionProbeController).GetTypeInfo(),
                MethodInfo = typeof(ExceptionProbeController).GetMethod(nameof(ExceptionProbeController.Get))!
            }), "test"));
        var middleware = Middleware(_ => ThrowFromRepository(), reporter);
        await middleware.InvokeAsync(context);
        var entry = Assert.Single(reporter.Entries);
        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("test-123", entry.CorrelationId);
        Assert.Contains(nameof(ExceptionProbeController), entry.Controller);
        Assert.Equal("Get", entry.Action);
        Assert.Contains(nameof(ThrowFromRepository), entry.ExceptionDetails);
        Assert.Contains("inner detail", entry.ExceptionDetails);
        Assert.DoesNotContain("secret-value", entry.ExceptionDetails);
        Assert.DoesNotContain("do-not-log", JsonSerializer.Serialize(entry));
        var body = await Body(context);
        Assert.DoesNotContain("inner detail", body);
        Assert.DoesNotContain(nameof(ThrowFromRepository), body);
        Assert.Contains("test-123", body);
    }

    [Theory]
    [InlineData(400, 400)]
    [InlineData(404, 404)]
    [InlineData(409, 409)]
    [InlineData(200, 500)]
    [InlineData(700, 500)]
    public async Task ApiExceptionUsesOnlyValidErrorStatus(int supplied, int expected)
    {
        var context = Context();
        await Middleware(_ => throw new ApiException("private details", supplied), new CaptureReporter()).InvokeAsync(context);
        Assert.Equal(expected, context.Response.StatusCode);
        Assert.DoesNotContain("private details", await Body(context));
    }

    [Fact]
    public async Task RepositoryNameIsCapturedFromOriginalStack()
    {
        var reporter = new CaptureReporter();
        await Middleware(_ => new ExceptionProbeRepository().ReadAsync(), reporter).InvokeAsync(Context());
        Assert.Contains(nameof(ExceptionProbeRepository), Assert.Single(reporter.Entries).Repository);
    }

    [Theory]
    [InlineData("Password=supersecret;Server=localhost", "supersecret")]
    [InlineData("token='super secret'", "super secret")]
    [InlineData("Bearer abc.def.ghi", "abc.def.ghi")]
    public void KnownSecretsAreRedacted(string input, string secret)
    {
        Assert.DoesNotContain(secret, ExceptionLogEntryFactory.Redact(input));
        Assert.Contains("[REDACTED]", ExceptionLogEntryFactory.Redact(input));
    }

    [Fact]
    public async Task SuccessfulRequestsDoNotWriteLogsAndInvalidCorrelationIsReplaced()
    {
        var context = Context();
        context.Request.Headers[ErrorHandlingMiddleware.CorrelationIdHeaderName] = "invalid header value";
        var reporter = new CaptureReporter();
        await Middleware(_ => Task.CompletedTask, reporter).InvokeAsync(context);
        Assert.Empty(reporter.Entries);
        Assert.True(Guid.TryParseExact(context.TraceIdentifier, "N", out _));
    }

    [Fact]
    public async Task ClientCancellationIsNotReportedAsApplicationFailure()
    {
        var context = Context();
        context.RequestAborted = new CancellationToken(true);
        var reporter = new CaptureReporter();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Middleware(_ => throw new OperationCanceledException(context.RequestAborted), reporter).InvokeAsync(context));
        Assert.Empty(reporter.Entries);
    }

    [Fact]
    public async Task StartedResponseIsLoggedAndOriginalExceptionRethrown()
    {
        var context = Context();
        var reporter = new CaptureReporter();
        var exception = new InvalidOperationException("already started");
        var middleware = Middleware(ctx =>
        {
            ctx.Features.Set<IHttpResponseFeature>(new StartedResponse());
            throw exception;
        }, reporter);
        Assert.Same(exception, await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context)));
        Assert.True(Assert.Single(reporter.Entries).ResponseStarted);
    }

    [Fact]
    public async Task EachSinkIsAttemptedEvenIfOtherSinkFails()
    {
        var successful = new CaptureSink();
        var reporter = new ExceptionReporter([new FailingSink(), successful], Options.Create(new ExceptionLoggingOptions()),
            NullLogger<ExceptionReporter>.Instance);
        await reporter.ReportAsync(Entry());
        Assert.Single(successful.Entries);
        var other = new CaptureSink();
        await new ExceptionReporter([other, new FailingSink()], Options.Create(new ExceptionLoggingOptions()),
            NullLogger<ExceptionReporter>.Instance).ReportAsync(Entry());
        Assert.Single(other.Entries);
    }

    [Fact]
    public async Task BothLoggingFailuresDoNotChangeHttpProblemResponse()
    {
        var reporter = new ExceptionReporter([new FailingSink(), new FailingSink()], Options.Create(new ExceptionLoggingOptions()),
            NullLogger<ExceptionReporter>.Instance);
        var context = Context();
        await Middleware(_ => throw new InvalidOperationException("original"), reporter).InvokeAsync(context);
        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("application/problem+json; charset=utf-8", context.Response.ContentType);
    }

    [Fact]
    public async Task SinkCancellationHasBoundedWaitAndOtherSinkStillWrites()
    {
        var successful = new CaptureSink();
        var options = Options.Create(new ExceptionLoggingOptions { SinkTimeoutSeconds = 1 });
        await new ExceptionReporter([new WaitingSink(), successful], options, NullLogger<ExceptionReporter>.Instance)
            .ReportAsync(Entry()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(successful.Entries);
    }

    [Fact]
    public async Task DailyFilesRollOverAndConcurrentRecordsRemainReadable()
    {
        var root = Path.Combine(Path.GetTempPath(), "easytolet-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var sink = new DailyTextExceptionLogSink(Options.Create(new ExceptionLoggingOptions { Directory = root }), new TestEnvironment());
            var first = Entry() with { OccurredAtUtc = new DateTime(2026, 9, 19, 23, 59, 59, DateTimeKind.Utc) };
            await Task.WhenAll(Enumerable.Range(0, 30).Select(i => sink.WriteAsync(first with { Id = i.ToString() }, default)));
            await sink.WriteAsync(first with { OccurredAtUtc = first.OccurredAtUtc.AddSeconds(1) }, default);
            var files = Directory.GetFiles(root, "*.txt", SearchOption.AllDirectories);
            Assert.Equal(2, files.Length);
            var lines = await File.ReadAllLinesAsync(files.Single(f => f.EndsWith("2026-09-19.txt")));
            Assert.Equal(30, lines.Length);
            Assert.Equal(30, lines.Select(line => JsonSerializer.Deserialize<ExceptionLogEntry>(line)!.Id).Distinct().Count());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RealHttpPipelineUsesSharedHandlerAndCapturesRoutedController()
    {
        var reporter = new CaptureReporter();
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(ExceptionProbeController).Assembly);
                services.AddGlobalExceptionHandling(new ConfigurationBuilder().Build());
                services.AddSingleton<IExceptionReporter>(reporter);
            })
            .Configure(app =>
            {
                app.UseGlobalExceptionHandling();
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            })).StartAsync();
        using var response = await host.GetTestClient().GetAsync("/test/exception-probe");
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var entry = Assert.Single(reporter.Entries);
        Assert.Contains(nameof(ExceptionProbeController), entry.Controller);
        Assert.Equal(entry.CorrelationId, response.Headers.GetValues(ErrorHandlingMiddleware.CorrelationIdHeaderName).Single());
    }

    [Fact]
    public void RegistrationIsIdempotentAndDoesNotRequireDbContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment());
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddGlobalExceptionHandling(config).AddGlobalExceptionHandling(config);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.Equal(2, provider.GetServices<IExceptionLogSink>().Count());
        Assert.NotNull(provider.GetRequiredService<IExceptionReporter>());
    }

    private static ErrorHandlingMiddleware Middleware(RequestDelegate next, IExceptionReporter reporter) =>
        new(next, reporter, new ExceptionLogEntryFactory(new TestEnvironment(), TimeProvider.System));
    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        context.Request.Method = "GET"; context.Request.Path = "/test"; return context;
    }
    private static async Task<string> Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Task ThrowFromRepository() => throw new InvalidOperationException("password=secret-value", new Exception("inner detail"));
    internal static ExceptionLogEntry Entry() => new(Guid.NewGuid().ToString("N"), DateTime.UtcNow, "TestService", "Development", "instance",
        "correlation", "trace", "GET", "/test", "Controller", "Action", null, 500, false, "Exception", "message", "stack\nline2");

    private sealed class CaptureReporter : IExceptionReporter
    {
        public ConcurrentBag<ExceptionLogEntry> Entries { get; } = [];
        public Task ReportAsync(ExceptionLogEntry entry) { Entries.Add(entry); return Task.CompletedTask; }
    }
    private sealed class CaptureSink : IExceptionLogSink
    {
        public List<ExceptionLogEntry> Entries { get; } = [];
        public Task WriteAsync(ExceptionLogEntry entry, CancellationToken cancellationToken) { Entries.Add(entry); return Task.CompletedTask; }
    }
    private sealed class FailingSink : IExceptionLogSink
    {
        public Task WriteAsync(ExceptionLogEntry entry, CancellationToken cancellationToken) => throw new IOException("sink failed");
    }
    private sealed class WaitingSink : IExceptionLogSink
    {
        public Task WriteAsync(ExceptionLogEntry entry, CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
    }
    private sealed class StartedResponse : HttpResponseFeature { public override bool HasStarted => true; }
    internal sealed class TestEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "TestService";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

[ApiController]
[Route("test/exception-probe")]
public sealed class ExceptionProbeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => throw new InvalidOperationException("probe detail");
}

internal sealed class ExceptionProbeRepository
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public Task ReadAsync() => throw new InvalidOperationException("repository failed");
}
