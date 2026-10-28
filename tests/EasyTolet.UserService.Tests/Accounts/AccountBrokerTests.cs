using EasyTolet.Contracts.Notifications;
using EasyTolet.Shared.Notifications;
using EasyTolet.UserService.Accounts;
using Xunit;

namespace EasyTolet.UserService.Tests.Accounts;

public sealed class AccountBrokerTests
{
    [Theory]
    [InlineData("reset", "email")]
    [InlineData("email", "email")]
    [InlineData("delete", "email")]
    [InlineData("phone", "sms")]
    public async Task AccountOutboxPublishesStableIdentityContentAndExpiryAcrossRetries(string purpose, string channel)
    {
        var publisher = new Publisher(); var sender = new BrokerAccountMessageSender(publisher);
        var message = new AccountMessage(Guid.NewGuid().ToString("D"), channel, "recipient", purpose, new string('a', 43), DateTime.UtcNow.AddMinutes(15));
        await sender.SendAsync(message, default); await sender.SendAsync(message, default);
        var first = publisher.Messages[0]; var retry = publisher.Messages[1];
        Assert.Equal(Guid.Parse(message.Id), first.NotificationId); Assert.Equal("UserService", first.Source);
        Assert.Equal("account." + purpose, first.Template); Assert.Equal(channel, first.Channel);
        Assert.Equal(message.Token, first.Data["token"]); Assert.Equal(message.Destination, first.Recipient);
        Assert.Equal(first.CreatedAtUtc, retry.CreatedAtUtc); Assert.Equal(first.ExpiresAtUtc, retry.ExpiresAtUtc);
        Assert.Equal(message.ExpiresAtUtc, first.ExpiresAtUtc);
    }
    private sealed class Publisher : INotificationPublisher
    {
        public List<NotificationRequestedV1> Messages { get; } = [];
        public Task PublishAsync(NotificationRequestedV1 notification, CancellationToken ct) { Messages.Add(notification); return Task.CompletedTask; }
    }
}
