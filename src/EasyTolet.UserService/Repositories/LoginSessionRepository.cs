using EasyTolet.Shared.Exceptions;
using EasyTolet.Shared.Interfaces;
using EasyTolet.UserService.Authentication.Tokens;

namespace EasyTolet.UserService.Repositories;

public sealed class LoginSessionRepository(AppDbContext database, IExceptionReporter exceptionReporter,
    ISqlExecutor sql, IUnitOfWork unitOfWork) : ILoginSessionRepository
{
    private readonly AppDbContext _database = database;
    private readonly IExceptionReporter _exceptionReporter = exceptionReporter;

    public async Task<ApprovedAccess> GetAccessAsync(long userId, CancellationToken ct)
    {
        var grants = await sql.QueryAsync<GrantRow>("""
            SELECT r.RoleCode AS Role, p.Permission
            FROM Users u
            INNER JOIN UserRoleGrants g ON g.UserId = u.Id AND g.RoleId = u.RoleId
            INNER JOIN Roles r ON r.RoleId = g.RoleId AND r.IsActive = TRUE
            LEFT JOIN RolePermissions p ON p.RoleId = r.RoleId
            WHERE u.Id = @userId AND u.IsActive = TRUE
            """, new { userId }, ct);
        return new(grants.Select(g => g.Role).Distinct(StringComparer.Ordinal).ToArray(),
            grants.Where(g => g.Permission is not null).Select(g => g.Permission!).Distinct(StringComparer.Ordinal).ToArray());
    }

    public Task CreateAsync(LoginSession session, string tokenHash, CancellationToken ct) =>
        unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await sql.ExecuteAsync("""
                INSERT INTO UserLoginSessions (Id, UserId, CreatedAtUtc, LastActivityAtUtc, ExpiresAtUtc, CredentialStamp)
                VALUES (@id, @UserId, @CreatedAtUtc, @CreatedAtUtc, @ExpiresAtUtc, @CredentialStamp)
                """, new { id = session.Id.ToString("D"), session.UserId, session.CreatedAtUtc, session.ExpiresAtUtc, session.CredentialStamp }, token);
            await InsertTokenAsync(session.Id.ToString("D"), tokenHash, session.CreatedAtUtc, token);
        }, ct);

    public async Task<LoginSession?> GetSessionAsync(long userId, string tokenHash, CancellationToken ct)
    {
        var rows = await sql.QueryAsync<SessionRow>("""
            SELECT s.Id, s.UserId, s.CreatedAtUtc, s.ExpiresAtUtc, s.CredentialStamp
            FROM UserLoginSessions s INNER JOIN UserRefreshTokens t ON t.SessionId = s.Id
            WHERE t.TokenHash = @tokenHash AND s.UserId = @userId
            """, new { tokenHash, userId }, ct);
        var row = rows.SingleOrDefault();
        return row is null ? null : new(Guid.Parse(row.Id), row.UserId, row.CreatedAtUtc, row.ExpiresAtUtc, row.CredentialStamp);
    }

    public async Task<LoginSession?> RotateAsync(long userId, string tokenHash, string replacementHash, string credentialStamp, DateTime now, CancellationToken ct)
    {
        LoginSession? result = null;
        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var session = await LockSessionAsync(userId, tokenHash, token);
            if (session is null) return;
            var refresh = (await sql.QueryAsync<RefreshRow>(
                "SELECT ConsumedAtUtc FROM UserRefreshTokens WHERE TokenHash = @tokenHash FOR UPDATE", new { tokenHash }, token)).Single();
            if (session.RevokedAtUtc.HasValue) return;
            if (!session.UserIsActive || session.CurrentCredentialStamp != credentialStamp || session.CredentialStamp != credentialStamp || refresh.ConsumedAtUtc.HasValue || session.ExpiresAtUtc <= now || session.LastActivityAtUtc.AddMinutes(30) <= now)
            {
                // Commit family revocation on replay/expiry. Throwing inside this transaction would undo it.
                await RevokeSessionAsync(session.Id, now, token);
                return;
            }
            await sql.ExecuteAsync("UPDATE UserRefreshTokens SET ConsumedAtUtc = @now WHERE TokenHash = @tokenHash", new { now, tokenHash }, token);
            await InsertTokenAsync(session.Id, replacementHash, now, token);
            await sql.ExecuteAsync("UPDATE UserLoginSessions SET LastActivityAtUtc = @now WHERE Id = @id", new { now, id = session.Id }, token);
            result = new(Guid.Parse(session.Id), session.UserId, session.CreatedAtUtc, session.ExpiresAtUtc, session.CredentialStamp);
        }, ct);
        return result;
    }

    public Task RevokeAsync(long userId, string tokenHash, DateTime now, CancellationToken ct) =>
        unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var session = await LockSessionAsync(userId, tokenHash, token);
            if (session is not null) await RevokeSessionAsync(session.Id, now, token);
        }, ct);

    private async Task<SessionRow?> LockSessionAsync(long userId, string tokenHash, CancellationToken ct)
    {
        // Lock the family before inspecting token consumption so concurrent refreshes serialize across replicas.
        // The subquery identifies the family without accepting a caller-supplied session/user identifier.
        var rows = await sql.QueryAsync<SessionRow>("""
            SELECT s.Id, s.UserId, s.CreatedAtUtc, s.LastActivityAtUtc, s.ExpiresAtUtc, s.RevokedAtUtc, s.CredentialStamp,
                   u.IsActive AS UserIsActive, UPPER(SHA2(COALESCE(u.PasswordHash, 'sso-only'), 256)) AS CurrentCredentialStamp
            FROM UserLoginSessions s INNER JOIN Users u ON u.Id = s.UserId
            WHERE s.Id = (SELECT SessionId FROM UserRefreshTokens WHERE TokenHash = @tokenHash)
              AND s.UserId = @userId FOR UPDATE
            """, new { tokenHash, userId }, ct);
        return rows.SingleOrDefault();
    }
    private Task<int> RevokeSessionAsync(string id, DateTime now, CancellationToken ct) =>
        sql.ExecuteAsync("UPDATE UserLoginSessions SET RevokedAtUtc = COALESCE(RevokedAtUtc, @now) WHERE Id = @id", new { now, id }, ct);
    private Task<int> InsertTokenAsync(string sessionId, string tokenHash, DateTime now, CancellationToken ct) =>
        sql.ExecuteAsync("INSERT INTO UserRefreshTokens (TokenHash, SessionId, CreatedAtUtc) VALUES (@tokenHash, @sessionId, @now)", new { tokenHash, sessionId, now }, ct);

    public sealed class GrantRow { public string Role { get; set; } = ""; public string? Permission { get; set; } }
    public sealed class RefreshRow { public DateTime? ConsumedAtUtc { get; set; } }
    public sealed class SessionRow
    {
        public string Id { get; set; } = "";
        public long UserId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime LastActivityAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public bool UserIsActive { get; set; }
        public string CurrentCredentialStamp { get; set; } = "";
        public string CredentialStamp { get; set; } = "";
        public DateTime? RevokedAtUtc { get; set; }
    }
}
