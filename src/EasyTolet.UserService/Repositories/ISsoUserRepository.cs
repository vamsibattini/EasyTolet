using EasyTolet.UserService.Authentication;
using EasyTolet.UserService.Contracts;

namespace EasyTolet.UserService.Repositories;

public interface ISsoUserRepository
{
    Task<UserResponse?> FindSsoAsync(SsoIdentity identity, CancellationToken ct);
    Task<UserResponse> RegisterSsoAsync(RegisterUserRequest request, SsoIdentity identity, CancellationToken ct);
}
