using System.Linq.Expressions;

namespace EasyTolet.Shared.Interfaces;

public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(object id, CancellationToken cancellationToken = default);
    Task<T?> GetAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> ListAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
    Task AddAsync(T entity, CancellationToken cancellationToken = default);
    [Obsolete("Stage changes using AddAsync/Update/Delete and commit through IUnitOfWork.SaveChangesAsync.")]
    Task AddAndSaveAsync(T entity, CancellationToken cancellationToken = default);
    void Update(T entity);
    [Obsolete("Stage changes using AddAsync/Update/Delete and commit through IUnitOfWork.SaveChangesAsync.")]
    Task UpdateAndSaveAsync(T entity, CancellationToken cancellationToken = default);
    void Delete(T entity);
    [Obsolete("Stage changes using AddAsync/Update/Delete and commit through IUnitOfWork.SaveChangesAsync.")]
    Task DeleteAndSaveAsync(T entity, CancellationToken cancellationToken = default);
}
