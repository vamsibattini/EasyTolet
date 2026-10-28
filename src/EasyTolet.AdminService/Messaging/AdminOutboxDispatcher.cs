using System.Text.Json;
using EasyTolet.Contracts.Admin;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace EasyTolet.AdminService.Messaging;

public interface IRoleEventPublisher
{
    Task PublishAsync(RoleChangedV1 message, CancellationToken ct);
}

public sealed class RabbitMqRoleEventPublisher(IPublishEndpoint publisher) : IRoleEventPublisher
{
    public Task PublishAsync(RoleChangedV1 message, CancellationToken ct) =>
        publisher.Publish(message, context => context.MessageId = message.EventId, ct);
}

/// <summary>At-least-once delivery. Consumers deduplicate EventId and reject stale per-role Sequence values.</summary>
public sealed class AdminOutboxDispatcher(AppDbContext database, IRoleEventPublisher publisher, TimeProvider clock)
{
    public async Task<int> DispatchAsync(CancellationToken ct)
    {
        var pending = await database.AdminOutboxMessages.Where(m => m.PublishedAtUtc == null)
            .OrderBy(m => m.Id).Take(50).ToListAsync(ct);
        foreach (var row in pending)
        {
            var message = JsonSerializer.Deserialize<RoleChangedV1>(row.Payload)
                ?? throw new InvalidOperationException($"Invalid Admin outbox payload {row.Id}.");
            await publisher.PublishAsync(message with { Sequence = row.Id }, ct);
            row.PublishedAtUtc = clock.GetUtcNow().UtcDateTime;
            await database.SaveChangesAsync(ct);
        }
        return pending.Count;
    }
}
