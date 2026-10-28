using EasyTolet.Shared.Database;
using EasyTolet.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Xunit;

namespace EasyTolet.Shared.Tests;

public class DatabaseTests
{
    private static MySqlDatabaseOptions Settings() => new()
    {
        Database = "UserDB", User = "user_service", Password = "test;Password=still-one-value"
    };

    [Fact]
    public void ConnectionBuilderEscapesSecretsAndEnforcesSafeFlags()
    {
        var options = Settings();
        var parsed = new MySqlConnectionStringBuilder(MySqlConnectionStringHelper.Build(options, false));
        Assert.Equal(options.Password, parsed.Password);
        Assert.Equal("UserDB", parsed.Database);
        Assert.Equal(MySqlSslMode.VerifyFull, parsed.SslMode);
        Assert.False(parsed.PersistSecurityInfo);
        Assert.False(parsed.AllowLoadLocalInfile);
        Assert.False(parsed.AllowPublicKeyRetrieval);
    }

    [Theory]
    [InlineData("root", MySqlSslMode.VerifyFull)]
    [InlineData("user_service", MySqlSslMode.Preferred)]
    [InlineData("user_service", MySqlSslMode.Required)]
    public void ProductionRejectsRootOrUnverifiedTls(string user, MySqlSslMode sslMode)
    {
        var options = Settings(); options.User = user; options.SslMode = sslMode;
        Assert.Throws<InvalidOperationException>(() => MySqlConnectionStringHelper.Build(options, false));
    }

    [Fact]
    public void OverrideCannotBypassValidationAndErrorsDoNotExposeSecrets()
    {
        Assert.Throws<InvalidOperationException>(() => MySqlConnectionStringHelper.Build(Settings(), false,
            "Server=localhost;Database=UserDB;User=root;Password=secret;SslMode=VerifyFull"));
        var error = Assert.Throws<InvalidOperationException>(() => MySqlConnectionStringHelper.Build(Settings(), true,
            "Password=secret;Trusted_Connection=True"));
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void EmptyPasswordFailsEarly()
    {
        var options = Settings(); options.Password = "";
        Assert.Throws<InvalidOperationException>(() => MySqlConnectionStringHelper.Build(options, true));
    }

    [Fact]
    public async Task EfAndDapperWritesRollBackTogetherAndTrackerIsCleared()
    {
        await using var context = await CreateContext();
        var uow = new UnitOfWork(context);
        var sql = new SqlExecutor(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => uow.ExecuteInTransactionAsync(async ct =>
        {
            await uow.InsertAsync(new Entry { Name = "EF" }, ct);
            await uow.SaveChangesAsync(ct);
            await sql.ExecuteAsync("INSERT INTO Entries (Name) VALUES (@Name)", new { Name = "Dapper" }, ct);
            Assert.Equal(2L, await sql.ScalarAsync<long>("SELECT COUNT(*) FROM Entries", cancellationToken: ct));
            throw new InvalidOperationException("Roll back");
        }));
        Assert.Equal(0, await context.Entries.CountAsync());
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task MixedWritesCommitAndValuesRemainParameterized()
    {
        await using var context = await CreateContext();
        var uow = new UnitOfWork(context);
        var sql = new SqlExecutor(context);
        var hostileValue = "x'); DROP TABLE Entries; --";
        await uow.ExecuteInTransactionAsync(async ct =>
        {
            await uow.InsertAsync(new Entry { Name = "EF" }, ct);
            await sql.ExecuteAsync("INSERT INTO Entries (Name) VALUES (@Name)", new { Name = hostileValue }, ct);
            await sql.ExecuteEfAsync($"INSERT INTO Entries (Name) VALUES ({hostileValue})", ct);
        });
        Assert.Equal(3, await context.Entries.CountAsync());
        Assert.Equal(2, (await sql.QueryEfAsync<Entry>($"SELECT Id, Name FROM Entries WHERE Name = {hostileValue}")).Count);
        Assert.Single(await sql.QueryAsync<Entry>("SELECT Id, Name FROM Entries WHERE Name = @Name", new { Name = "EF" }));
    }

    [Theory]
    [InlineData("OtherDb.Function")]
    [InlineData("fn(); DROP TABLE Entries;")]
    [InlineData("function\n")]
    public async Task UnsafeRoutineNamesAreRejected(string name)
    {
        await using var context = await CreateContext();
        var sql = new SqlExecutor(context);
        await Assert.ThrowsAsync<ArgumentException>(() => sql.FunctionAsync<int>(name, []));
        await Assert.ThrowsAsync<ArgumentException>(() => sql.QueryProcedureAsync<Entry>(name));
    }

    [Fact]
    public async Task CancellationRollsBackAndNestedTransactionsAreRejected()
    {
        await using var context = await CreateContext();
        var uow = new UnitOfWork(context);
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uow.ExecuteInTransactionAsync(async ct =>
        {
            await uow.InsertAsync(new Entry { Name = "cancel" }, ct);
            await uow.SaveChangesAsync(ct);
            cts.Cancel();
        }, cts.Token));
        Assert.Equal(0, await context.Entries.CountAsync());
        await uow.ExecuteInTransactionAsync(async ct =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => uow.ExecuteInTransactionAsync(_ => Task.CompletedTask, ct)));
    }

    private static async Task<TestContext> CreateContext()
    {
        var context = new TestContext(new DbContextOptionsBuilder<TestContext>().UseSqlite("Data Source=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    public sealed class TestContext(DbContextOptions<TestContext> options) : DbContext(options)
    {
        public DbSet<Entry> Entries => Set<Entry>();
    }

    public sealed class Entry
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }
}
