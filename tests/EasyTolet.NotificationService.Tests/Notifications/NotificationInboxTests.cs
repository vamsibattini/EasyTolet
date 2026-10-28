using System.Reflection;
using System.Text.Json;
using EasyTolet.Contracts.Notifications;
using EasyTolet.NotificationService.Notifications;
using EasyTolet.Shared.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace EasyTolet.NotificationService.Tests.Notifications;

public sealed class NotificationInboxTests
{
    [Fact]
    public async Task AcceptEncryptsPayloadCommitsBeforeReturnAndDuplicateDoesNotReplaceDelivery()
    {
        var h = new Harness(); var message = NotificationDeliveryTests.Message();
        await h.Inbox.AcceptAsync(message, default);
        Assert.True(h.Committed); Assert.NotNull(h.Payload); Assert.DoesNotContain(message.Data["token"], h.Payload);
        var value = JsonSerializer.Deserialize<NotificationRequestedV1>(h.Protection.CreateProtector("EasyTolet.NotificationInbox.v1").Unprotect(h.Payload!))!;
        Assert.Equal(message.Recipient, value.Recipient); Assert.Equal(message.NotificationId, value.NotificationId);
        var original = h.Payload;
        await h.Inbox.AcceptAsync(message, default); Assert.Equal(original, h.Payload);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Inbox.AcceptAsync(message with { Recipient = "other@example.com" }, default));
        Assert.Equal(original, h.Payload); Assert.True(h.RolledBack);
    }

    [Fact]
    public async Task DictionaryOrderingDoesNotChangeDuplicateIdentity()
    {
        var h = new Harness(); var message = NotificationDeliveryTests.Message(template: "message");
        await h.Inbox.AcceptAsync(message, default);
        await h.Inbox.AcceptAsync(message with { Data = new() { ["body"] = message.Data["body"], ["subject"] = message.Data["subject"] } }, default);
        Assert.False(h.RolledBack);
    }

    [Fact]
    public async Task CommitFailureDoesNotAcceptAndRollsBackPersistedMessage()
    {
        var h = new Harness { FailCommit = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Inbox.AcceptAsync(NotificationDeliveryTests.Message(), default));
        Assert.Null(h.Payload); Assert.False(h.Committed); Assert.True(h.RolledBack);
    }

    [Fact]
    public async Task ExpiredAndInvalidMessagesCannotCreateInboxEntries()
    {
        var h = new Harness(); var message = NotificationDeliveryTests.Message();
        await h.Inbox.AcceptAsync(message with { CreatedAtUtc = DateTime.UtcNow.AddHours(-1), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1) }, default);
        await Assert.ThrowsAsync<ArgumentException>(() => h.Inbox.AcceptAsync(message with { NotificationId = Guid.Empty }, default));
        Assert.Empty(h.Writes); Assert.Null(h.Payload);
    }

    [Fact]
    public async Task ClaimsCommitBeforeDeliveryAndAcknowledgementsAreScopedToLease()
    {
        var h = new Harness(); var message = NotificationDeliveryTests.Message();
        await h.Inbox.AcceptAsync(message, default);
        var lease = await h.Inbox.ClaimAsync(["email"], default);
        Assert.NotNull(lease); Assert.True(h.Committed); Assert.Equal(message.NotificationId, lease.Message.NotificationId);
        await h.Inbox.CompleteAsync(lease, default);
        var ack = h.Writes.Last(); Assert.Contains("LeaseId=@LeaseId", ack.Sql); Assert.Contains("ProtectedPayload=NULL", ack.Sql);
        Assert.Equal(lease.LeaseId, Field<string>(ack.Parameters, "LeaseId"));
    }

    [Fact]
    public async Task DeliveryFailureSchedulesRetryAndCapsAttemptsWithoutPersistingSensitiveErrorText()
    {
        var h = new Harness(); var message = NotificationDeliveryTests.Message();
        await h.Inbox.FailAsync(new(message.NotificationId.ToString("D"), Guid.NewGuid().ToString("D"), message), false, default);
        var write = Assert.Single(h.Writes);
        Assert.Contains("Attempts>=10", write.Sql); Assert.Contains("LeaseId=@LeaseId", write.Sql);
        Assert.Equal("DeliveryFailed", Field<string>(write.Parameters, "error"));
    }

    [Fact]
    public async Task CorruptedProtectedPayloadBecomesTerminalWithoutCallingAProvider()
    {
        var h = new Harness { Payload = "not-a-protected-payload" };
        Assert.Null(await h.Inbox.ClaimAsync(["email"], default));
        Assert.Contains(h.Writes, write => write.Sql.Contains("LastErrorCode='InvalidPayload'"));
    }

    public static T Field<T>(object parameters, string name) => (T)parameters.GetType().GetProperty(name)!.GetValue(parameters)!;
    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args!);
    }
    private sealed class Harness
    {
        public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
        public NotificationInbox Inbox { get; }
        public string? Hash, Payload;
        public bool Committed, RolledBack, FailCommit;
        private bool _inTransaction;
        public List<(string Sql, object Parameters)> Writes { get; } = [];
        public Harness()
        {
            var sql = DispatchProxy.Create<ISqlExecutor, Stub>();
            ((Stub)(object)sql).Call = (method, args) =>
            {
                var query = (string)args[0]!; var parameters = args[1]!;
                if (method.Name == "ExecuteAsync")
                {
                    Writes.Add((query, parameters));
                    if (query.Contains("INSERT INTO"))
                    {
                        Assert.True(_inTransaction);
                        if (Payload is null) { Payload = Field<string>(parameters, "payload"); Hash = Field<string>(parameters, "hash"); }
                        Assert.Contains("ON DUPLICATE KEY UPDATE Id=Id", query);
                    }
                    return Task.FromResult(1);
                }
                Assert.True(_inTransaction);
                if (method.GetGenericArguments()[0] == typeof(NotificationInbox.HashRow))
                    return Task.FromResult<IReadOnlyList<NotificationInbox.HashRow>>([new() { PayloadHash = Hash! }]);
                Assert.Contains("SKIP LOCKED", query); Assert.Contains("Channel IN @channels", query);
                return Task.FromResult<IReadOnlyList<NotificationInbox.InboxRow>>([new() { Id = Guid.NewGuid().ToString("D"), ProtectedPayload = Payload! }]);
            };
            var uow = DispatchProxy.Create<IUnitOfWork, Stub>();
            ((Stub)(object)uow).Call = (_, args) => Transaction((Func<CancellationToken, Task>)args[0]!);
            Inbox = new(sql, uow, Protection, TimeProvider.System);
        }
        private async Task Transaction(Func<CancellationToken, Task> action)
        {
            var hash = Hash; var payload = Payload; _inTransaction = true;
            try { await action(default); if (FailCommit) throw new InvalidOperationException("commit failure"); Committed = true; }
            catch { Hash = hash; Payload = payload; RolledBack = true; throw; }
            finally { _inTransaction = false; }
        }
    }
}
