using Dapper;
using EasyTolet.Shared.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace EasyTolet.Shared.Exceptions;

/// <summary>Uses its own connection, outside failed EF transactions and ambient TransactionScope.</summary>
public sealed class MySqlExceptionLogSink(IConfiguration configuration, IHostEnvironment environment,
    IOptions<ExceptionLoggingOptions> options) : IExceptionLogSink
{
    public async Task WriteAsync(ExceptionLogEntry entry, CancellationToken cancellationToken)
    {
        if (!options.Value.DatabaseEnabled) return;
        var dedicated = configuration.GetSection("ExceptionLogging:Database");
        var settings = (dedicated.Exists() ? dedicated : configuration.GetSection("Database"))
            .Get<MySqlDatabaseOptions>() ?? throw new InvalidOperationException("Exception logging database is not configured.");
        var connectionOverride = configuration.GetConnectionString("ExceptionLogging")
            ?? (dedicated.Exists() ? null : configuration.GetConnectionString("DefaultConnection"));
        var builder = new MySqlConnectionStringBuilder(
            MySqlConnectionStringHelper.Build(settings, environment.IsDevelopment(), connectionOverride))
        {
            AutoEnlist = false,
            ConnectionTimeout = (uint)options.Value.SinkTimeoutSeconds,
            DefaultCommandTimeout = (uint)options.Value.SinkTimeoutSeconds,
            MaximumPoolSize = 10,
            MinimumPoolSize = 0,
            CancellationTimeout = 1
        };
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(InsertSql, entry,
            commandTimeout: options.Value.SinkTimeoutSeconds, cancellationToken: cancellationToken));
    }

    private const string InsertSql = """
        INSERT INTO ExceptionLogs
        (Id, OccurredAtUtc, Service, Environment, Instance, CorrelationId, TraceId,
         Method, Path, Controller, Action, Repository, StatusCode, ResponseStarted, ExceptionType, Message, ExceptionDetails)
        VALUES
        (@Id, @OccurredAtUtc, @Service, @Environment, @Instance, @CorrelationId, @TraceId,
         @Method, @Path, @Controller, @Action, @Repository, @StatusCode, @ResponseStarted, @ExceptionType, @Message, @ExceptionDetails)
        """;
}
