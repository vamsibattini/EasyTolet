using EasyTolet.Contracts.Notifications;
using EasyTolet.NotificationService.Notifications;
using EasyTolet.Shared.Notifications;
using MassTransit;
using Xunit;

namespace EasyTolet.NotificationService.Tests.Notifications;

public sealed class NotificationBrokerTests
{
    [Fact]
    public async Task SharedPublisherDeliversContractToConsumerWithStableMessageIdentity()
    {
        var inbox = new CapturingInbox(); var consumer = new NotificationRequestedConsumer(inbox);
        var received = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bus = Bus.Factory.CreateUsingInMemory(config => config.ReceiveEndpoint(NotificationQueues.Delivery, endpoint =>
            endpoint.Handler<NotificationRequestedV1>(async context =>
            {
                await consumer.Consume(context); received.TrySetResult(context.MessageId);
            })));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await bus.StartAsync(timeout.Token);
            var message = NotificationDeliveryTests.Message();
            await new RabbitMqNotificationPublisher(bus, TimeProvider.System).PublishAsync(message, timeout.Token);
            Assert.Equal(message.NotificationId, await received.Task.WaitAsync(timeout.Token));
            Assert.Equal(message.Recipient, inbox.Message!.Recipient); Assert.Equal(message.Template, inbox.Message.Template);
            Assert.Equal(message.Data["token"], inbox.Message.Data["token"]);
        }
        finally { await bus.StopAsync(); }
    }

    private sealed class CapturingInbox : INotificationInbox
    {
        public NotificationRequestedV1? Message;
        public Task AcceptAsync(NotificationRequestedV1 message, CancellationToken ct) { Message = message; return Task.CompletedTask; }
        public Task<NotificationLease?> ClaimAsync(string[] channels, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteAsync(NotificationLease lease, CancellationToken ct) => throw new NotSupportedException();
        public Task FailAsync(NotificationLease lease, bool disabled, CancellationToken ct) => throw new NotSupportedException();
    }
}
