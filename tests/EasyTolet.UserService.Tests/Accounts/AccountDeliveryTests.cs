using System.Net;
using System.Text.Json;
using EasyTolet.Shared.Interfaces;
using EasyTolet.UserService.Accounts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;
using static EasyTolet.UserService.Tests.Repositories.UserBusinessRepositoryTests;
using System.Reflection;

namespace EasyTolet.UserService.Tests.Accounts;

public sealed class AccountDeliveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatcherCommitsLeaseBeforeSendingAndOnlyAcknowledgesSuccessfulDelivery(bool fail)
    {
        var protection = new EphemeralDataProtectionProvider(); var value = Message();
        var payload = protection.CreateProtector("EasyTolet.AccountDelivery.v1").Protect(JsonSerializer.Serialize(value));
        bool inTransaction = false, committed = false, acknowledged = false;
        var sql = DispatchProxy.Create<ISqlExecutor, Stub>();
        ((Stub)(object)sql).Call = (method, args) =>
        {
            var query = (string)args[0]!;
            if (method.Name == "QueryAsync")
            {
                Assert.True(inTransaction); Assert.Contains("SKIP LOCKED", query);
                return Task.FromResult<IReadOnlyList<AccountMessageDispatcher.MessageRow>>([new() { Id = value.Id, ProtectedPayload = payload }]);
            }
            if (query.Contains("SET DeliveredAtUtc")) { Assert.False(inTransaction); acknowledged = true; Assert.Contains("LeaseId=@lease", query); }
            else Assert.True(inTransaction);
            return Task.FromResult(1);
        };
        var uow = DispatchProxy.Create<IUnitOfWork, Stub>();
        async Task Transaction(Func<CancellationToken, Task> action)
        { inTransaction = true; await action(default); inTransaction = false; committed = true; }
        ((Stub)(object)uow).Call = (_, args) => Transaction((Func<CancellationToken, Task>)args[0]!);
        var sender = new Sender(message =>
        {
            Assert.True(committed); Assert.False(inTransaction); Assert.Equal(value, message);
            if (fail) throw new InvalidOperationException("provider failure");
        });
        var dispatcher = new AccountMessageDispatcher(sql, uow, protection, sender, TimeProvider.System);
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync(default));
        else Assert.True(await dispatcher.DispatchAsync(default));
        Assert.Equal(!fail, acknowledged);
    }

    private static AccountMessage Message() => new(Guid.NewGuid().ToString("D"), "email", "user@example.com", "reset", "secret", DateTime.UtcNow.AddMinutes(15));
    private sealed class Sender(Action<AccountMessage> action) : IAccountMessageSender
    { public Task SendAsync(AccountMessage message, CancellationToken ct) { action(message); return Task.CompletedTask; } }
}
