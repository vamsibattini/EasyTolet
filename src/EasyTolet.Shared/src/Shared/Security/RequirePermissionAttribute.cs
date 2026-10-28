using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace EasyTolet.Shared.Security;

// Combine with [Authorize(Roles = "ADMIN")] when both a role and permission are required.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public RequirePermissionAttribute(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        Policy = PermissionPolicyProvider.Prefix + permission;
    }
}

public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "permission:";
    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(Prefix, StringComparison.Ordinal)) return base.GetPolicyAsync(policyName);
        var permission = policyName[Prefix.Length..];
        if (string.IsNullOrWhiteSpace(permission)) return Task.FromResult<AuthorizationPolicy?>(null);
        return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder("Bearer")
            .RequireAuthenticatedUser().RequireClaim("permission", permission).Build());
    }
}
