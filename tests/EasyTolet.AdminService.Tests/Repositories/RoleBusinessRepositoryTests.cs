using System.Text.Json;
using EasyTolet.AdminService.Contracts;
using EasyTolet.AdminService.Domain;
using EasyTolet.AdminService.Messaging;
using EasyTolet.AdminService.Repositories;
using EasyTolet.Contracts.Admin;
using EasyTolet.Shared.Exceptions;
using EasyTolet.Shared.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EasyTolet.AdminService.Tests.Repositories;

public sealed class RoleBusinessRepositoryTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private TestContext _db = null!;
    private RoleBusinessRepository _roles = null!;
    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new TestContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _roles = new(_db, new UnitOfWork(_db), new UnexpectedReporter(), TimeProvider.System);
    }
    public async Task DisposeAsync() { await _db.DisposeAsync(); await _connection.DisposeAsync(); }
    private static SaveRoleRequest Input(string code = "PO", int? parent = null) => new() { RoleCode = code, RoleName = "Property owner", ParentRoleId = parent };

    [Fact]
    public async Task CreatePersistsMappedFieldsAuditAndOutboxAtomically()
    {
        var input = Input("po"); input.IsActive = false; input.Description = "Owner description";
        var role = await _roles.CreateAsync(input, 7, default);
        _db.ChangeTracker.Clear();
        var stored = await _roles.GetAsync(role.RoleId, default);
        Assert.Equal("PO", stored.RoleCode); Assert.False(stored.IsActive);
        Assert.Equal(7, stored.CreatedBy); Assert.Null(stored.UpdatedDate);
        var outbox = await _db.AdminOutboxMessages.SingleAsync();
        var message = JsonSerializer.Deserialize<RoleChangedV1>(outbox.Payload)!;
        Assert.Equal("Created", message.Operation); Assert.Equal(role.RoleId, message.Role.RoleId);
        Assert.Equal(outbox.EventId, message.EventId); Assert.Null(outbox.PublishedAtUtc);
    }

    [Fact]
    public async Task UpdatePreservesCreationAuditAndEmitsUpdatedEvent()
    {
        var original = await _roles.CreateAsync(Input(), 7, default);
        var updated = await _roles.UpdateAsync(original.RoleId, Input("OWNER"), 8, default);
        Assert.Equal(original.CreatedDate, updated.CreatedDate);
        Assert.Equal(7, updated.CreatedBy); Assert.Equal(8, updated.UpdatedBy);
        Assert.NotNull(updated.UpdatedDate); Assert.Equal("OWNER", updated.RoleCode);
        Assert.Equal(2, await _db.AdminOutboxMessages.CountAsync());
    }

    [Fact]
    public async Task DeleteRemovesLeafAndEmitsSnapshotTombstone()
    {
        var role = await _roles.CreateAsync(Input(), 7, default);
        await _roles.DeleteAsync(role.RoleId, 8, default);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => _roles.GetAsync(role.RoleId, default))).StatusCode);
        var row = await _db.AdminOutboxMessages.OrderByDescending(m => m.Id).FirstAsync();
        var message = JsonSerializer.Deserialize<RoleChangedV1>(row.Payload)!;
        Assert.Equal("Deleted", message.Operation); Assert.Equal(8, message.ActorId);
        Assert.Equal(role.RoleId, message.Role.RoleId);
    }

    [Fact]
    public async Task ParentDeletionAndAncestorCycleAreRejected()
    {
        var root = await _roles.CreateAsync(Input("ROOT"), 7, default);
        var child = await _roles.CreateAsync(Input("CHILD", root.RoleId), 7, default);
        var leaf = await _roles.CreateAsync(Input("LEAF", child.RoleId), 7, default);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => _roles.DeleteAsync(root.RoleId, 7, default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => _roles.UpdateAsync(root.RoleId, Input("ROOT", leaf.RoleId), 7, default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => _roles.UpdateAsync(root.RoleId, Input("ROOT", root.RoleId), 7, default))).StatusCode);
        Assert.Equal(3, await _db.AdminOutboxMessages.CountAsync());
        Assert.Null((await _roles.GetAsync(root.RoleId, default)).ParentRoleId);
    }

    [Fact]
    public async Task DuplicateCodeMissingParentAndMissingRoleAreRejected()
    {
        await _roles.CreateAsync(Input(), 7, default);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => _roles.CreateAsync(Input("po"), 7, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => _roles.CreateAsync(Input("TT", 999), 7, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => _roles.UpdateAsync(999, Input(), 7, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => _roles.DeleteAsync(999, 7, default))).StatusCode);
        Assert.Equal(1, await _db.Roles.CountAsync());
    }

    [Theory]
    [InlineData("", "Name")]
    [InlineData("TOOLONGCODE1", "Name")]
    [InlineData("IN VALID", "Name")]
    [InlineData("OK", "   ")]
    public async Task InvalidInputCreatesNeitherRoleNorEvent(string code, string name)
    {
        var request = Input(code); request.RoleName = name;
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => _roles.CreateAsync(request, 7, default))).StatusCode);
        Assert.Empty(await _db.Roles.ToListAsync()); Assert.Empty(await _db.AdminOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task InvalidActorAndPaginationAreRejected()
    {
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => _roles.CreateAsync(Input(), 0, default))).StatusCode);
        await Assert.ThrowsAsync<ApiException>(() => _roles.ListAsync(0, 20, default));
        await Assert.ThrowsAsync<ApiException>(() => _roles.ListAsync(1, 101, default));
        await Assert.ThrowsAsync<ApiException>(() => _roles.ListAsync(int.MaxValue, 100, default));
    }

    [Fact]
    public async Task ListIsBoundedAndOrdered()
    {
        await _roles.CreateAsync(Input("A"), 7, default); var second = await _roles.CreateAsync(Input("B"), 7, default);
        var result = await _roles.ListAsync(2, 1, default);
        Assert.Equal(2, result.TotalCount); Assert.Equal(second.RoleId, Assert.Single(result.Items).RoleId);
    }

    [Fact]
    public async Task OutboxFailureRollsBackRoleInsert()
    {
        await _db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailOutbox BEFORE INSERT ON AdminOutboxMessages BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => _roles.CreateAsync(Input(), 7, default));
        Assert.Equal(0, await _db.Roles.CountAsync()); Assert.Empty(_db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutboxFailureAlsoRollsBackUpdateAndDelete(bool delete)
    {
        var original = await _roles.CreateAsync(Input(), 7, default);
        await _db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailOutbox BEFORE INSERT ON AdminOutboxMessages BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        if (delete)
            await Assert.ThrowsAsync<DbUpdateException>(() => _roles.DeleteAsync(original.RoleId, 8, default));
        else
            await Assert.ThrowsAsync<DbUpdateException>(() => _roles.UpdateAsync(original.RoleId, Input("NEW"), 8, default));
        var stored = await _roles.GetAsync(original.RoleId, default);
        Assert.Equal("PO", stored.RoleCode);
        Assert.Null(stored.UpdatedDate);
        Assert.Equal(1, await _db.AdminOutboxMessages.CountAsync());
    }

    [Fact]
    public async Task BrokerFailureLeavesMessagePendingAndRetryPublishesStableIdentity()
    {
        await _roles.CreateAsync(Input(), 7, default);
        var publisher = new FakePublisher { Fail = true };
        var dispatcher = new AdminOutboxDispatcher(_db, publisher, TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() => dispatcher.DispatchAsync(default));
        Assert.Null((await _db.AdminOutboxMessages.SingleAsync()).PublishedAtUtc);
        publisher.Fail = false;
        Assert.Equal(1, await dispatcher.DispatchAsync(default));
        var row = await _db.AdminOutboxMessages.SingleAsync();
        Assert.Equal(row.EventId, Assert.Single(publisher.Messages).EventId);
        Assert.Equal(row.Id, publisher.Messages[0].Sequence);
        Assert.NotNull(row.PublishedAtUtc); Assert.Equal(0, await dispatcher.DispatchAsync(default));
    }

    [Fact]
    public async Task CancelledMutationDoesNotWrite()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _roles.CreateAsync(Input(), 7, new CancellationToken(true)));
        Assert.Equal(0, await _db.Roles.CountAsync());
    }

    private sealed class FakePublisher : IRoleEventPublisher
    {
        public bool Fail { get; set; }
        public List<RoleChangedV1> Messages { get; } = [];
        public Task PublishAsync(RoleChangedV1 message, CancellationToken ct)
        {
            if (Fail) throw new IOException("broker unavailable");
            Messages.Add(message); return Task.CompletedTask;
        }
    }
    internal sealed class UnexpectedReporter : IExceptionReporter
    {
        public Task ReportAsync(ExceptionLogEntry entry) => throw new InvalidOperationException("Repository must let middleware report unhandled errors once.");
    }
    internal sealed class TestContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // SQLite has a different timestamp default syntax; production MySQL mapping remains exact.
            modelBuilder.Entity<Role>().Property(r => r.CreatedDate).HasDefaultValueSql("CURRENT_TIMESTAMP");
        }
    }
}
