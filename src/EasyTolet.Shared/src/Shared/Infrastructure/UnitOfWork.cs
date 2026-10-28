using EasyTolet.Shared.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using System.Linq.Expressions;

namespace EasyTolet.Shared.Infrastructure;

public class UnitOfWork : IUnitOfWork
{
    private readonly DbContext _context;
    private readonly Dictionary<Type, object> _repositories = new();
    public UnitOfWork(DbContext context) => _context = context;

    public IRepository<T> Repository<T>() where T : class
    {
        if (_repositories.TryGetValue(typeof(T), out var repository))
        {
            return (IRepository<T>)repository;
        }

        var createdRepository = new Repository<T>(_context);
        _repositories[typeof(T)] = createdRepository;
        return createdRepository;
    }

    public Task<T?> GetAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) where T : class =>
        Repository<T>().GetAsync(predicate, cancellationToken);

    public Task<T?> GetByIdAsync<T>(object id, CancellationToken cancellationToken = default) where T : class =>
        Repository<T>().GetByIdAsync(id, cancellationToken);

    public Task InsertAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class =>
        Repository<T>().AddAsync(entity, cancellationToken);

    public Task UpdateAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        Repository<T>().Update(entity);
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        Repository<T>().Delete(entity);
        return Task.CompletedTask;
    }

    [Obsolete("Use ISqlExecutor.QueryProcedureAsync with named parameters instead.")]
    public Task<IReadOnlyList<TResult>> ExecuteStoredProcedureAsync<TResult>(string sql, CancellationToken cancellationToken = default, params object[] parameters)
    {
        // Compatibility path for existing CALL statements. Require explicitly named input parameters.
        var values = new Dapper.DynamicParameters();
        foreach (var parameter in parameters)
        {
            if (parameter is not DbParameter dbParameter || string.IsNullOrWhiteSpace(dbParameter.ParameterName) ||
                dbParameter.Direction != System.Data.ParameterDirection.Input)
                throw new ArgumentException("Use named DbParameter inputs, or ISqlExecutor with DynamicParameters for output parameters.", nameof(parameters));
            values.Add(dbParameter.ParameterName, dbParameter.Value is DBNull ? null : dbParameter.Value,
                dbParameter.DbType, size: dbParameter.Size > 0 ? dbParameter.Size : null);
        }
        return new EasyTolet.Shared.Database.SqlExecutor(_context).QueryAsync<TResult>(sql, values, cancellationToken);
    }

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Nested Unit of Work transactions are not supported.");
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await operation(cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            finally
            {
                // Rolled-back entities must not be accidentally saved later in this scope.
                _context.ChangeTracker.Clear();
            }
            throw;
        }
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        await _context.SaveChangesAsync(cancellationToken);
}
