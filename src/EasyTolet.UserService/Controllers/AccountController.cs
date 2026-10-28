using EasyTolet.Shared.Controllers;
using EasyTolet.Shared.Exceptions;
using EasyTolet.UserService.Accounts;
using EasyTolet.UserService.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EasyTolet.UserService.Controllers;

[ApiController]
[Route("api/account")]
[Authorize(AuthenticationSchemes = "Bearer")]
[EnableRateLimiting("Login")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountController(AppDbContext database, IExceptionReporter exceptionReporter,
    IAccountService accounts) : ServiceControllerBase<AppDbContext>(database, exceptionReporter)
{
    [HttpGet("profile")]
    public Task<UserResponse> Profile(CancellationToken ct) => accounts.ProfileAsync(Actor(), ct);

    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile(ProfileRequest request, CancellationToken ct)
    { await accounts.UpdateProfileAsync(Actor(), request, ct); return NoContent(); }

    [HttpPost("password/change")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    { await accounts.ChangePasswordAsync(Actor(), request, ct); return NoContent(); }

    [AllowAnonymous, HttpPost("password/forgot")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        await accounts.ForgotPasswordAsync(request, ct);
        return Accepted(new { message = "If this account supports password recovery, a message will be sent." });
    }

    [AllowAnonymous, HttpPost("password/reset")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct)
    { await accounts.ResetPasswordAsync(request, ct); return NoContent(); }

    [HttpGet("verification")]
    public Task<AccountStatus> VerificationStatus(CancellationToken ct) => accounts.StatusAsync(Actor(), ct);

    [HttpPost("verification/request")]
    public async Task<IActionResult> RequestVerification(VerificationRequest request, CancellationToken ct)
    { await accounts.RequestVerificationAsync(Actor(), request, ct); return Accepted(); }

    [HttpPost("verification/confirm")]
    public async Task<IActionResult> ConfirmVerification(ConfirmTokenRequest request, CancellationToken ct)
    { await accounts.ConfirmVerificationAsync(Actor(), request, ct); return NoContent(); }

    [HttpPost("deletion/request")]
    public async Task<IActionResult> RequestDeletion(CancellationToken ct)
    { await accounts.RequestDeletionAsync(Actor(), ct); return Accepted(); }

    [HttpPost("deletion/confirm")]
    public async Task<IActionResult> Delete(ConfirmTokenRequest request, CancellationToken ct)
    { await accounts.DeleteAsync(Actor(), request, ct); return NoContent(); }

    [HttpGet("sessions")]
    public Task<IReadOnlyList<AccountSession>> Sessions(CancellationToken ct) => accounts.SessionsAsync(Actor(), ct);

    [HttpDelete("sessions/{sessionId:guid}")]
    public async Task<IActionResult> RevokeSession(Guid sessionId, CancellationToken ct)
    { await accounts.RevokeSessionAsync(Actor(), sessionId, ct); return NoContent(); }

    [HttpDelete("sessions")]
    public async Task<IActionResult> RevokeAllSessions(CancellationToken ct)
    { await accounts.RevokeAllSessionsAsync(Actor(), ct); return NoContent(); }

    private AccountActor Actor() => long.TryParse(User.FindFirst("user_id")?.Value, out var id) && id > 0 &&
        Guid.TryParse(User.FindFirst("sid")?.Value, out var session) && session != Guid.Empty
        ? new(id, session) : throw new ApiException("A valid user and login session are required.", 401);
}
