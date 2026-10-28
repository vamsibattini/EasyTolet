using Dapper;
using System.Data;
using EasyTolet.UserService.Authentication.Tokens;
using EasyTolet.Shared.Database;
using EasyTolet.Shared.Exceptions;
using EasyTolet.Shared.Infrastructure;
using EasyTolet.Shared.Interfaces;
using EasyTolet.UserService.Repositories;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using System.Reflection;
using Xunit;
using static EasyTolet.UserService.Tests.Repositories.UserBusinessRepositoryTests;

namespace EasyTolet.UserService.Tests.Integration;

public sealed class UserMySqlFactAttribute : FactAttribute
{
    public UserMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYTOLET_USER_TEST_MYSQL")) ||
            !int.TryParse(Environment.GetEnvironmentVariable("EASYTOLET_USER_TEST_ROLE_ID"), out var role) || role <= 0)
            Skip = "Configure EASYTOLET_USER_TEST_MYSQL (isolated schema ending _tests) and EASYTOLET_USER_TEST_ROLE_ID; install the supplied routines and tables first.";
    }
}

public sealed class UserMySqlTests
{
    [UserMySqlFact]
    public async Task SuppliedProcedureSupportsCrudAuditConflictsAndRollback()
    {
        var connectionString = Environment.GetEnvironmentVariable("EASYTOLET_USER_TEST_MYSQL")!;
        Assert.EndsWith("_tests", new MySqlConnectionStringBuilder(connectionString).Database);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0))).Options);
        var sql = new SqlExecutor(db);
        var repository = new UserBusinessRepository(db, new UnexpectedReporter(), sql, new UnitOfWork(db));
        var request = Input();
        request.EmailId = $"user-test-{Guid.NewGuid():N}@example.com";
        request.RoleId = int.Parse(Environment.GetEnvironmentVariable("EASYTOLET_USER_TEST_ROLE_ID")!);
        var originalEmail = request.EmailId;
        var rollbackEmail = $"rollback-{Guid.NewGuid():N}@example.com";
        try
        {
            var created = await repository.RegisterAsync(request, default);
            Assert.True(created.Id > 0);
            Assert.False(string.IsNullOrWhiteSpace(created.UserId));
            Assert.Equal(created.Id, created.CreatedBy);
            Assert.Equal(request.RoleId, created.RoleId);
            Assert.Equal(request.Address1, created.Address1);
            Assert.Equal(created.Id, Assert.Single(await repository.ListAsync(created.UserId, default)).Id);
            var storedHash = await connection.ExecuteScalarAsync<string>("SELECT PasswordHash FROM Users WHERE Id=@Id", new { created.Id });
            Assert.NotEqual(request.Password, storedHash);
            Assert.True(PasswordHashing.Verify(storedHash, request.Password!));
            Assert.False(PasswordHashing.Verify(storedHash, "Incorrect-password-123!"));

            // Inspect the actual result-set columns, not a DTO that could silently ignore leaked hashes.
            var readParameters = new DynamicParameters();
            foreach (var name in new[] { "p_Action", "p_Id", "p_UserId", "p_RoleId", "p_FirstName", "p_LastName",
                "p_Address1", "p_Address2", "p_Address3", "p_Country", "p_State", "p_City", "p_PinNumber",
                "p_MobileNumber", "p_AlternateMobileNumber", "p_EmailId", "p_SSOProvider", "p_SSOProviderUserId",
                "p_SSOEmailId", "p_SSOProfileData", "p_IsActive", "p_ActionBy", "p_PasswordHash" })
                readParameters.Add(name, null);
            readParameters.Add("p_Action", "R"); readParameters.Add("p_Id", created.Id);
            var result = (IDictionary<string, object>)Assert.Single(await connection.QueryAsync("SP_User", readParameters, commandType: CommandType.StoredProcedure));
            Assert.DoesNotContain(result.Keys, key => key.Contains("password", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => repository.RegisterAsync(request, default))).StatusCode);
            request.Password = null; // Ordinary profile changes must not clear or rehash credentials.
            request.FirstName = "Updated";
            request.Address2 = "Second address";
            var updated = await repository.UpdateAsync(created.Id, request, created.Id, default);
            Assert.Equal("Updated", updated.FirstName);
            Assert.Equal("Second address", updated.Address2);
            Assert.Equal(created.Id, updated.UpdatedBy);
            Assert.Equal(storedHash, await connection.ExecuteScalarAsync<string>("SELECT PasswordHash FROM Users WHERE Id=@Id", new { created.Id }));
            request.Password = "Replacement-test-password-456!";
            await repository.UpdateAsync(created.Id, request, created.Id, default);
            var replacementHash = await connection.ExecuteScalarAsync<string>("SELECT PasswordHash FROM Users WHERE Id=@Id", new { created.Id });
            Assert.True(PasswordHashing.Verify(replacementHash, request.Password));
            Assert.False(PasswordHashing.Verify(replacementHash, "Test-only-password-123!"));

            request.Address2 = null;
            Assert.Null((await repository.UpdateAsync(created.Id, request, created.Id, default)).Address2);
            await repository.DeleteAsync(created.Id, created.Id, default);
            Assert.False((await repository.GetAsync(created.Id, default)).IsActive);
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Id = @Id", new { created.Id }));

            // Force a failure after INSERT but before commit: the inserted user must disappear.
            var failingSql = DispatchProxy.Create<ISqlExecutor, Stub>();
            ((Stub)(object)failingSql).Call = (method, args) =>
            {
                var action = args[1]!.GetType().GetProperty("p_Action")!.GetValue(args[1]);
                if (Equals(action, "R")) throw new InvalidOperationException("Post-insert read failed");
                return method.Invoke(sql, args);
            };
            var sequenceBeforeFailure = await connection.ExecuteScalarAsync<long>(
                "SELECT LastNumber FROM UserIdSequence WHERE SequenceId = 1");
            request.EmailId = rollbackEmail;
            var failingRepository = new UserBusinessRepository(db, new UnexpectedReporter(), failingSql, new UnitOfWork(db));
            await Assert.ThrowsAsync<InvalidOperationException>(() => failingRepository.RegisterAsync(request, default));
            Assert.Equal(sequenceBeforeFailure, await connection.ExecuteScalarAsync<long>(
                "SELECT LastNumber FROM UserIdSequence WHERE SequenceId = 1"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE EmailId = @email", new { email = rollbackEmail }));
        }
        finally
        {
            // Only remove the two unique test identities, never unrelated rows or schemas.
            await connection.ExecuteAsync("DELETE FROM Users WHERE EmailId IN (@email, @rollbackEmail)",
                new { email = originalEmail, rollbackEmail });
        }
    }
}
