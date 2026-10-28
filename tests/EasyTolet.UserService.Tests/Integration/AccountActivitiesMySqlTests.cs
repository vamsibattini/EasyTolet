using System.Reflection;
using System.Text.Json;
using Dapper;
using EasyTolet.Shared.Database;
using EasyTolet.Shared.Exceptions;
using EasyTolet.Shared.Infrastructure;
using EasyTolet.Shared.Interfaces;
using EasyTolet.UserService.Accounts;
using EasyTolet.UserService.Authentication.Tokens;
using EasyTolet.UserService.Repositories;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Xunit;
using static EasyTolet.UserService.Tests.Repositories.UserBusinessRepositoryTests;

namespace EasyTolet.UserService.Tests.Integration;

public sealed class AccountActivitiesMySqlTests
{
    [UserMySqlFact]
    public async Task AccountActivitiesEnforceOwnershipSingleUseAndTransactionalRollbackInMySql()
    {
        var connectionString = Environment.GetEnvironmentVariable("EASYTOLET_USER_TEST_MYSQL")!;
        Assert.EndsWith("_tests", new MySqlConnectionStringBuilder(connectionString).Database);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0))).Options);
        var protection = new EphemeralDataProtectionProvider();
        var protector = protection.CreateProtector("EasyTolet.AccountDelivery.v1");
        AccountService Accounts(AppDbContext context, ISqlExecutor? executor = null) => new(context, new UnexpectedReporter(), executor ?? new SqlExecutor(context),
            new UnitOfWork(context), new UserBusinessRepository(context, new UnexpectedReporter(), new SqlExecutor(context), new UnitOfWork(context)), protection, TimeProvider.System,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AccountFeatures:PhoneVerificationEnabled"] = "true" }).Build());
        await using var db = Context();
        var users = new UserBusinessRepository(db, new UnexpectedReporter(), new SqlExecutor(db), new UnitOfWork(db));
        var input = Input(); input.RoleId = int.Parse(Environment.GetEnvironmentVariable("EASYTOLET_USER_TEST_ROLE_ID")!);
        input.EmailId = $"account-{Guid.NewGuid():N}@example.com";
        var ownedIds = new List<long>();
        async Task<AccountActor> Session(long id)
        {
            var hash = await connection.ExecuteScalarAsync<string>("SELECT PasswordHash FROM Users WHERE Id=@id", new { id });
            var session = new LoginSession(Guid.NewGuid(), id, DateTime.UtcNow, DateTime.UtcNow.AddHours(8), LoginService.Hash(hash!));
            await new LoginSessionRepository(db, new UnexpectedReporter(), new SqlExecutor(db), new UnitOfWork(db))
                .CreateAsync(session, LoginService.Hash(Guid.NewGuid().ToString()), default);
            return new(id, session.Id);
        }
        async Task<AccountMessage> Message(long id, string purpose)
        {
            var payload = await connection.QuerySingleAsync<string>("""
                SELECT m.ProtectedPayload FROM UserAccountMessages m JOIN UserAccountChallenges c ON c.TokenHash=m.TokenHash
                WHERE c.UserId=@id AND c.Purpose=@purpose ORDER BY c.CreatedAtUtc DESC LIMIT 1
                """, new { id, purpose });
            return JsonSerializer.Deserialize<AccountMessage>(protector.Unprotect(payload))!;
        }
        ISqlExecutor FailingSql(string fragment)
        {
            var actual = new SqlExecutor(db); var proxy = DispatchProxy.Create<ISqlExecutor, Stub>();
            ((Stub)(object)proxy).Call = (method, args) =>
            {
                if (((string)args[0]!).Contains(fragment)) throw new InvalidOperationException("Injected transactional failure");
                return method.Invoke(actual, args);
            };
            return proxy;
        }
        try
        {
            var user = await users.RegisterAsync(input, default); ownedIds.Add(user.Id);
            input.EmailId = $"other-account-{Guid.NewGuid():N}@example.com";
            var other = await users.RegisterAsync(input, default); ownedIds.Add(other.Id);
            var actor = await Session(user.Id); var foreign = await Session(other.Id);
            var service = Accounts(db);
            await service.UpdateProfileAsync(actor, new() { FirstName = "Updated", Address1 = "Road", Country = "India", State = "Telangana", City = "Hyderabad", PinNumber = "500001" }, default);
            var profile = await service.ProfileAsync(actor, default);
            Assert.Equal("Updated", profile.FirstName); Assert.Equal(user.RoleId, profile.RoleId); Assert.Equal(user.EmailId, profile.EmailId);
            await service.RevokeSessionAsync(actor, foreign.SessionId, default);
            Assert.Null(await connection.ExecuteScalarAsync<DateTime?>("SELECT RevokedAtUtc FROM UserLoginSessions WHERE Id=@id", new { id = foreign.SessionId.ToString("D") }));

            // Failed queue insertion rolls back challenge creation/invalidation as well.
            await Assert.ThrowsAsync<InvalidOperationException>(() => Accounts(db, FailingSql("INSERT INTO UserAccountMessages"))
                .RequestVerificationAsync(actor, new() { Channel = "email" }, default));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM UserAccountChallenges WHERE UserId=@id", new { id = user.Id }));
            await service.RequestVerificationAsync(actor, new() { Channel = "email" }, default);
            var verification = await Message(user.Id, "email");
            await service.ConfirmVerificationAsync(actor, new() { Token = verification.Token }, default);
            Assert.True((await service.StatusAsync(actor, default)).EmailVerified);
            await Assert.ThrowsAsync<ApiException>(() => service.ConfirmVerificationAsync(actor, new() { Token = verification.Token }, default));

            // A unique-contact conflict must not consume its challenge or change the user's email.
            await connection.ExecuteAsync("UPDATE UserAccountChallenges SET CreatedAtUtc=DATE_SUB(CreatedAtUtc, INTERVAL 2 MINUTE) WHERE UserId=@id", new { id = user.Id });
            await service.RequestVerificationAsync(actor, new() { Channel = "email", Destination = other.EmailId }, default);
            var conflict = await Message(user.Id, "email");
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.ConfirmVerificationAsync(actor, new() { Token = conflict.Token }, default))).StatusCode);
            Assert.Null(await connection.ExecuteScalarAsync<DateTime?>("SELECT ConsumedAtUtc FROM UserAccountChallenges WHERE TokenHash=@hash", new { hash = LoginService.Hash(conflict.Token) }));
            Assert.Equal(user.EmailId, (await users.GetAsync(user.Id, default)).EmailId);

            await service.RequestVerificationAsync(actor, new() { Channel = "phone", Destination = "+14155552671" }, default);
            var phone = await Message(user.Id, "phone");
            await service.ConfirmVerificationAsync(actor, new() { Token = phone.Token }, default);
            await Assert.ThrowsAsync<ApiException>(() => service.ProfileAsync(actor, default));
            actor = await Session(user.Id);
            Assert.True((await service.StatusAsync(actor, default)).PhoneVerified);

            var oldHash = await connection.ExecuteScalarAsync<string>("SELECT PasswordHash FROM Users WHERE Id=@id", new { id = user.Id });
            await Assert.ThrowsAsync<InvalidOperationException>(() => Accounts(db, FailingSql("UPDATE UserLoginSessions"))
                .ChangePasswordAsync(actor, new() { CurrentPassword = input.Password!, NewPassword = "Changed-account-password-123!" }, default));
            Assert.Equal(oldHash, await connection.ExecuteScalarAsync<string>("SELECT PasswordHash FROM Users WHERE Id=@id", new { id = user.Id }));
            Assert.Single(await service.SessionsAsync(actor, default));

            await service.ForgotPasswordAsync(new() { Email = user.EmailId }, default);
            await service.ForgotPasswordAsync(new() { Email = user.EmailId }, default);
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM UserAccountChallenges WHERE UserId=@id AND Purpose='reset'", new { id = user.Id }));
            var reset = await Message(user.Id, "reset");
            async Task<bool> Reset()
            {
                await using var concurrent = Context();
                try { await Accounts(concurrent).ResetPasswordAsync(new() { Token = reset.Token, NewPassword = "Changed-account-password-123!" }, default); return true; }
                catch (ApiException e) when (e.StatusCode == 400) { return false; }
            }
            var results = await Task.WhenAll(Reset(), Reset());
            Assert.Single(results, value => value); Assert.Single(results, value => !value);
            await Assert.ThrowsAsync<ApiException>(() => service.ProfileAsync(actor, default));
            var newHash = await connection.ExecuteScalarAsync<string>("SELECT PasswordHash FROM Users WHERE Id=@id", new { id = user.Id });
            Assert.True(PasswordHashing.Verify(newHash, "Changed-account-password-123!"));

            actor = await Session(user.Id);
            await service.RequestDeletionAsync(actor, default);
            var deletion = await Message(user.Id, "delete");
            await service.DeleteAsync(actor, new() { Token = deletion.Token }, default);
            Assert.False((await users.GetAsync(user.Id, default)).IsActive);
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM UserLoginSessions WHERE UserId=@id AND RevokedAtUtc IS NULL", new { id = user.Id }));
            await Assert.ThrowsAsync<ApiException>(() => service.ProfileAsync(actor, default));
        }
        finally
        {
            foreach (var id in ownedIds)
            {
                await connection.ExecuteAsync("DELETE FROM UserAccountChallenges WHERE UserId=@id", new { id });
                await connection.ExecuteAsync("DELETE FROM UserVerifiedContacts WHERE UserId=@id", new { id });
                await connection.ExecuteAsync("DELETE FROM UserLoginSessions WHERE UserId=@id", new { id });
                await connection.ExecuteAsync("DELETE FROM Users WHERE Id=@id", new { id });
            }
        }
    }
}
