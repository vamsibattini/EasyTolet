using System.Data;
using Dapper;
using EasyTolet.Shared.Database;
using EasyTolet.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Xunit;

namespace EasyTolet.Shared.Tests;

public sealed class MySqlFactAttribute : FactAttribute
{
    public MySqlFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EASYTOLET_TEST_MYSQL")))
            Skip = "Set EASYTOLET_TEST_MYSQL to a dedicated MySQL test database connection string ending in _tests.";
    }
}

public class MySqlIntegrationTests
{
    [MySqlFact]
    public async Task ProceduresFunctionsParametersAndMixedTransactionWorkOnMySql()
    {
        var connectionString = Environment.GetEnvironmentVariable("EASYTOLET_TEST_MYSQL")!;
        var settings = new MySqlConnectionStringBuilder(connectionString);
        Assert.EndsWith("_tests", settings.Database);
        await using var context = new DbContext(new DbContextOptionsBuilder<DbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0))).Options);
        var sql = new SqlExecutor(context);
        var uow = new UnitOfWork(context);
        var suffix = Guid.NewGuid().ToString("N");
        var table = "test_" + suffix;
        var procedure = "proc_" + suffix;
        var function = "fn_" + suffix;
        try
        {
            // DDL intentionally runs outside transactions: MySQL DDL implicitly commits.
            await sql.ExecuteAsync($"CREATE TABLE `{table}` (Value TEXT NULL) ENGINE=InnoDB");
            await sql.ExecuteAsync($"CREATE PROCEDURE `{procedure}`(IN p_value TEXT, OUT p_length INT) BEGIN SET p_length = CHAR_LENGTH(p_value); SELECT p_value AS Value; END");
            await sql.ExecuteAsync($"CREATE FUNCTION `{function}`(p_value INT) RETURNS INT DETERMINISTIC NO SQL RETURN p_value + 1");
            var text = "quoted ' value; --";
            var parameters = new DynamicParameters();
            parameters.Add("p_value", text);
            parameters.Add("p_length", dbType: DbType.Int32, direction: ParameterDirection.Output);
            var rows = await sql.QueryProcedureAsync<Row>(procedure, parameters);
            Assert.Equal(text, Assert.Single(rows).Value);
            Assert.Equal(text.Length, parameters.Get<int>("p_length"));
            Assert.Equal(42, await sql.FunctionAsync<int>(function, new object?[] { 41 }));
            Assert.Null(await sql.FunctionAsync<int?>(function, new object?[] { null }));
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);

            await Assert.ThrowsAsync<InvalidOperationException>(() => uow.ExecuteInTransactionAsync(async ct =>
            {
                await sql.ExecuteAsync($"INSERT INTO `{table}` (Value) VALUES (@Value)", new { Value = text }, ct);
                // Only the generated identifier enters the format string; values stay parameters.
                await sql.ExecuteEfAsync(System.Runtime.CompilerServices.FormattableStringFactory.Create(
                    $"INSERT INTO `{table}` (Value) VALUES ({{0}})", text), ct);
                Assert.Equal(2L, await sql.ScalarAsync<long>($"SELECT COUNT(*) FROM `{table}`", cancellationToken: ct));
                throw new InvalidOperationException("Rollback");
            }));
            Assert.Equal(0L, await sql.ScalarAsync<long>($"SELECT COUNT(*) FROM `{table}`"));
        }
        finally
        {
            await sql.ExecuteAsync($"DROP PROCEDURE IF EXISTS `{procedure}`");
            await sql.ExecuteAsync($"DROP FUNCTION IF EXISTS `{function}`");
            await sql.ExecuteAsync($"DROP TABLE IF EXISTS `{table}`");
        }
    }

    public sealed class Row
    {
        public string? Value { get; set; }
    }
}
