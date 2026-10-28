using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using EasyTolet.UserService.Contracts;

namespace EasyTolet.UserService.Accounts;

// Reject privilege fields instead of silently accepting RoleId/IsActive/Password from an old admin DTO.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProfileRequest
{
    [Required, StringLength(150)] public string FirstName { get; set; } = "";
    [StringLength(150)] public string? LastName { get; set; }
    [Required, StringLength(500)] public string Address1 { get; set; } = "";
    [StringLength(500)] public string? Address2 { get; set; }
    [StringLength(500)] public string? Address3 { get; set; }
    [Required, StringLength(100)] public string Country { get; set; } = "";
    [Required, StringLength(150)] public string State { get; set; } = "";
    [Required, StringLength(150)] public string City { get; set; } = "";
    [Required, StringLength(20)] public string PinNumber { get; set; } = "";
    [StringLength(30)] public string? AlternateMobileNumber { get; set; }
}

public sealed class ChangePasswordRequest
{
    [Required, StringLength(128)] public string CurrentPassword { get; set; } = "";
    [Required, StringLength(128, MinimumLength = 12)] public string NewPassword { get; set; } = "";
}
public sealed class ForgotPasswordRequest
{
    [Required, EmailAddress, StringLength(320)] public string Email { get; set; } = "";
}
public class ConfirmTokenRequest
{
    [Required, StringLength(43, MinimumLength = 43)] public string Token { get; set; } = "";
}
public sealed class ResetPasswordRequest : ConfirmTokenRequest
{
    [Required, StringLength(128, MinimumLength = 12)] public string NewPassword { get; set; } = "";
}
public sealed class VerificationRequest
{
    [Required, RegularExpression("^(email|phone)$")] public string Channel { get; set; } = "";
    // Omit to verify the current contact; provide a value to change it after proof of ownership.
    [StringLength(320)] public string? Destination { get; set; }
}
public sealed record AccountActor(long UserId, Guid SessionId);
public sealed record AccountStatus(bool EmailVerified, bool PhoneVerified);
public sealed class AccountSession
{
    public string Id { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastActivityAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public bool IsCurrent { get; set; }
}
public interface IAccountService
{
    Task<UserResponse> ProfileAsync(AccountActor actor, CancellationToken ct);
    Task UpdateProfileAsync(AccountActor actor, ProfileRequest request, CancellationToken ct);
    Task ChangePasswordAsync(AccountActor actor, ChangePasswordRequest request, CancellationToken ct);
    Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct);
    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct);
    Task RequestVerificationAsync(AccountActor actor, VerificationRequest request, CancellationToken ct);
    Task ConfirmVerificationAsync(AccountActor actor, ConfirmTokenRequest request, CancellationToken ct);
    Task<AccountStatus> StatusAsync(AccountActor actor, CancellationToken ct);
    Task RequestDeletionAsync(AccountActor actor, CancellationToken ct);
    Task DeleteAsync(AccountActor actor, ConfirmTokenRequest request, CancellationToken ct);
    Task<IReadOnlyList<AccountSession>> SessionsAsync(AccountActor actor, CancellationToken ct);
    Task RevokeSessionAsync(AccountActor actor, Guid sessionId, CancellationToken ct);
    Task RevokeAllSessionsAsync(AccountActor actor, CancellationToken ct);
}
