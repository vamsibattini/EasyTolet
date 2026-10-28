using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyTolet.Shared.Exceptions;
using EasyTolet.Shared.Interfaces;
using EasyTolet.UserService.Authentication.Tokens;
using EasyTolet.UserService.Contracts;
using EasyTolet.UserService.Repositories;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using MySqlConnector;

namespace EasyTolet.UserService.Accounts;

public sealed class AccountService(AppDbContext database, IExceptionReporter reporter, ISqlExecutor sql,
    IUnitOfWork unitOfWork, IUserBusinessRepository users, IDataProtectionProvider protection, TimeProvider clock,
    IConfiguration configuration) : IAccountService
{
    private readonly AppDbContext _database = database;
    private readonly IExceptionReporter _reporter = reporter;
    private readonly IDataProtector _protector = protection.CreateProtector("EasyTolet.AccountDelivery.v1");
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public Task<UserResponse> ProfileAsync(AccountActor actor, CancellationToken ct) => Transaction(async token =>
    {
        await Authenticate(actor, token);
        return await users.GetAsync(actor.UserId, token);
    }, ct);

    public async Task UpdateProfileAsync(AccountActor actor, ProfileRequest request, CancellationToken ct)
    {
        Validate(request);
        await Transaction(async token =>
        {
            await Authenticate(actor, token);
            await sql.ExecuteAsync("""
                UPDATE Users SET FirstName=@FirstName, LastName=@LastName, Address1=@Address1, Address2=@Address2,
                    Address3=@Address3, Country=@Country, State=@State, City=@City, PinNumber=@PinNumber,
                    AlternateMobileNumber=@AlternateMobileNumber, UpdatedBy=@id, UpdatedDate=@now WHERE Id=@id
                """, new { id = actor.UserId, now = Now, FirstName = request.FirstName.Trim(), LastName = request.LastName?.Trim(),
                    Address1 = request.Address1.Trim(), Address2 = request.Address2?.Trim(), Address3 = request.Address3?.Trim(),
                    Country = request.Country.Trim(), State = request.State.Trim(), City = request.City.Trim(),
                    PinNumber = request.PinNumber.Trim(), AlternateMobileNumber = request.AlternateMobileNumber?.Trim() }, token);
        }, ct);
    }

    public async Task ChangePasswordAsync(AccountActor actor, ChangePasswordRequest request, CancellationToken ct)
    {
        Validate(request);
        await Transaction(async token =>
        {
            var user = await Authenticate(actor, token);
            if (!PasswordHashing.Verify(user.PasswordHash, request.CurrentPassword))
                throw new ApiException("The current password is incorrect.", 400);
            await SetPassword(user.Id, request.NewPassword, token);
        }, ct);
    }

    public async Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct)
    {
        Validate(request);
        await Transaction(async token =>
        {
            var user = (await sql.QueryAsync<AccountRow>(
                "SELECT Id, EmailId, MobileNumber, PasswordHash, IsActive FROM Users WHERE EmailId=@email FOR UPDATE",
                new { email = request.Email.Trim() }, token)).SingleOrDefault();
            // Identical public response for absent, inactive, SSO-only, and throttled accounts.
            if (user is null || !user.IsActive || user.PasswordHash is null) return;
            await QueueChallenge(user, "reset", user.EmailId, user.EmailId, token);
        }, ct);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct)
    {
        Validate(request); ValidateToken(request.Token);
        await Transaction(async token =>
        {
            var hash = LoginService.Hash(request.Token);
            var hint = (await sql.QueryAsync<ChallengeRow>(
                "SELECT UserId FROM UserAccountChallenges WHERE TokenHash=@hash AND Purpose='reset'", new { hash }, token)).SingleOrDefault();
            if (hint is null) throw InvalidToken();
            var user = await LockUser(hint.UserId, token);
            var challenge = await Consume(user, request.Token, "reset", token);
            if (user.PasswordHash is null || challenge.Destination != user.EmailId) throw InvalidToken();
            await SetPassword(user.Id, request.NewPassword, token);
        }, ct);
    }

    public async Task RequestVerificationAsync(AccountActor actor, VerificationRequest request, CancellationToken ct)
    {
        Validate(request);
        if (request.Channel == "phone" && !configuration.GetValue<bool>("AccountFeatures:PhoneVerificationEnabled"))
            throw new ApiException("Phone verification is not available yet.", 503);
        await Transaction(async token =>
        {
            var user = await Authenticate(actor, token);
            var current = request.Channel == "email" ? user.EmailId : user.MobileNumber;
            var destination = request.Destination?.Trim() ?? current;
            if (request.Channel == "email" ? !new EmailAddressAttribute().IsValid(destination) || destination.Length > 320
                : !Regex.IsMatch(destination, @"\A\+[1-9][0-9]{7,14}\z"))
                throw new ApiException("Use a valid email address or an international phone number (for example +919876543210).", 400);
            await QueueChallenge(user, request.Channel, destination, current, token);
        }, ct);
    }

    public async Task ConfirmVerificationAsync(AccountActor actor, ConfirmTokenRequest request, CancellationToken ct)
    {
        Validate(request); ValidateToken(request.Token);
        await Transaction(async token =>
        {
            var user = await Authenticate(actor, token);
            var challenge = await Consume(user, request.Token, null, token);
            if (challenge.Purpose is not ("email" or "phone")) throw InvalidToken();
            var email = challenge.Purpose == "email";
            if (challenge.OriginalDestination != (email ? user.EmailId : user.MobileNumber)) throw InvalidToken();
            // SQL identifiers come only from these fixed branches; contact values are always parameters.
            await sql.ExecuteAsync(email
                ? "UPDATE Users SET EmailId=@destination, UpdatedBy=@id, UpdatedDate=@now WHERE Id=@id"
                : "UPDATE Users SET MobileNumber=@destination, UpdatedBy=@id, UpdatedDate=@now WHERE Id=@id",
                new { id = user.Id, destination = challenge.Destination, now = Now }, token);
            await sql.ExecuteAsync("""
                INSERT INTO UserVerifiedContacts (UserId, Channel, Destination, VerifiedAtUtc)
                VALUES (@id, @channel, @destination, @now)
                ON DUPLICATE KEY UPDATE Destination=@destination, VerifiedAtUtc=@now
                """, new { id = user.Id, channel = challenge.Purpose, destination = challenge.Destination, now = Now }, token);
            await InvalidateChallenges(user.Id, token);
            // Contact changes can affect account recovery. End all sessions when a contact actually changes.
            if (challenge.Destination != challenge.OriginalDestination) await RevokeAll(user.Id, token);
        }, ct);
    }

    public Task<AccountStatus> StatusAsync(AccountActor actor, CancellationToken ct) => Transaction(async token =>
    {
        var user = await Authenticate(actor, token);
        var contacts = await sql.QueryAsync<ContactRow>("SELECT Channel, Destination FROM UserVerifiedContacts WHERE UserId=@id", new { id = user.Id }, token);
        return new AccountStatus(contacts.Any(c => c.Channel == "email" && c.Destination == user.EmailId),
            contacts.Any(c => c.Channel == "phone" && c.Destination == user.MobileNumber));
    }, ct);

    public Task RequestDeletionAsync(AccountActor actor, CancellationToken ct) => Transaction(async token =>
    {
        var user = await Authenticate(actor, token);
        await QueueChallenge(user, "delete", user.EmailId, user.EmailId, token);
    }, ct);

    public async Task DeleteAsync(AccountActor actor, ConfirmTokenRequest request, CancellationToken ct)
    {
        Validate(request); ValidateToken(request.Token);
        await Transaction(async token =>
        {
            var user = await Authenticate(actor, token);
            await Consume(user, request.Token, "delete", token);
            await sql.ExecuteAsync("UPDATE Users SET IsActive=FALSE, UpdatedBy=@id, UpdatedDate=@now WHERE Id=@id", new { id = user.Id, now = Now }, token);
            await RevokeAll(user.Id, token);
            await InvalidateChallenges(user.Id, token);
        }, ct);
    }

    public Task<IReadOnlyList<AccountSession>> SessionsAsync(AccountActor actor, CancellationToken ct) => Transaction(async token =>
    {
        await Authenticate(actor, token);
        var sessions = await sql.QueryAsync<AccountSession>("""
            SELECT Id, CreatedAtUtc, LastActivityAtUtc, ExpiresAtUtc FROM UserLoginSessions
            WHERE UserId=@id AND RevokedAtUtc IS NULL AND ExpiresAtUtc>@now AND LastActivityAtUtc>@idle
              AND CredentialStamp=(SELECT UPPER(SHA2(COALESCE(PasswordHash, 'sso-only'), 256)) FROM Users WHERE Id=@id)
            ORDER BY CreatedAtUtc DESC
            """, new { id = actor.UserId, now = Now, idle = Now.AddMinutes(-30) }, token);
        foreach (var session in sessions) session.IsCurrent = session.Id == actor.SessionId.ToString("D");
        return sessions;
    }, ct);

    public Task RevokeSessionAsync(AccountActor actor, Guid sessionId, CancellationToken ct) => Transaction(async token =>
    {
        await Authenticate(actor, token);
        // Ownership is part of the write predicate. A foreign or absent ID has the same idempotent response.
        await sql.ExecuteAsync("UPDATE UserLoginSessions SET RevokedAtUtc=COALESCE(RevokedAtUtc,@now) WHERE UserId=@id AND Id=@session",
            new { id = actor.UserId, session = sessionId.ToString("D"), now = Now }, token);
    }, ct);

    public Task RevokeAllSessionsAsync(AccountActor actor, CancellationToken ct) => Transaction(async token =>
    {
        await Authenticate(actor, token);
        await RevokeAll(actor.UserId, token);
    }, ct);

    private async Task<AccountRow> LockUser(long id, CancellationToken ct)
    {
        var user = (await sql.QueryAsync<AccountRow>(
            "SELECT Id, EmailId, MobileNumber, PasswordHash, IsActive FROM Users WHERE Id=@id FOR UPDATE", new { id }, ct)).SingleOrDefault();
        if (user is null || !user.IsActive) throw new ApiException("An active account is required.", 401);
        return user;
    }

    private async Task<AccountRow> Authenticate(AccountActor actor, CancellationToken ct)
    {
        if (actor.UserId <= 0 || actor.SessionId == Guid.Empty) throw new ApiException("A valid login session is required.", 401);
        var user = await LockUser(actor.UserId, ct);
        var sessions = await sql.QueryAsync<SessionKey>("""
            SELECT Id FROM UserLoginSessions WHERE Id=@session AND UserId=@id AND RevokedAtUtc IS NULL
                AND ExpiresAtUtc>@now AND LastActivityAtUtc>@idle AND CredentialStamp=@stamp FOR UPDATE
            """, new { id = actor.UserId, session = actor.SessionId.ToString("D"), now = Now,
                idle = Now.AddMinutes(-30), stamp = Stamp(user) }, ct);
        if (sessions.Count != 1) throw new ApiException("The login session is expired or revoked.", 401);
        return user;
    }

    private async Task QueueChallenge(AccountRow user, string purpose, string destination, string original, CancellationToken ct)
    {
        var now = Now;
        // User row lock serializes requests across replicas. Retain used tokens briefly for resend throttling.
        var recent = await sql.QueryAsync<SessionKey>("""
            SELECT TokenHash AS Id FROM UserAccountChallenges WHERE UserId=@id AND Purpose=@purpose AND CreatedAtUtc>@since LIMIT 1
            """, new { id = user.Id, purpose, since = now.AddMinutes(-1) }, ct);
        if (recent.Count != 0) return;
        await sql.ExecuteAsync("UPDATE UserAccountChallenges SET ConsumedAtUtc=COALESCE(ConsumedAtUtc,@now) WHERE UserId=@id AND Purpose=@purpose",
            new { id = user.Id, purpose, now }, ct);
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var hash = LoginService.Hash(secret);
        var expires = now.AddMinutes(15);
        await sql.ExecuteAsync("""
            INSERT INTO UserAccountChallenges (TokenHash, UserId, Purpose, Destination, OriginalDestination, CredentialStamp, CreatedAtUtc, ExpiresAtUtc)
            VALUES (@hash,@id,@purpose,@destination,@original,@stamp,@now,@expires)
            """, new { hash, id = user.Id, purpose, destination, original, stamp = Stamp(user), now, expires }, ct);
        var message = new AccountMessage(Guid.NewGuid().ToString("D"), purpose == "phone" ? "sms" : "email", destination, purpose, secret, expires, now);
        // Only the protected payload contains the raw secret. Never return it through an API or log it.
        await sql.ExecuteAsync("""
            INSERT INTO UserAccountMessages (Id, TokenHash, ProtectedPayload, NextAttemptAtUtc, ExpiresAtUtc)
            VALUES (@Id,@hash,@payload,@now,@expires)
            """, new { message.Id, hash, payload = _protector.Protect(JsonSerializer.Serialize(message)), now, expires }, ct);
    }

    private async Task<ChallengeRow> Consume(AccountRow user, string secret, string? purpose, CancellationToken ct)
    {
        var hash = LoginService.Hash(secret);
        var challenge = (await sql.QueryAsync<ChallengeRow>("SELECT * FROM UserAccountChallenges WHERE TokenHash=@hash AND UserId=@id FOR UPDATE",
            new { hash, id = user.Id }, ct)).SingleOrDefault();
        if (challenge is null || challenge.UserId != user.Id || challenge.ConsumedAtUtc.HasValue || challenge.ExpiresAtUtc <= Now ||
            challenge.CredentialStamp != Stamp(user) || (purpose is not null && challenge.Purpose != purpose) ||
            challenge.OriginalDestination != (challenge.Purpose == "phone" ? user.MobileNumber : user.EmailId)) throw InvalidToken();
        await sql.ExecuteAsync("UPDATE UserAccountChallenges SET ConsumedAtUtc=@now WHERE TokenHash=@hash", new { hash, now = Now }, ct);
        return challenge;
    }

    private async Task SetPassword(long id, string password, CancellationToken ct)
    {
        await sql.ExecuteAsync("UPDATE Users SET PasswordHash=@hash, UpdatedBy=@id, UpdatedDate=@now WHERE Id=@id",
            new { id, hash = PasswordHashing.Hash(password), now = Now }, ct);
        await RevokeAll(id, ct);
        await InvalidateChallenges(id, ct);
    }
    private Task<int> RevokeAll(long id, CancellationToken ct) => sql.ExecuteAsync(
        "UPDATE UserLoginSessions SET RevokedAtUtc=COALESCE(RevokedAtUtc,@now) WHERE UserId=@id", new { id, now = Now }, ct);
    private Task<int> InvalidateChallenges(long id, CancellationToken ct) => sql.ExecuteAsync(
        "UPDATE UserAccountChallenges SET ConsumedAtUtc=COALESCE(ConsumedAtUtc,@now) WHERE UserId=@id", new { id, now = Now }, ct);
    private static string Stamp(AccountRow user) => LoginService.Hash(user.PasswordHash ?? "sso-only");
    private static ApiException InvalidToken() => new("The confirmation token is invalid or expired.", 400);
    private static void ValidateToken(string token)
    {
        if (token.Length != 43 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw InvalidToken();
    }
    private static void Validate(object request)
    {
        if (request is null || !Validator.TryValidateObject(request, new ValidationContext(request), [], true))
            throw new ApiException("Invalid account fields.", 400);
    }
    private async Task<T> Transaction<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        T result = default!;
        try { await unitOfWork.ExecuteInTransactionAsync(async token => result = await operation(token), ct); }
        catch (MySqlException error) when (error.Number is 1062 or 1451 or 1452 or 1213 or 1205)
        { throw new ApiException("The account change conflicts with existing data or another operation.", 409, error); }
        return result;
    }
    private Task Transaction(Func<CancellationToken, Task> operation, CancellationToken ct) =>
        Transaction(async token => { await operation(token); return true; }, ct);

    public sealed class AccountRow
    {
        public long Id { get; set; }
        public string EmailId { get; set; } = "";
        public string MobileNumber { get; set; } = "";
        public string? PasswordHash { get; set; }
        public bool IsActive { get; set; }
    }
    public sealed class SessionKey { public string Id { get; set; } = ""; }
    public sealed class ContactRow { public string Channel { get; set; } = ""; public string Destination { get; set; } = ""; }
    public sealed class ChallengeRow
    {
        public long UserId { get; set; }
        public string Purpose { get; set; } = "";
        public string Destination { get; set; } = "";
        public string OriginalDestination { get; set; } = "";
        public string CredentialStamp { get; set; } = "";
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime? ConsumedAtUtc { get; set; }
    }
}
