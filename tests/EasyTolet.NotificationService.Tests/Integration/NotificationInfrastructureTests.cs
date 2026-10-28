using Dapper;
using EasyTolet.Contracts.Notifications;
using EasyTolet.NotificationService.Notifications;
using EasyTolet.NotificationService.Tests.Notifications;
using EasyTolet.Shared.Database;
using EasyTolet.Shared.Infrastructure;
using EasyTolet.Shared.Notifications;
using MassTransit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Xunit;

namespace EasyTolet.NotificationService.Tests.Integration;

public sealed class NotificationMySqlFactAttribute : FactAttribute
{
    public NotificationMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYTOLET_NOTIFICATION_TEST_MYSQL")))
            Skip = "Set EASYTOLET_NOTIFICATION_TEST_MYSQL to an isolated _tests schema with NotificationInbox.sql installed.";
    }
}
public sealed class NotificationRabbitMqFactAttribute : FactAttribute
{
    public NotificationRabbitMqFactAttribute()
    {
        if (new[] { "EASYTOLET_TEST_RABBITMQ_ADDRESS", "EASYTOLET_TEST_RABBITMQ_USER", "EASYTOLET_TEST_RABBITMQ_PASSWORD" }
            .Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "Configure EASYTOLET_TEST_RABBITMQ_* for an isolated virtual host ending _tests.";
    }
}

public sealed class NotificationInfrastructureTests
{
    [NotificationMySqlFact]
    public async Task MySqlDeduplicatesConcurrentAcceptanceAndProtectsDeliveryLeasesFromStaleWorkers()
    {
        var connectionString = Environment.GetEnvironmentVariable("EASYTOLET_NOTIFICATION_TEST_MYSQL")!;
        Assert.EndsWith("_tests", new MySqlConnectionStringBuilder(connectionString).Database);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM NotificationInbox WHERE Status IN ('Pending','Processing')"));
        AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0))).Options);
        var protection = new EphemeralDataProtectionProvider(); var clock = new Clock();
        NotificationInbox Inbox(AppDbContext db) => new(new SqlExecutor(db), new UnitOfWork(db), protection, clock);
        var message = NotificationDeliveryTests.Message(); var id = message.NotificationId.ToString("D");
        try
        {
            async Task Accept()
            { await using var db = Context(); await Inbox(db).AcceptAsync(message, default); }
            await Task.WhenAll(Accept(), Accept());
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM NotificationInbox WHERE Id=@id", new { id }));
            await using var context = Context(); var inbox = Inbox(context);
            await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.AcceptAsync(message with { Recipient = "different@example.com" }, default));
            async Task<NotificationLease?> Claim()
            { await using var db = Context(); return await Inbox(db).ClaimAsync(["email"], default); }
            var claims = await Task.WhenAll(Claim(), Claim());
            var first = Assert.Single(claims, lease => lease is not null)!;
            Assert.Single(claims, lease => lease is null);
            Assert.Equal(message.Recipient, first.Message.Recipient);
            clock.Advance(TimeSpan.FromMinutes(2));
            var replacement = await inbox.ClaimAsync(["email"], default); Assert.NotNull(replacement);
            await inbox.CompleteAsync(first, default);
            Assert.Equal("Processing", await connection.ExecuteScalarAsync<string>("SELECT Status FROM NotificationInbox WHERE Id=@id", new { id }));
            Assert.Equal(replacement.LeaseId, await connection.ExecuteScalarAsync<string>("SELECT LeaseId FROM NotificationInbox WHERE Id=@id", new { id }));
            await inbox.CompleteAsync(replacement, default);
            await inbox.AcceptAsync(message, default);
            Assert.Null(await inbox.ClaimAsync(["email"], default));
            Assert.Equal("Sent", await connection.ExecuteScalarAsync<string>("SELECT Status FROM NotificationInbox WHERE Id=@id", new { id }));
            Assert.Null(await connection.ExecuteScalarAsync<string>("SELECT ProtectedPayload FROM NotificationInbox WHERE Id=@id", new { id }));
        }
        finally { await connection.ExecuteAsync("DELETE FROM NotificationInbox WHERE Id=@id", new { id }); }
    }

    [NotificationRabbitMqFact]
    public async Task DurableRabbitMqQueueTransportsNotificationIdentityAndContent()
    {
        var address = new Uri(Environment.GetEnvironmentVariable("EASYTOLET_TEST_RABBITMQ_ADDRESS")!);
        Assert.EndsWith("_tests", Uri.UnescapeDataString(address.AbsolutePath).Trim('/'));
        var received = new TaskCompletionSource<NotificationRequestedV1>(TaskCreationOptions.RunContinuationsAsynchronously);
        var message = NotificationDeliveryTests.Message();
        var bus = Bus.Factory.CreateUsingRabbitMq(config =>
        {
            config.Host(address, host =>
            {
                host.Username(Environment.GetEnvironmentVariable("EASYTOLET_TEST_RABBITMQ_USER")!);
                host.Password(Environment.GetEnvironmentVariable("EASYTOLET_TEST_RABBITMQ_PASSWORD")!);
            });
            config.ReceiveEndpoint(NotificationQueues.Delivery, endpoint =>
            {
                endpoint.Durable = true;
                endpoint.Handler<NotificationRequestedV1>(context =>
                {
                    if (context.Message.NotificationId == message.NotificationId) received.TrySetResult(context.Message);
                    return Task.CompletedTask;
                });
            });
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await bus.StartAsync(timeout.Token);
            await new RabbitMqNotificationPublisher(bus, TimeProvider.System).PublishAsync(message, timeout.Token);
            var actual = await received.Task.WaitAsync(timeout.Token);
            Assert.Equal(message.NotificationId, actual.NotificationId); Assert.Equal(message.Recipient, actual.Recipient);
            Assert.Equal(message.Data["token"], actual.Data["token"]);
        }
        finally { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await bus.StopAsync(stop.Token); }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }
}
