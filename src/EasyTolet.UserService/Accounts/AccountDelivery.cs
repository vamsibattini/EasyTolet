using EasyTolet.Contracts.Notifications;
using EasyTolet.Shared.Notifications;
using System.Text.Json;
using EasyTolet.Shared.Interfaces;
using Microsoft.AspNetCore.DataProtection;

namespace EasyTolet.UserService.Accounts;

public sealed record AccountMessage(string Id, string Channel, string Destination, string Purpose, string Token, DateTime ExpiresAtUtc, DateTime? CreatedAtUtc = null);
public interface IAccountMessageSender { Task SendAsync(AccountMessage message, CancellationToken ct); }

// The existing account outbox remains UserService-owned; delivery belongs to NotificationService.
public sealed class BrokerAccountMessageSender(INotificationPublisher publisher) : IAccountMessageSender
{
    public Task SendAsync(AccountMessage message, CancellationToken ct) => publisher.PublishAsync(new NotificationRequestedV1(
        Guid.Parse(message.Id), "UserService", message.Channel, message.Destination, "account." + message.Purpose,
        new Dictionary<string, string> { ["token"] = message.Token }, message.CreatedAtUtc ?? message.ExpiresAtUtc.AddMinutes(-15), message.ExpiresAtUtc), ct);
}

public sealed class AccountMessageDispatcher(ISqlExecutor sql, IUnitOfWork unitOfWork,
    IDataProtectionProvider protection, IAccountMessageSender sender, TimeProvider clock)
{
    public async Task<bool> DispatchAsync(CancellationToken ct)
    {
        MessageRow? message = null;
        var now = clock.GetUtcNow().UtcDateTime;
        var lease = Guid.NewGuid().ToString("D");
        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            // Expired or consumed messages must not retain recoverable secrets indefinitely.
            await sql.ExecuteAsync("""
                UPDATE UserAccountMessages m INNER JOIN UserAccountChallenges c ON c.TokenHash=m.TokenHash
                SET m.ProtectedPayload=NULL WHERE m.ProtectedPayload IS NOT NULL
                  AND (m.ExpiresAtUtc<=@now OR c.ConsumedAtUtc IS NOT NULL)
                """, new { now }, token);
            message = (await sql.QueryAsync<MessageRow>("""
                SELECT Id, ProtectedPayload FROM UserAccountMessages WHERE ProtectedPayload IS NOT NULL
                    AND DeliveredAtUtc IS NULL AND ExpiresAtUtc>@now AND NextAttemptAtUtc<=@now
                ORDER BY NextAttemptAtUtc LIMIT 1 FOR UPDATE SKIP LOCKED
                """, new { now }, token)).SingleOrDefault();
            if (message is null) return;
            await sql.ExecuteAsync("UPDATE UserAccountMessages SET LeaseId=@lease, NextAttemptAtUtc=@next, Attempts=Attempts+1 WHERE Id=@Id",
                new { message.Id, lease, next = now.AddMinutes(1) }, token);
        }, ct);
        if (message is null) return false;
        var payload = protection.CreateProtector("EasyTolet.AccountDelivery.v1").Unprotect(message.ProtectedPayload);
        var value = JsonSerializer.Deserialize<AccountMessage>(payload) ?? throw new InvalidOperationException("Invalid account delivery payload.");
        // DeliveredAtUtc is the legacy column name: it now records broker acceptance, not provider delivery.
        await sender.SendAsync(value, ct);
        await sql.ExecuteAsync("UPDATE UserAccountMessages SET DeliveredAtUtc=@now, ProtectedPayload=NULL WHERE Id=@Id AND LeaseId=@lease",
            new { message.Id, lease, now = clock.GetUtcNow().UtcDateTime }, ct);
        return true;
    }
    public sealed class MessageRow { public string Id { get; set; } = ""; public string ProtectedPayload { get; set; } = ""; }
}

public sealed class AccountDeliveryWorker(IServiceScopeFactory scopes, ILogger<AccountDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<AccountMessageDispatcher>().DispatchAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // Provider exceptions may contain destinations or secrets. Do not log their message/body.
                logger.LogWarning("Account notification publication failed; pending messages will be retried until expiry.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
