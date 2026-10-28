using System.Reflection;
using EasyTolet.Shared.Exceptions;
using EasyTolet.Shared.Interfaces;
using EasyTolet.UserService.Contracts;
using EasyTolet.UserService.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EasyTolet.UserService.Tests.Repositories;

public sealed class UserBusinessRepositoryTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().Options);
    private readonly List<object> _parameters = [];
    private bool _inTransaction, _committed, _rolledBack;
    private long _affected = 1;
    private bool _missing;
    private Exception? _failure;
    private Exception? _readFailure;
    private Exception? _commitFailure;
    private int _role = 2;
    public void Dispose() => _db.Dispose();

    private UserBusinessRepository Repository()
    {
        var sql = DispatchProxy.Create<ISqlExecutor, Stub>();
        ((Stub)(object)sql).Call = (method, args) =>
        {
            Assert.Equal("QueryProcedureAsync", method.Name);
            Assert.Equal("SP_User", args[0]);
            var parameters = args[1]!;
            _parameters.Add(parameters);
            var action = Field<string>(parameters, "p_Action");
            if (action != "R") Assert.True(_inTransaction);
            if (action == "R" && _parameters.Count > 1) Assert.True(_inTransaction);
            if (action == "R" && _readFailure is not null) throw _readFailure;
            if (_failure is not null) throw _failure;
            if (method.GetGenericArguments()[0] == typeof(UserMutationResult))
                return Task.FromResult<IReadOnlyList<UserMutationResult>>([new() { RowsAffected = _affected }]);
            return Task.FromResult<IReadOnlyList<UserResponse>>(_missing ? [] : [new() { Id = 42, UserId = "U42", RoleId = _role, CreatedBy = 42 }]);
        };
        var uow = DispatchProxy.Create<IUnitOfWork, Stub>();
        ((Stub)(object)uow).Call = (method, args) =>
        {
            Assert.Equal("ExecuteInTransactionAsync", method.Name);
            return Transaction((Func<CancellationToken, Task>)args[0]!, (CancellationToken)args[1]!);
        };
        return new(_db, new UnexpectedReporter(), sql, uow);
    }
    private async Task Transaction(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        _inTransaction = true;
        try
        {
            await operation(ct);
            if (_commitFailure is not null) throw _commitFailure;
            _committed = true;
        }
        catch { _rolledBack = true; throw; }
        finally { _inTransaction = false; }
    }

    [Fact]
    public async Task RegistrationUsesSelectedRoleAllParametersAndReadsFullProfileBeforeCommit()
    {
        var input = Input(); input.RoleId = 999;
        var user = await Repository().RegisterAsync(input, default);
        Assert.Equal(42, user.CreatedBy);
        Assert.True(_committed);
        Assert.Equal(2, _parameters.Count);
        var insert = _parameters[0];
        Assert.Equal(23, insert.GetType().GetProperties().Length);
        Assert.Equal("I", Field<string>(insert, "p_Action"));
        Assert.Equal(999, Field<int>(insert, "p_RoleId"));
        Assert.Null(Field<object>(insert, "p_ActionBy"));
        Assert.Null(Field<object>(insert, "p_SSOProviderUserId"));
        Assert.Equal("Test", Field<string>(insert, "p_FirstName"));
        var hash = Field<string>(insert, "p_PasswordHash");
        Assert.NotEqual(input.Password, hash);
        Assert.True(EasyTolet.UserService.Authentication.Tokens.PasswordHashing.Verify(hash, input.Password!));
        Assert.Equal(42L, Field<long>(_parameters[1], "p_Id"));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("email")]
    [InlineData("length")]
    [InlineData("role")]
    public async Task InvalidInputNeverStartsDatabaseWork(string invalid)
    {
        var input = Input();
        if (invalid == "name") input.FirstName = " ";
        if (invalid == "email") input.EmailId = "invalid";
        if (invalid == "length") input.Address1 = new string('a', 501);
        if (invalid == "role") input.RoleId = 0;
        var error = await Assert.ThrowsAsync<ApiException>(() => Repository().UpdateAsync(42, input, 7, default));
        Assert.Equal(400, error.StatusCode); Assert.Empty(_parameters); Assert.False(_committed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("            ")]
    public async Task PasswordRegistrationRejectsMissingOrInvalidPasswordBeforeTransaction(string? password)
    {
        var input = Input(); input.Password = password;
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => Repository().RegisterAsync(input, default))).StatusCode);
        Assert.Empty(_parameters); Assert.False(_committed);
    }

    [Fact]
    public async Task UpdateUsesSignedActorAndReadsChangedProfile()
    {
        await Repository().UpdateAsync(42, Input(), 9000000000, default);
        Assert.Equal("U", Field<string>(_parameters[0], "p_Action"));
        Assert.Equal(9000000000, Field<long>(_parameters[0], "p_ActionBy"));
        Assert.Equal("R", Field<string>(_parameters[1], "p_Action"));
        Assert.True(_committed);
    }

    [Fact]
    public async Task ProfileUpdateWithoutPasswordPassesNullHashToPreserveExistingCredentials()
    {
        var input = Input(); input.Password = null;
        await Repository().UpdateAsync(42, input, 7, default);
        Assert.Null(Field<object>(_parameters[0], "p_PasswordHash"));
        Assert.True(_committed);
    }

    [Fact]
    public async Task DeleteUsesProcedureSoftDeleteAction()
    {
        await Repository().DeleteAsync(42, 7, default);
        Assert.Equal("D", Field<string>(Assert.Single(_parameters), "p_Action"));
        Assert.True(_committed);
    }

    [Fact]
    public async Task NoAffectedRowReturnsNotFoundAndRollsBack()
    {
        _affected = 0;
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => Repository().DeleteAsync(42, 7, default))).StatusCode);
        Assert.True(_rolledBack); Assert.False(_committed);
    }

    [Fact]
    public async Task MissingUserReadReturnsNotFound()
    {
        _missing = true;
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => Repository().GetAsync(42, default))).StatusCode);
    }

    [Fact]
    public async Task ProcedureFailureRollsBackAndPropagatesWithoutDoubleReporting()
    {
        _failure = new InvalidOperationException("failure");
        Assert.Same(_failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Repository().RegisterAsync(Input(), default)));
        Assert.True(_rolledBack); Assert.False(_committed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFailureAfterSuccessfulWriteDoesNotCommit(bool update)
    {
        _readFailure = new InvalidOperationException("Read failed after mutation");
        var repository = Repository();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => update
            ? repository.UpdateAsync(42, Input(), 7, default)
            : repository.RegisterAsync(Input(), default));
        Assert.Same(_readFailure, error);
        Assert.Equal(2, _parameters.Count);
        Assert.True(_rolledBack);
        Assert.False(_committed);
    }

    [Fact]
    public async Task CommitFailureDoesNotReturnSuccessfulRegistration()
    {
        _commitFailure = new InvalidOperationException("Commit failed");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Repository().RegisterAsync(Input(), default));
        Assert.Same(_commitFailure, error);
        Assert.True(_rolledBack);
        Assert.False(_committed);
    }

    [Fact]
    public async Task CancellationDuringPostInsertReadPropagatesWithoutCommitting()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        _readFailure = new OperationCanceledException(cancellation.Token);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => Repository().RegisterAsync(Input(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(_rolledBack);
        Assert.False(_committed);
    }

    [Theory]
    [InlineData(0, 7, 400)]
    [InlineData(42, 0, 403)]
    public async Task InvalidIdentifiersNeverReachProcedure(long id, long actor, int status)
    {
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(() => Repository().DeleteAsync(id, actor, default))).StatusCode);
        Assert.Empty(_parameters);
    }

    [Fact]
    public async Task ReadByPublicIdPassesExactFilter()
    {
        await Repository().ListAsync("U42", default);
        Assert.Equal("U42", Field<string>(Assert.Single(_parameters), "p_UserId"));
    }

    public static SaveUserRequest Input() => new()
    {
        Password = "Test-only-password-123!", FirstName = " Test ", Address1 = "Road", Country = "India", State = "Telangana", City = "Hyderabad",
        PinNumber = "500001", MobileNumber = "1234567890", EmailId = "test@example.com", RoleId = 2
    };
    private static T? Field<T>(object value, string name) => (T?)value.GetType().GetProperty(name)!.GetValue(value);
    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args!);
    }
    public sealed class UnexpectedReporter : IExceptionReporter
    {
        public Task ReportAsync(ExceptionLogEntry entry) => throw new InvalidOperationException("Repository must not double-log.");
    }
}
