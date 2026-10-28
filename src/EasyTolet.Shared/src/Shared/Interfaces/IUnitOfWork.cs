namespace EasyTolet.Shared.Interfaces;

using System.Linq.Expressions;

public interface IUnitOfWork
{
    IRepository<T> Repository<T>() where T : class;
    Task<T?> GetAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) where T : class;
    Task<T?> GetByIdAsync<T>(object id, CancellationToken cancellationToken = default) where T : class;
    Task InsertAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class;
    Task UpdateAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class;
    Task DeleteAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class;
    [Obsolete("Use ISqlExecutor.QueryProcedureAsync with named parameters instead.")]
    Task<IReadOnlyList<TResult>> ExecuteStoredProcedureAsync<TResult>(string sql, CancellationToken cancellationToken = default, params object[] parameters);
    /// <summary>Run database-only work, save pending EF changes, and commit atomically. Rollback clears tracked entities.</summary>
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
