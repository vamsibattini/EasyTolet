using EasyTolet.Shared.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace EasyTolet.Shared.Infrastructure;

public class Repository<T> : IRepository<T> where T : class
{
    private readonly DbContext _context;
    public Repository(DbContext context) => _context = context;

    public async Task<T?> GetByIdAsync(object id, CancellationToken cancellationToken = default) =>
        await _context.Set<T>().FindAsync(new[] { id }, cancellationToken);

    public async Task<T?> GetAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        await _context.Set<T>().FirstOrDefaultAsync(predicate, cancellationToken);

    public async Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default) =>
        await _context.Set<T>().ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<T>> ListAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        await _context.Set<T>().Where(predicate).ToListAsync(cancellationToken);

    public async Task AddAsync(T entity, CancellationToken cancellationToken = default) =>
        await _context.Set<T>().AddAsync(entity, cancellationToken);

    [Obsolete("Stage changes using AddAsync/Update/Delete and commit through IUnitOfWork.SaveChangesAsync.")]
    public async Task AddAndSaveAsync(T entity, CancellationToken cancellationToken = default)
    {
        await AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public void Update(T entity) => _context.Set<T>().Update(entity);

    [Obsolete("Stage changes using AddAsync/Update/Delete and commit through IUnitOfWork.SaveChangesAsync.")]
    public async Task UpdateAndSaveAsync(T entity, CancellationToken cancellationToken = default)
    {
        Update(entity);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public void Delete(T entity) => _context.Set<T>().Remove(entity);

    [Obsolete("Stage changes using AddAsync/Update/Delete and commit through IUnitOfWork.SaveChangesAsync.")]
    public async Task DeleteAndSaveAsync(T entity, CancellationToken cancellationToken = default)
    {
        Delete(entity);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
