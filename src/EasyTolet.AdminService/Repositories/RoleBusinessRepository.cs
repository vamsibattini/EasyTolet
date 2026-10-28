using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text.Json;
using EasyTolet.AdminService.Contracts;
using EasyTolet.AdminService.Domain;
using EasyTolet.Contracts.Admin;
using EasyTolet.Shared.Exceptions;
using EasyTolet.Shared.Interfaces;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace EasyTolet.AdminService.Repositories;

public sealed class RoleBusinessRepository : IRoleBusinessRepository
{
    private readonly AppDbContext _database;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _clock;
    // Available for deliberately handled errors. Unhandled errors go to middleware exactly once.
    private readonly IExceptionReporter _exceptionReporter;

    public RoleBusinessRepository(AppDbContext database, IUnitOfWork unitOfWork,
        IExceptionReporter exceptionReporter, TimeProvider clock)
    {
        _database = database;
        _unitOfWork = unitOfWork;
        _exceptionReporter = exceptionReporter;
        _clock = clock;
    }

    public async Task<PagedResult<RoleSnapshot>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue)
            throw new ApiException("Invalid pagination.", 400);
        var query = _database.Roles.AsNoTracking();
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(r => r.RoleId).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(rows.Select(Snapshot).ToArray(), page, pageSize, total);
    }

    public async Task<RoleSnapshot> GetAsync(int id, CancellationToken ct) => Snapshot(
        await _unitOfWork.Repository<Role>().GetByIdAsync(id, ct) ?? throw new ApiException("Role not found.", 404));

    public Task<RoleSnapshot> CreateAsync(SaveRoleRequest request, int actorId, CancellationToken ct) =>
        MutateAsync(null, request, actorId, "Created", ct);

    public Task<RoleSnapshot> UpdateAsync(int id, SaveRoleRequest request, int actorId, CancellationToken ct) =>
        MutateAsync(id, request, actorId, "Updated", ct);

    public async Task DeleteAsync(int id, int actorId, CancellationToken ct) =>
        await MutateAsync(id, null, actorId, "Deleted", ct);

    private async Task<RoleSnapshot> MutateAsync(int? id, SaveRoleRequest? request, int actorId, string operation, CancellationToken ct)
    {
        if (actorId <= 0) throw new ApiException("A numeric user_id claim is required.", 403);
        if (request is not null) Validate(request);
        // Serializable hierarchy reads prevent concurrent reparenting from creating a cycle in MySQL.
        await using var transaction = await _database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var repository = _unitOfWork.Repository<Role>();
            var role = id.HasValue ? await repository.GetByIdAsync(id.Value, ct)
                ?? throw new ApiException("Role not found.", 404) : new Role();
            var hierarchy = await _database.Roles.AsNoTracking().ToListAsync(ct);
            var now = _clock.GetUtcNow().UtcDateTime;
            if (operation == "Deleted")
            {
                if (hierarchy.Any(r => r.ParentRoleId == role.RoleId))
                    throw new ApiException("Reassign child roles before deleting their parent.", 409);
                repository.Delete(role);
            }
            else
            {
                var code = request!.RoleCode.ToUpperInvariant();
                if (hierarchy.Any(r => r.RoleId != role.RoleId && r.RoleCode.Equals(code, StringComparison.OrdinalIgnoreCase)))
                    throw new ApiException("Role code already exists.", 409);
                ValidateParent(role.RoleId, request.ParentRoleId, hierarchy);
                role.RoleCode = code;
                role.RoleName = request.RoleName.Trim();
                role.ParentRoleId = request.ParentRoleId;
                role.Description = request.Description?.Trim();
                role.IsActive = request.IsActive;
                if (id is null)
                {
                    role.CreatedDate = now; role.CreatedBy = actorId;
                    await repository.AddAsync(role, ct);
                }
                else
                {
                    role.UpdatedDate = now; role.UpdatedBy = actorId;
                    repository.Update(role);
                }
            }
            // Obtain the generated RoleId; both saves are inside the same transaction.
            await _unitOfWork.SaveChangesAsync(ct);
            var snapshot = Snapshot(role);
            var message = new RoleChangedV1(Guid.NewGuid(), 0, now, operation, actorId, snapshot);
            await _unitOfWork.Repository<AdminOutboxMessage>().AddAsync(new AdminOutboxMessage
            {
                EventId = message.EventId, OccurredAtUtc = now, Payload = JsonSerializer.Serialize(message)
            }, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return snapshot;
        }
        catch (Exception error)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _database.ChangeTracker.Clear();
            var mysql = error as MySqlException ?? error.InnerException as MySqlException;
            if (mysql?.Number is 1062 or 1451 or 1452 or 1213 or 1205)
                throw new ApiException("The role change conflicts with existing data or another operation; reload and retry.", 409, error);
            throw;
        }
    }

    private static void Validate(SaveRoleRequest request)
    {
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), errors, true) ||
            string.IsNullOrWhiteSpace(request.RoleName))
            throw new ApiException("Invalid role fields.", 400);
    }

    private static void ValidateParent(int roleId, int? parentId, IReadOnlyList<Role> hierarchy)
    {
        var roles = hierarchy.ToDictionary(r => r.RoleId);
        var seen = new HashSet<int>();
        while (parentId.HasValue)
        {
            if (parentId.Value == roleId || !seen.Add(parentId.Value))
                throw new ApiException("Role hierarchy cannot contain a cycle.", 409);
            if (!roles.TryGetValue(parentId.Value, out var parent))
                throw new ApiException("Parent role does not exist.", 400);
            parentId = parent.ParentRoleId;
        }
    }

    private static RoleSnapshot Snapshot(Role r) => new(r.RoleId, r.RoleCode, r.RoleName, r.ParentRoleId,
        r.Description, r.IsActive, r.CreatedDate, r.UpdatedDate, r.CreatedBy, r.UpdatedBy);
}
