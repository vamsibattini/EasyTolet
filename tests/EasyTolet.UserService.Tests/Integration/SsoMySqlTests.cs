using Dapper;
using EasyTolet.Shared.Database;
using EasyTolet.Shared.Exceptions;
using EasyTolet.Shared.Infrastructure;
using EasyTolet.UserService.Authentication;
using EasyTolet.UserService.Repositories;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Xunit;
using static EasyTolet.UserService.Tests.Repositories.UserBusinessRepositoryTests;

namespace EasyTolet.UserService.Tests.Integration;

public sealed class SsoMySqlTests
{
    [UserMySqlFact]
    public async Task SsoIdentityPersistsAndUniqueConstraintPreventsDuplicateAccountsWithoutEmailLinking()
    {
        var connectionString = Environment.GetEnvironmentVariable("EASYTOLET_USER_TEST_MYSQL")!;
        Assert.EndsWith("_tests", new MySqlConnectionStringBuilder(connectionString).Database);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0))).Options);
        var repository = new UserBusinessRepository(db, new UnexpectedReporter(), new SqlExecutor(db), new UnitOfWork(db));
        var suffix = Guid.NewGuid().ToString("N");
        var email1 = $"sso1-{suffix}@example.com";
        var email2 = $"sso2-{suffix}@example.com";
        var identity = new SsoIdentity("Google", "Subject" + suffix, "provider@example.com", "Provider Name");
        var request = Input();
        request.RoleId = int.Parse(Environment.GetEnvironmentVariable("EASYTOLET_USER_TEST_ROLE_ID")!);
        request.EmailId = email1;
        try
        {
            var created = await repository.RegisterSsoAsync(request, identity, default);
            Assert.Equal(created.Id, (await repository.FindSsoAsync(identity, default))!.Id);
            Assert.Equal(identity.Email, await connection.ExecuteScalarAsync<string>(
                "SELECT SSOEmailId FROM Users WHERE Id = @id", new { id = created.Id }));
            var storedProfile = await connection.ExecuteScalarAsync<string>(
                "SELECT CAST(SSOProfileData AS CHAR) FROM Users WHERE Id = @id", new { id = created.Id });
            using var profile = System.Text.Json.JsonDocument.Parse(storedProfile!);
            Assert.Equal(identity.Provider, profile.RootElement.GetProperty("provider").GetString());
            Assert.Equal(identity.DisplayName, profile.RootElement.GetProperty("displayName").GetString());
            Assert.Equal(2, profile.RootElement.EnumerateObject().Count());
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => repository.RegisterSsoAsync(request, identity, default))).StatusCode);
            // Same email, different verified identity must not silently link to the existing account.
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => repository.RegisterSsoAsync(request,
                identity with { Subject = "different" + suffix }, default))).StatusCode);
            Assert.Null(await repository.FindSsoAsync(identity with { Subject = "different" + suffix }, default));
            // Provider subjects are case-sensitive. Verify the schema migration, not just application comparison.
            request.EmailId = email2;
            var second = await repository.RegisterSsoAsync(request, identity with { Subject = "subject" + suffix }, default);
            Assert.NotEqual(created.Id, second.Id);
            var duplicate = await Assert.ThrowsAsync<MySqlException>(() => connection.ExecuteAsync(
                "UPDATE Users SET SSOProviderUserId = @subject WHERE Id = @id", new { subject = identity.Subject, id = second.Id }));
            Assert.Equal(1062, duplicate.Number);
            await repository.DeleteAsync(created.Id, created.Id, default);
            Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => repository.FindSsoAsync(identity, default))).StatusCode);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM Users WHERE EmailId IN (@email1, @email2)", new { email1, email2 });
        }
    }
}
