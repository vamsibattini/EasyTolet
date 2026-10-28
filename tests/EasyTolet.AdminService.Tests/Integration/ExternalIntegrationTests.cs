using Dapper;
using EasyTolet.AdminService.Contracts;
using EasyTolet.AdminService.Messaging;
using EasyTolet.AdminService.Repositories;
using EasyTolet.Contracts.Admin;
using EasyTolet.Shared.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Xunit;
using static EasyTolet.AdminService.Tests.Repositories.RoleBusinessRepositoryTests;

namespace EasyTolet.AdminService.Tests.Integration;

public sealed class AdminMySqlFactAttribute : FactAttribute
{
    public AdminMySqlFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EASYTOLET_ADMIN_TEST_MYSQL")))
            Skip = "Set EASYTOLET_ADMIN_TEST_MYSQL to a dedicated schema ending in _tests.";
    }
}
public sealed class RabbitMqFactAttribute : FactAttribute
{
    public RabbitMqFactAttribute()
    {
        if (new[] { "EASYTOLET_TEST_RABBITMQ_ADDRESS", "EASYTOLET_TEST_RABBITMQ_USER", "EASYTOLET_TEST_RABBITMQ_PASSWORD" }
            .Any(key => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))))
            Skip = "Configure the EASYTOLET_TEST_RABBITMQ_* variables for a dedicated test virtual host.";
    }
}

public sealed class ExternalIntegrationTests
{
    [AdminMySqlFact]
    public async Task ExistingMySqlSchemaSupportsCrudAuditAndAtomicOutbox()
    {
        var connectionString = Environment.GetEnvironmentVariable("EASYTOLET_ADMIN_TEST_MYSQL")!;
        var builder = new MySqlConnectionStringBuilder(connectionString);
        Assert.EndsWith("_tests", builder.Database);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Roles.sql")));
        await connection.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AdminOutboxMessages.sql")));
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0))).Options);
        var roles = new RoleBusinessRepository(db, new UnitOfWork(db), new UnexpectedReporter(), TimeProvider.System);
        var code = "T" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant();
        int? id = null;
        try
        {
            var created = await roles.CreateAsync(new SaveRoleRequest { RoleCode = code, RoleName = "Test role", IsActive = false }, 7, default);
            id = created.RoleId;
            db.ChangeTracker.Clear();
            Assert.False((await roles.GetAsync(id.Value, default)).IsActive);
            var changed = await roles.UpdateAsync(id.Value, new SaveRoleRequest { RoleCode = code, RoleName = "Updated" }, 8, default);
            Assert.Equal(8, changed.UpdatedBy);
            await roles.DeleteAsync(id.Value, 8, default);
            Assert.Equal(3, await db.AdminOutboxMessages.CountAsync(m => m.Payload.Contains(code)));
        }
        finally
        {
            db.ChangeTracker.Clear();
            if (id.HasValue) await db.Roles.Where(r => r.RoleId == id.Value).ExecuteDeleteAsync();
            await db.AdminOutboxMessages.Where(m => m.Payload.Contains(code)).ExecuteDeleteAsync();
        }
    }

    [RabbitMqFact]
    public async Task RoleContractTravelsThroughRabbitMqWithStableEventIdentity()
    {
        var received = new TaskCompletionSource<RoleChangedV1>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(new Uri(Environment.GetEnvironmentVariable("EASYTOLET_TEST_RABBITMQ_ADDRESS")!), host =>
            {
                host.Username(Environment.GetEnvironmentVariable("EASYTOLET_TEST_RABBITMQ_USER")!);
                host.Password(Environment.GetEnvironmentVariable("EASYTOLET_TEST_RABBITMQ_PASSWORD")!);
            });
            cfg.ReceiveEndpoint("easytolet-role-tests-" + Guid.NewGuid().ToString("N"), endpoint =>
            {
                endpoint.Durable = false; endpoint.AutoDelete = true;
                endpoint.Handler<RoleChangedV1>(context => { received.TrySetResult(context.Message); return Task.CompletedTask; });
            });
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await bus.StartAsync(timeout.Token);
            var message = new RoleChangedV1(Guid.NewGuid(), 42, DateTime.UtcNow, "Created", 7,
                new RoleSnapshot(1, "PO", "Owner", null, null, true, DateTime.UtcNow, null, 7, null));
            await new RabbitMqRoleEventPublisher(bus).PublishAsync(message, timeout.Token);
            var actual = await received.Task.WaitAsync(timeout.Token);
            Assert.Equal(message.EventId, actual.EventId); Assert.Equal(42, actual.Sequence);
            Assert.Equal(message.Role, actual.Role);
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await bus.StopAsync(stopTimeout.Token);
        }
    }
}
