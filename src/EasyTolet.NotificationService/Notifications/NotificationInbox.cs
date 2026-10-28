using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EasyTolet.Contracts.Notifications;
using EasyTolet.Shared.Interfaces;
using MassTransit;
using Microsoft.AspNetCore.DataProtection;

namespace EasyTolet.NotificationService.Notifications;

public sealed record NotificationLease(string Id, string LeaseId, NotificationRequestedV1 Message);
public interface INotificationInbox
{
    Task AcceptAsync(NotificationRequestedV1 message, CancellationToken ct);
    Task<NotificationLease?> ClaimAsync(string[] channels, CancellationToken ct);
    Task CompleteAsync(NotificationLease lease, CancellationToken ct);
    Task FailAsync(NotificationLease lease, bool disabled, CancellationToken ct);
}

public sealed class NotificationRequestedConsumer(INotificationInbox inbox) : IConsumer<NotificationRequestedV1>
{
    // RabbitMQ acknowledgement follows durable inbox acceptance; provider failures never lose the broker command.
    public Task Consume(ConsumeContext<NotificationRequestedV1> context) => inbox.AcceptAsync(context.Message, context.CancellationToken);
}

public sealed class NotificationInbox(ISqlExecutor sql, IUnitOfWork unitOfWork, IDataProtectionProvider protection, TimeProvider clock) : INotificationInbox
{
    private readonly IDataProtector _protector = protection.CreateProtector("EasyTolet.NotificationInbox.v1");
    public async Task AcceptAsync(NotificationRequestedV1 message, CancellationToken ct)
    {
        NotificationTemplates.Validate(message);
        var now = clock.GetUtcNow().UtcDateTime;
        if (message.CreatedAtUtc > now.AddMinutes(5)) throw new ArgumentException("Invalid notification timestamp.");
        if (message.ExpiresAtUtc <= now) return;
        var canonical = message with { Data = message.Data.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value) };
        var json = JsonSerializer.Serialize(canonical);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var id = message.NotificationId.ToString("D");
        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            // Duplicates never reset a completed delivery or replace its recipient/content.
            await sql.ExecuteAsync("""
                INSERT INTO NotificationInbox (Id, Source, Channel, PayloadHash, ProtectedPayload, CreatedAtUtc, ExpiresAtUtc, NextAttemptAtUtc, Status)
                VALUES (@id,@Source,@Channel,@hash,@payload,@CreatedAtUtc,@ExpiresAtUtc,@now,'Pending')
                ON DUPLICATE KEY UPDATE Id=Id
                """, new { id, message.Source, message.Channel, hash, payload = _protector.Protect(json), message.CreatedAtUtc, message.ExpiresAtUtc, now }, token);
            var row = (await sql.QueryAsync<HashRow>("SELECT PayloadHash FROM NotificationInbox WHERE Id=@id FOR UPDATE", new { id }, token)).Single();
            if (row.PayloadHash != hash) throw new InvalidOperationException("Notification ID was reused with different content.");
        }, ct);
    }

    public async Task<NotificationLease?> ClaimAsync(string[] channels, CancellationToken ct)
    {
        InboxRow? row = null;
        var now = clock.GetUtcNow().UtcDateTime;
        var leaseId = Guid.NewGuid().ToString("D");
        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await sql.ExecuteAsync("""
                UPDATE NotificationInbox SET Status='Expired', ProtectedPayload=NULL, LeaseId=NULL
                WHERE Status IN ('Pending','Processing') AND ExpiresAtUtc<=@now
                """, new { now }, token);
            if (channels.Length == 0) return;
            row = (await sql.QueryAsync<InboxRow>("""
                SELECT Id, ProtectedPayload FROM NotificationInbox
                WHERE Status IN ('Pending','Processing') AND Channel IN @channels
                  AND NextAttemptAtUtc<=@now AND ExpiresAtUtc>@now
                ORDER BY NextAttemptAtUtc LIMIT 1 FOR UPDATE SKIP LOCKED
                """, new { now, channels }, token)).SingleOrDefault();
            if (row is null) return;
            await sql.ExecuteAsync("""
                UPDATE NotificationInbox SET Status='Processing', LeaseId=@leaseId, NextAttemptAtUtc=@next,
                    Attempts=Attempts+1 WHERE Id=@Id
                """, new { row.Id, leaseId, next = now.AddMinutes(1) }, token);
        }, ct);
        if (row is null) return null;
        // An unreadable payload is terminal; do not poison the worker by retrying it forever.
        try
        {
            var value = JsonSerializer.Deserialize<NotificationRequestedV1>(_protector.Unprotect(row.ProtectedPayload))
                ?? throw new InvalidOperationException("Invalid notification payload.");
            NotificationTemplates.Validate(value);
            return new(row.Id, leaseId, value);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException or InvalidOperationException)
        {
            await sql.ExecuteAsync("UPDATE NotificationInbox SET Status='Failed', LastErrorCode='InvalidPayload', ProtectedPayload=NULL, LeaseId=NULL WHERE Id=@Id AND LeaseId=@leaseId",
                new { row.Id, leaseId }, ct);
            return null;
        }
    }

    public Task CompleteAsync(NotificationLease lease, CancellationToken ct) => sql.ExecuteAsync("""
        UPDATE NotificationInbox SET Status='Sent', DeliveredAtUtc=@now, ProtectedPayload=NULL, LeaseId=NULL, LastErrorCode=NULL
        WHERE Id=@Id AND LeaseId=@LeaseId AND Status='Processing'
        """, new { lease.Id, lease.LeaseId, now = clock.GetUtcNow().UtcDateTime }, ct);

    public Task FailAsync(NotificationLease lease, bool disabled, CancellationToken ct) => sql.ExecuteAsync("""
        UPDATE NotificationInbox SET
            Status=CASE WHEN Attempts>=10 AND NOT @disabled THEN 'Failed' ELSE 'Pending' END,
            ProtectedPayload=CASE WHEN Attempts>=10 AND NOT @disabled THEN NULL ELSE ProtectedPayload END,
            LastErrorCode=@error, NextAttemptAtUtc=@next, LeaseId=NULL
        WHERE Id=@Id AND LeaseId=@LeaseId AND Status='Processing'
        """, new { lease.Id, lease.LeaseId, disabled, error = disabled ? "ChannelDisabled" : "DeliveryFailed", next = clock.GetUtcNow().UtcDateTime.AddMinutes(1) }, ct);

    public sealed class HashRow { public string PayloadHash { get; set; } = ""; }
    public sealed class InboxRow { public string Id { get; set; } = ""; public string ProtectedPayload { get; set; } = ""; }
}

public sealed class NotificationDeliveryDispatcher(INotificationInbox inbox, INotificationSender sender, IConfiguration configuration)
{
    public async Task<bool> DispatchAsync(CancellationToken ct)
    {
        var channels = new List<string> { "email" };
        if (configuration.GetValue<bool>("Notifications:Sms:Enabled")) channels.Add("sms");
        if (configuration.GetValue<bool>("Notifications:Push:Enabled")) channels.Add("push");
        var lease = await inbox.ClaimAsync(channels.ToArray(), ct);
        if (lease is null) return false;
        try { await sender.SendAsync(lease.Message, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ChannelDisabledException) { await inbox.FailAsync(lease, true, ct); return true; }
        catch (Exception) { await inbox.FailAsync(lease, false, ct); return true; }
        // Keep provider acknowledgement separate: a database failure must retain the lease for a safe retry.
        await inbox.CompleteAsync(lease, ct);
        return true;
    }
}

public sealed class NotificationDeliveryWorker(IServiceScopeFactory scopes, ILogger<NotificationDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<NotificationDeliveryDispatcher>().DispatchAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Notification processing failed; pending deliveries will be retried."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
