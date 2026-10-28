using EasyTolet.AdminService.Contracts;
using EasyTolet.Contracts.Admin;

namespace EasyTolet.AdminService.Repositories;

public interface IRoleBusinessRepository
{
    Task<PagedResult<RoleSnapshot>> ListAsync(int page, int pageSize, CancellationToken ct);
    Task<RoleSnapshot> GetAsync(int id, CancellationToken ct);
    Task<RoleSnapshot> CreateAsync(SaveRoleRequest request, int actorId, CancellationToken ct);
    Task<RoleSnapshot> UpdateAsync(int id, SaveRoleRequest request, int actorId, CancellationToken ct);
    Task DeleteAsync(int id, int actorId, CancellationToken ct);
}
