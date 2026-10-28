using EasyTolet.UserService.Contracts;

namespace EasyTolet.UserService.Repositories;

public interface IUserBusinessRepository
{
    Task<UserResponse> RegisterAsync(RegisterUserRequest request, CancellationToken ct);
    Task<IReadOnlyList<UserResponse>> ListAsync(string? userId, CancellationToken ct);
    Task<UserResponse> GetAsync(long id, CancellationToken ct);
    Task<UserResponse> UpdateAsync(long id, SaveUserRequest request, long actorId, CancellationToken ct);
    Task DeleteAsync(long id, long actorId, CancellationToken ct);
}
