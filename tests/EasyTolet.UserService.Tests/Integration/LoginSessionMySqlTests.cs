using Dapper;
using EasyTolet.Shared.Database;
using EasyTolet.Shared.Infrastructure;
using EasyTolet.UserService.Authentication.Tokens;
using EasyTolet.UserService.Repositories;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Xunit;
using static EasyTolet.UserService.Tests.Repositories.UserBusinessRepositoryTests;

namespace EasyTolet.UserService.Tests.Integration;

public sealed class LoginSessionMySqlTests
{
    [UserMySqlFact]
    public async Task SessionsRollbackOnFailureSerializeRefreshAndRevokeOnReplayOrPasswordChange()
    {
        var connectionString = Environment.GetEnvironmentVariable("EASYTOLET_USER_TEST_MYSQL")!;
        Assert.EndsWith("_tests", new MySqlConnectionStringBuilder(connectionString).Database);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0))).Options);
        LoginSessionRepository Sessions(AppDbContext db) => new(db, new UnexpectedReporter(), new SqlExecutor(db), new UnitOfWork(db));
        await using var db = Context();
        var userRepository = new UserBusinessRepository(db, new UnexpectedReporter(), new SqlExecutor(db), new UnitOfWork(db));
        var input = Input();
        input.RoleId = int.Parse(Environment.GetEnvironmentVariable("EASYTOLET_USER_TEST_ROLE_ID")!);
        input.EmailId = $"login-{Guid.NewGuid():N}@example.com";
        long? userId = null;
        try
        {
            var user = await userRepository.RegisterAsync(input, default);
            userId = user.Id;
            var passwordHash = await connection.ExecuteScalarAsync<string>("SELECT PasswordHash FROM Users WHERE Id = @id", new { id = user.Id });
            Assert.NotEqual(input.Password, passwordHash);
            Assert.True(PasswordHashing.Verify(passwordHash, input.Password!));
            var stamp = LoginService.Hash(passwordHash!);
            var now = DateTime.UtcNow;
            var session = new LoginSession(Guid.NewGuid(), user.Id, now, now.AddHours(8), stamp);
            var hash = LoginService.Hash(Guid.NewGuid().ToString());
            var repository = Sessions(db);
            await repository.CreateAsync(session, hash, default);
            Assert.Empty((await repository.GetAccessAsync(user.Id, default)).Roles); // Signup choice is not an approved grant.
            var failedSession = session with { Id = Guid.NewGuid() };
            await Assert.ThrowsAsync<MySqlException>(() => repository.CreateAsync(failedSession, hash, default));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM UserLoginSessions WHERE Id = @id", new { id = failedSession.Id.ToString("D") }));

            async Task<LoginSession?> Rotate(string replacement)
            {
                await using var concurrentDb = Context();
                return await Sessions(concurrentDb).RotateAsync(user.Id, hash, LoginService.Hash(replacement), stamp, now.AddMinutes(20), default);
            }
            var rotations = await Task.WhenAll(Rotate("replacement-a"), Rotate("replacement-b"));
            Assert.Single(rotations, result => result is not null);
            Assert.Single(rotations, result => result is null);
            Assert.NotNull(await connection.ExecuteScalarAsync<DateTime?>("SELECT RevokedAtUtc FROM UserLoginSessions WHERE Id = @id", new { id = session.Id.ToString("D") }));

            var passwordSession = session with { Id = Guid.NewGuid() };
            var passwordToken = LoginService.Hash("password-change-" + Guid.NewGuid());
            await repository.CreateAsync(passwordSession, passwordToken, default);
            input.Password = "Changed-test-password-456!";
            await userRepository.UpdateAsync(user.Id, input, user.Id, default);
            var changedHash = await connection.ExecuteScalarAsync<string>("SELECT PasswordHash FROM Users WHERE Id = @id", new { id = user.Id });
            Assert.True(PasswordHashing.Verify(changedHash, input.Password));
            Assert.Null(await repository.RotateAsync(user.Id, passwordToken, LoginService.Hash("next"), LoginService.Hash(changedHash!), now.AddMinutes(1), default));

            var expired = session with { Id = Guid.NewGuid(), CredentialStamp = LoginService.Hash(changedHash!) };
            var expiredToken = LoginService.Hash("idle-" + Guid.NewGuid());
            await repository.CreateAsync(expired, expiredToken, default);
            Assert.Null(await repository.RotateAsync(user.Id, expiredToken, LoginService.Hash("next-idle"), expired.CredentialStamp, now.AddMinutes(30), default));
        }
        finally
        {
            if (userId.HasValue)
            {
                await connection.ExecuteAsync("DELETE FROM UserLoginSessions WHERE UserId = @id", new { id = userId.Value });
                await connection.ExecuteAsync("DELETE FROM Users WHERE Id = @id", new { id = userId.Value });
            }
        }
    }
}
