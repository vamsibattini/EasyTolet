using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using EasyTolet.Shared.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EasyTolet.Shared.Database;

public sealed class SqlExecutor(DbContext context) : ISqlExecutor
{
    public async Task<IReadOnlyList<T>> QueryEfAsync<T>(FormattableString sql, CancellationToken cancellationToken = default) =>
        await context.Database.SqlQuery<T>(sql).ToListAsync(cancellationToken);

    public Task<int> ExecuteEfAsync(FormattableString sql, CancellationToken cancellationToken = default) =>
        context.Database.ExecuteSqlInterpolatedAsync(sql, cancellationToken);

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        (await context.Database.GetDbConnection().QueryAsync<T>(Command(sql, parameters, CommandType.Text, cancellationToken))).AsList();

    public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        context.Database.GetDbConnection().ExecuteAsync(Command(sql, parameters, CommandType.Text, cancellationToken));

    public Task<T?> ScalarAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        context.Database.GetDbConnection().ExecuteScalarAsync<T>(Command(sql, parameters, CommandType.Text, cancellationToken));

    public async Task<IReadOnlyList<T>> QueryProcedureAsync<T>(string name, object? parameters = null, CancellationToken cancellationToken = default) =>
        (await context.Database.GetDbConnection().QueryAsync<T>(Command(Identifier(name), parameters, CommandType.StoredProcedure, cancellationToken))).AsList();

    public Task<int> ExecuteProcedureAsync(string name, object? parameters = null, CancellationToken cancellationToken = default) =>
        context.Database.GetDbConnection().ExecuteAsync(Command(Identifier(name), parameters, CommandType.StoredProcedure, cancellationToken));

    public Task<T?> FunctionAsync<T>(string name, IReadOnlyList<object?> arguments, CancellationToken cancellationToken = default)
    {
        Identifier(name);
        ArgumentNullException.ThrowIfNull(arguments);
        var parameters = new DynamicParameters();
        var placeholders = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var parameterName = $"arg{i}";
            placeholders.Add($"@{parameterName}");
            parameters.Add(parameterName, arguments[i]);
        }
        return ScalarAsync<T>($"SELECT `{name}`({string.Join(", ", placeholders)})", parameters, cancellationToken);
    }

    private CommandDefinition Command(string sql, object? parameters, CommandType type, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        cancellationToken.ThrowIfCancellationRequested();
        return new CommandDefinition(sql, parameters, context.Database.CurrentTransaction?.GetDbTransaction(),
            context.Database.GetCommandTimeout() ?? 30, type, cancellationToken: cancellationToken);
    }

    private static string Identifier(string name)
    {
        // Routine names are application constants, never request values or cross-database names.
        if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, "\\A[A-Za-z_][A-Za-z0-9_]{0,63}\\z"))
            throw new ArgumentException("Use an unqualified routine name of at most 64 ASCII letters, digits or underscores.", nameof(name));
        return name;
    }
}
