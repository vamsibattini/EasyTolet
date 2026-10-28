using System.Reflection;
using System.Text.Json;
using EasyTolet.Shared.Exceptions;
using EasyTolet.Shared.Interfaces;
using EasyTolet.UserService.Authentication;
using EasyTolet.UserService.Contracts;
using EasyTolet.UserService.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static EasyTolet.UserService.Tests.Repositories.UserBusinessRepositoryTests;

namespace EasyTolet.UserService.Tests.Repositories;

public sealed class SsoUserRepositoryTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().Options);
    private int _matches;
    private bool _active = true, _transaction, _committed, _rolledBack, _failRead;
    private int _queries;
    private object? _insert;
    private object? _lookup;
    private static SsoIdentity Identity => new("Google", "ExactSubject", "provider@example.com", "Provider Name");
    public void Dispose() => _db.Dispose();

    private UserBusinessRepository Repository()
    {
        var sql = DispatchProxy.Create<ISqlExecutor, Stub>();
        ((Stub)(object)sql).Call = (method, args) =>
        {
            _queries++;
            if (method.Name == "QueryAsync")
            {
                Assert.Equal("SELECT Id FROM Users WHERE SSOProvider = @Provider AND SSOProviderUserId = @Subject LIMIT 2", args[0]);
                _lookup = args[1];
                return Task.FromResult<IReadOnlyList<SsoUserKey>>(Enumerable.Range(1, _matches).Select(i => new SsoUserKey { Id = i }).ToArray());
            }
            Assert.Equal("QueryProcedureAsync", method.Name);
            Assert.Equal("SP_User", args[0]);
            if (Field<string>(args[1]!, "p_Action") == "I")
            {
                Assert.True(_transaction);
                _insert = args[1];
            }
            else if (_failRead) throw new InvalidOperationException("Read failed after insertion");
            return Task.FromResult<IReadOnlyList<UserResponse>>([new() { Id = 42, UserId = "U42", IsActive = _active }]);
        };
        var unitOfWork = DispatchProxy.Create<IUnitOfWork, Stub>();
        ((Stub)(object)unitOfWork).Call = (method, args) =>
        {
            Assert.Equal("ExecuteInTransactionAsync", method.Name);
            return Transaction((Func<CancellationToken, Task>)args[0]!, (CancellationToken)args[1]!);
        };
        return new UserBusinessRepository(_db, new UnexpectedReporter(), sql, unitOfWork);
    }
    private async Task Transaction(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        _transaction = true;
        try { await operation(ct); _committed = true; }
        catch { _rolledBack = true; throw; }
        finally { _transaction = false; }
    }

    [Fact]
    public async Task RegistrationPersistsVerifiedIdentityWithSelectedRoleInsideTransaction()
    {
        var request = Input(); request.RoleId = 4;
        request.EmailId = "contact@example.com";
        await Repository().RegisterSsoAsync(request, Identity, default);
        Assert.True(_committed);
        Assert.NotNull(_insert);
        Assert.Equal("Google", Field<string>(_insert, "p_SSOProvider"));
        Assert.Equal("ExactSubject", Field<string>(_insert, "p_SSOProviderUserId"));
        Assert.Equal("provider@example.com", Field<string>(_insert, "p_SSOEmailId"));
        Assert.Equal("contact@example.com", Field<string>(_insert, "p_EmailId"));
        Assert.Equal(4, Field<int>(_insert, "p_RoleId"));
        using var profile = JsonDocument.Parse(Field<string>(_insert, "p_SSOProfileData")!);
        Assert.Equal(2, profile.RootElement.EnumerateObject().Count());
        Assert.Equal("Provider Name", profile.RootElement.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task LookupUsesExactProviderSubjectAndNeverEmail()
    {
        _matches = 1;
        Assert.NotNull(await Repository().FindSsoAsync(Identity, default));
        Assert.Equal("Google", Field<string>(_lookup!, "Provider"));
        Assert.Equal("ExactSubject", Field<string>(_lookup!, "Subject"));
        Assert.Equal(2, _lookup!.GetType().GetProperties().Length);
        Assert.Null(_insert);
    }

    [Fact]
    public async Task UnknownIdentityRequiresRegistrationWithoutCreatingRows()
    {
        Assert.Null(await Repository().FindSsoAsync(Identity, default));
        Assert.Null(_insert);
    }

    [Fact]
    public async Task ExistingIdentityCannotCreateAnotherUser()
    {
        _matches = 1;
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => Repository().RegisterSsoAsync(Input(), Identity, default))).StatusCode);
        Assert.Null(_insert); Assert.True(_rolledBack); Assert.False(_committed);
    }

    [Fact]
    public async Task InactiveUserCannotSignIn()
    {
        _matches = 1; _active = false;
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => Repository().FindSsoAsync(Identity, default))).StatusCode);
    }

    [Fact]
    public async Task AmbiguousLegacyIdentitiesFailClosed()
    {
        _matches = 2;
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => Repository().FindSsoAsync(Identity, default))).StatusCode);
        Assert.Equal(1, _queries);
    }

    [Fact]
    public async Task PostInsertReadFailureRollsBackSsoRegistration()
    {
        _failRead = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repository().RegisterSsoAsync(Input(), Identity, default));
        Assert.NotNull(_insert); Assert.True(_rolledBack); Assert.False(_committed);
    }

    [Theory]
    [InlineData("Instagram", "123")]
    [InlineData("Unknown", "123")]
    [InlineData("Google", " ")]
    [InlineData("Google", "subject with spaces")]
    public async Task UnsupportedOrMalformedIdentityNeverReachesDatabase(string provider, string subject)
    {
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => Repository().RegisterSsoAsync(Input(), new(provider, subject, null, null), default))).StatusCode);
        Assert.Equal(0, _queries);
    }

    private static T? Field<T>(object value, string property) => (T?)value.GetType().GetProperty(property)!.GetValue(value);
}
