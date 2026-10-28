namespace EasyTolet.Shared.Interfaces;

/// <summary>
/// Executes SQL on the service's EF connection and current transaction. Never pass user-built SQL.
/// Dapper methods accept anonymous parameters or DynamicParameters (including output parameters).
/// Writes execute immediately; wrap mixed writes in IUnitOfWork.ExecuteInTransactionAsync.
/// </summary>
public interface ISqlExecutor
{
    Task<IReadOnlyList<T>> QueryEfAsync<T>(FormattableString sql, CancellationToken cancellationToken = default);
    Task<int> ExecuteEfAsync(FormattableString sql, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);
    Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default);
    Task<T?> ScalarAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> QueryProcedureAsync<T>(string name, object? parameters = null, CancellationToken cancellationToken = default);
    Task<int> ExecuteProcedureAsync(string name, object? parameters = null, CancellationToken cancellationToken = default);
    Task<T?> FunctionAsync<T>(string name, IReadOnlyList<object?> arguments, CancellationToken cancellationToken = default);
}
