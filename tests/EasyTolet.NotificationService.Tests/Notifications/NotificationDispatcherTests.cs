using EasyTolet.Contracts.Notifications;
using EasyTolet.NotificationService.Notifications;
using Xunit;

namespace EasyTolet.NotificationService.Tests.Notifications;

public sealed class NotificationDispatcherTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("disabled")]
    public async Task DispatcherAcknowledgesOnlySuccessfulDeliveryAndRetriesOtherResults(string outcome)
    {
        var inbox = new FakeInbox(); var sender = new Sender(outcome);
        var dispatcher = new NotificationDeliveryDispatcher(inbox, sender, NotificationDeliveryTests.Config());
        Assert.True(await dispatcher.DispatchAsync(default));
        Assert.Equal(outcome == "success", inbox.Completed); Assert.Equal(outcome != "success", inbox.Failed);
        Assert.Equal(outcome == "disabled", inbox.Disabled); Assert.Equal(["email"], inbox.Channels);
    }

    [Fact]
    public async Task EnabledProvidersAreIncludedAndNoPendingWorkDoesNotContactProvider()
    {
        var inbox = new FakeInbox { Empty = true }; var sender = new Sender("success");
        Assert.False(await new NotificationDeliveryDispatcher(inbox, sender, NotificationDeliveryTests.Config(true, true)).DispatchAsync(default));
        Assert.Equal(["email", "sms", "push"], inbox.Channels); Assert.False(sender.Called);
    }

    [Fact]
    public async Task CancellationPreservesLeaseWithoutMarkingDeliveryFailed()
    {
        var inbox = new FakeInbox(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NotificationDeliveryDispatcher(inbox, new Sender("cancel"), NotificationDeliveryTests.Config()).DispatchAsync(cancellation.Token));
        Assert.False(inbox.Completed); Assert.False(inbox.Failed);
    }

    [Fact]
    public async Task DatabaseAcknowledgementFailureIsNotMisreportedAsProviderFailure()
    {
        var inbox = new FakeInbox { AckFailure = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NotificationDeliveryDispatcher(inbox, new Sender("success"), NotificationDeliveryTests.Config()).DispatchAsync(default));
        Assert.False(inbox.Failed);
    }

    public sealed class FakeInbox : INotificationInbox
    {
        public bool Completed, Failed, Disabled, Empty, AckFailure;
        public string[] Channels = [];
        public Task AcceptAsync(NotificationRequestedV1 message, CancellationToken ct) => Task.CompletedTask;
        public Task<NotificationLease?> ClaimAsync(string[] channels, CancellationToken ct)
        { Channels = channels; return Task.FromResult<NotificationLease?>(Empty ? null : new("id", "lease", NotificationDeliveryTests.Message())); }
        public Task CompleteAsync(NotificationLease lease, CancellationToken ct)
        { if (AckFailure) throw new InvalidOperationException("commit failure"); Completed = true; return Task.CompletedTask; }
        public Task FailAsync(NotificationLease lease, bool disabled, CancellationToken ct)
        { Failed = true; Disabled = disabled; return Task.CompletedTask; }
    }
    private sealed class Sender(string outcome) : INotificationSender
    {
        public bool Called;
        public Task SendAsync(NotificationRequestedV1 message, CancellationToken ct)
        {
            Called = true;
            if (outcome == "failure") throw new InvalidOperationException("provider failure");
            if (outcome == "disabled") throw new ChannelDisabledException();
            if (outcome == "cancel") throw new OperationCanceledException(ct);
            return Task.CompletedTask;
        }
    }
}
