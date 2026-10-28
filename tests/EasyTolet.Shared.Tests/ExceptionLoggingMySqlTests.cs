using Dapper;
using EasyTolet.Shared.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Xunit;

namespace EasyTolet.Shared.Tests;

public sealed class ExceptionLoggingMySqlTests
{
    [MySqlFact]
    public async Task DatabaseSinkPersistsParameterizedDetailsOutsideAmbientRollback()
    {
        var connectionString = Environment.GetEnvironmentVariable("EASYTOLET_TEST_MYSQL")!;
        var settings = new MySqlConnectionStringBuilder(connectionString);
        Assert.EndsWith("_tests", settings.Database);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "ExceptionLogs.sql")));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Database"] = settings.Database,
            ["ConnectionStrings:DefaultConnection"] = connectionString
        }).Build();
        var sink = new MySqlExceptionLogSink(config, new ExceptionHandlingTests.TestEnvironment(),
            Options.Create(new ExceptionLoggingOptions()));
        var entry = ExceptionHandlingTests.Entry() with { Message = "quote '; DROP TABLE ExceptionLogs; --", Repository = "UserRepository" };
        try
        {
            using (var transaction = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
            {
                await sink.WriteAsync(entry, default);
                // No Complete(): the ambient transaction is rolled back on disposal.
            }
            var actual = await connection.QuerySingleAsync<StoredEntry>(
                "SELECT Id, Message, ExceptionDetails, Repository FROM ExceptionLogs WHERE Id = @Id", new { entry.Id });
            Assert.Equal(entry.Id, actual.Id);
            Assert.Equal(entry.Message, actual.Message);
            Assert.Equal(entry.ExceptionDetails, actual.ExceptionDetails);
            Assert.Equal("UserRepository", actual.Repository);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM ExceptionLogs WHERE Id = @Id", new { entry.Id });
        }
    }

    public sealed class StoredEntry
    {
        public string Id { get; set; } = "";
        public string Message { get; set; } = "";
        public string ExceptionDetails { get; set; } = "";
        public string? Repository { get; set; }
    }
}
