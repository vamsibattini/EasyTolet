using EasyTolet.Shared.Exceptions;

namespace EasyTolet.AdminService.Messaging;

public sealed class AdminOutboxWorker(IServiceScopeFactory scopeFactory, IExceptionReporter reporter,
    ExceptionLogEntryFactory entryFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await scope.ServiceProvider.GetRequiredService<AdminOutboxDispatcher>().DispatchAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // Background failures do not pass through HTTP middleware: report explicitly once per batch failure.
                var context = new DefaultHttpContext();
                context.Request.Method = "BACKGROUND";
                context.Request.Path = "/admin/outbox";
                await reporter.ReportAsync(entryFactory.Create(context, exception, Guid.NewGuid().ToString("N"), 500));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
