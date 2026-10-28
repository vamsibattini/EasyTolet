using EasyTolet.AdminService.Contracts;
using EasyTolet.AdminService.Repositories;
using EasyTolet.Contracts.Admin;
using EasyTolet.Shared.Controllers;
using EasyTolet.Shared.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EasyTolet.AdminService.Controllers;

[ApiController]
[Route("api/admin/roles")]
[Authorize(Policy = "AdminRoles")]
public sealed class AdminController(AppDbContext database, IExceptionReporter exceptionReporter,
    IRoleBusinessRepository roles) : ServiceControllerBase<AppDbContext>(database, exceptionReporter)
{
    [HttpGet]
    public Task<PagedResult<RoleSnapshot>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        roles.ListAsync(page, pageSize, ct);

    [HttpGet("{id:int:min(1)}")]
    public Task<RoleSnapshot> Get(int id, CancellationToken ct) => roles.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<RoleSnapshot>> Create(SaveRoleRequest request, CancellationToken ct)
    {
        var result = await roles.CreateAsync(request, ActorId(), ct);
        return CreatedAtAction(nameof(Get), new { id = result.RoleId }, result);
    }

    [HttpPut("{id:int:min(1)}")]
    public Task<RoleSnapshot> Update(int id, SaveRoleRequest request, CancellationToken ct) =>
        roles.UpdateAsync(id, request, ActorId(), ct);

    [HttpDelete("{id:int:min(1)}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await roles.DeleteAsync(id, ActorId(), ct);
        return NoContent();
    }

    private int ActorId() => int.TryParse(User.FindFirst("user_id")?.Value, out var id) && id > 0
        ? id : throw new ApiException("A numeric user_id claim is required.", 403);
}
