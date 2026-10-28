using EasyTolet.Shared.Controllers;
using EasyTolet.Shared.Exceptions;
using EasyTolet.UserService.Contracts;
using EasyTolet.UserService.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EasyTolet.UserService.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public sealed class UsersController(AppDbContext database, IExceptionReporter exceptionReporter,
    IUserBusinessRepository users) : ServiceControllerBase<AppDbContext>(database, exceptionReporter)
{
    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<ActionResult<UserResponse>> Register(RegisterUserRequest request, CancellationToken ct)
    {
        var user = await users.RegisterAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = user.Id }, user);
    }

    [Authorize(Policy = "ManageUsers")]
    [HttpGet]
    public Task<IReadOnlyList<UserResponse>> List([FromQuery] string? userId, CancellationToken ct) =>
        users.ListAsync(userId, ct);

    [HttpGet("{id:long:min(1)}")]
    public Task<UserResponse> Get(long id, CancellationToken ct)
    {
        if (!User.HasClaim("permission", "users.manage") && ActorId() != id)
            throw new ApiException("You cannot access another user's profile.", 403);
        return users.GetAsync(id, ct);
    }

    [Authorize(Policy = "ManageUsers")]
    [HttpPut("{id:long:min(1)}")]
    public Task<UserResponse> Update(long id, SaveUserRequest request, CancellationToken ct) =>
        users.UpdateAsync(id, request, ActorId(), ct);

    [Authorize(Policy = "ManageUsers")]
    [HttpDelete("{id:long:min(1)}")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        await users.DeleteAsync(id, ActorId(), ct);
        return NoContent();
    }

    private long ActorId() => long.TryParse(User.FindFirst("user_id")?.Value, out var id) && id > 0
        ? id : throw new ApiException("A numeric user_id claim is required.", 403);
}
