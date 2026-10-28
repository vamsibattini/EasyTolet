using System.Reflection;
using System.Text.Json;
using EasyTolet.Shared.Exceptions;
using EasyTolet.Shared.Interfaces;
using EasyTolet.UserService.Accounts;
using EasyTolet.UserService.Authentication.Tokens;
using EasyTolet.UserService.Contracts;
using EasyTolet.UserService.Repositories;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using static EasyTolet.UserService.Tests.Repositories.UserBusinessRepositoryTests;

namespace EasyTolet.UserService.Tests.Accounts;

public sealed class AccountHarness : IDisposable
{
    public AppDbContext Db { get; } = new(new DbContextOptionsBuilder<AppDbContext>().Options);
    public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
    public AccountActor Actor { get; } = new(7, Guid.NewGuid());
    public AccountService.AccountRow? User = new() { Id = 7, IsActive = true, EmailId = "user@example.com", MobileNumber = "+919876543210", PasswordHash = PasswordHashing.Hash("Original-password-123!") };
    public bool ValidSession = true, RecentChallenge, Committed, RolledBack, InTransaction;
    public Exception? CommitFailure;
    public string? FailWriteContaining;
    public AccountService.ChallengeRow? Challenge;
    public IReadOnlyList<AccountService.ContactRow> Contacts = [];
    public IReadOnlyList<AccountSession> Sessions = [];
    public List<(string Sql, object Parameters)> Writes { get; } = [];
    public List<(string Sql, object Parameters)> Reads { get; } = [];
    public ISqlExecutor Sql { get; }
    public IUnitOfWork Uow { get; }
    public AccountService Service { get; }
    public IConfiguration Configuration { get; } = new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["AccountFeatures:PhoneVerificationEnabled"] = "true" }).Build();
    public AccountHarness()
    {
        Sql = DispatchProxy.Create<ISqlExecutor, Stub>();
        ((Stub)(object)Sql).Call = (method, args) =>
        {
            Assert.True(InTransaction);
            var query = (string)args[0]!;
            var parameters = args[1]!;
            if (method.Name == "ExecuteAsync")
            {
                Writes.Add((query, parameters));
                if (FailWriteContaining is not null && query.Contains(FailWriteContaining)) throw new InvalidOperationException("write failure");
                return Task.FromResult(1);
            }
            Reads.Add((query, parameters));
            var type = method.GetGenericArguments()[0];
            if (type == typeof(AccountService.AccountRow)) return Rows(User is null ? Array.Empty<AccountService.AccountRow>() : [User]);
            if (type == typeof(AccountService.SessionKey)) return Rows<AccountService.SessionKey>((query.Contains("UserLoginSessions") ? ValidSession : RecentChallenge) ? [new AccountService.SessionKey { Id = Actor.SessionId.ToString("D") }] : []);
            if (type == typeof(AccountService.ChallengeRow)) return Rows(Challenge is null ? Array.Empty<AccountService.ChallengeRow>() : [Challenge]);
            if (type == typeof(AccountService.ContactRow)) return Rows(Contacts);
            if (type == typeof(AccountSession)) return Rows(Sessions);
            throw new InvalidOperationException(query);
        };
        Uow = DispatchProxy.Create<IUnitOfWork, Stub>();
        ((Stub)(object)Uow).Call = (_, args) => Transaction((Func<CancellationToken, Task>)args[0]!, (CancellationToken)args[1]!);
        var users = DispatchProxy.Create<IUserBusinessRepository, Stub>();
        ((Stub)(object)users).Call = (_, _) => Task.FromResult(new UserResponse { Id = Actor.UserId });
        Service = new(Db, new UnexpectedReporter(), Sql, Uow, users, Protection, TimeProvider.System, Configuration);
    }
    private static Task<IReadOnlyList<T>> Rows<T>(IReadOnlyList<T> values) => Task.FromResult(values);
    private async Task Transaction(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        InTransaction = true;
        try { await operation(ct); if (CommitFailure is not null) throw CommitFailure; Committed = true; }
        catch { RolledBack = true; throw; }
        finally { InTransaction = false; }
    }
    public AccountService.ChallengeRow ValidChallenge(string purpose = "reset") => new()
    {
        UserId = Actor.UserId, Purpose = purpose, Destination = User!.EmailId, OriginalDestination = User.EmailId,
        CredentialStamp = LoginService.Hash(User.PasswordHash ?? "sso-only"), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15)
    };
    public static T Field<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
    public void Dispose() => Db.Dispose();
}

public sealed class AccountServiceTests
{
    [Fact]
    public async Task DisabledPhoneVerificationDoesNotCreateChallengeOrQueueMessage()
    {
        using var h = new AccountHarness(); h.Configuration["AccountFeatures:PhoneVerificationEnabled"] = "false";
        var error = await Assert.ThrowsAsync<EasyTolet.Shared.Exceptions.ApiException>(() =>
            h.Service.RequestVerificationAsync(h.Actor, new() { Channel = "phone", Destination = "+14155552671" }, default));
        Assert.Equal(503, error.StatusCode); Assert.Empty(h.Writes); Assert.Empty(h.Reads);
    }

    private const string Secret = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ";
    private static ProfileRequest Profile() => new() { FirstName = " New ", Address1 = "Road", Country = "India", State = "Telangana", City = "Hyderabad", PinNumber = "500001" };

    [Fact]
    public async Task ProfileUpdatesOnlySafeFieldsAndUsesAuthenticatedActorForAudit()
    {
        using var h = new AccountHarness();
        await h.Service.UpdateProfileAsync(h.Actor, Profile(), default);
        var write = Assert.Single(h.Writes);
        Assert.DoesNotContain("RoleId", write.Sql); Assert.DoesNotContain("PasswordHash", write.Sql);
        Assert.DoesNotContain("EmailId", write.Sql); Assert.DoesNotContain("IsActive", write.Sql);
        Assert.Equal(7, AccountHarness.Field<long>(write.Parameters, "id"));
        Assert.Equal("New", AccountHarness.Field<string>(write.Parameters, "FirstName")); Assert.True(h.Committed);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("missing")]
    [InlineData("revoked")]
    [InlineData("actor")]
    public async Task InvalidAccountOrSessionCannotMutate(string kind)
    {
        using var h = new AccountHarness();
        if (kind == "inactive") h.User!.IsActive = false;
        if (kind == "missing") h.User = null;
        if (kind == "revoked") h.ValidSession = false;
        var actor = kind == "actor" ? h.Actor with { UserId = 0 } : h.Actor;
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => h.Service.UpdateProfileAsync(actor, Profile(), default))).StatusCode);
        Assert.Empty(h.Writes); Assert.True(h.RolledBack);
    }

    [Fact]
    public async Task InvalidFieldsAreRejectedBeforeTransaction()
    {
        using var h = new AccountHarness();
        var input = Profile(); input.FirstName = " ";
        await Assert.ThrowsAsync<ApiException>(() => h.Service.UpdateProfileAsync(h.Actor, input, default));
        await Assert.ThrowsAsync<ApiException>(() => h.Service.ChangePasswordAsync(h.Actor, new() { CurrentPassword = "x", NewPassword = "short" }, default));
        await Assert.ThrowsAsync<ApiException>(() => h.Service.ForgotPasswordAsync(new() { Email = "invalid" }, default));
        Assert.Empty(h.Reads); Assert.Empty(h.Writes);
    }

    [Fact]
    public async Task PasswordChangeVerifiesOldPasswordHashesNewPasswordAndRevokesSessionsAtomically()
    {
        using var h = new AccountHarness();
        await h.Service.ChangePasswordAsync(h.Actor, new() { CurrentPassword = "Original-password-123!", NewPassword = "Replacement-password-123!" }, default);
        var write = Assert.Single(h.Writes, w => w.Sql.Contains("SET PasswordHash"));
        Assert.True(PasswordHashing.Verify(AccountHarness.Field<string>(write.Parameters, "hash"), "Replacement-password-123!"));
        Assert.Contains(h.Writes, w => w.Sql.Contains("UPDATE UserLoginSessions"));
        Assert.Contains(h.Writes, w => w.Sql.Contains("UPDATE UserAccountChallenges")); Assert.True(h.Committed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongPasswordAndSsoOnlyAccountsCannotChangePassword(bool sso)
    {
        using var h = new AccountHarness(); if (sso) h.User!.PasswordHash = null;
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => h.Service.ChangePasswordAsync(h.Actor,
            new() { CurrentPassword = "wrong", NewPassword = "Replacement-password-123!" }, default))).StatusCode);
        Assert.Empty(h.Writes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("inactive")]
    [InlineData("sso")]
    [InlineData("throttled")]
    public async Task RecoverySilentlyIgnoresIneligibleOrThrottledAccounts(string kind)
    {
        using var h = new AccountHarness();
        if (kind == "missing") h.User = null;
        if (kind == "inactive") h.User!.IsActive = false;
        if (kind == "sso") h.User!.PasswordHash = null;
        if (kind == "throttled") h.RecentChallenge = true;
        await h.Service.ForgotPasswordAsync(new() { Email = "user@example.com" }, default);
        Assert.Empty(h.Writes); Assert.True(h.Committed);
    }

    [Fact]
    public async Task RecoveryStoresHashAndProtectedDeliveryInSameTransaction()
    {
        using var h = new AccountHarness();
        await h.Service.ForgotPasswordAsync(new() { Email = "user@example.com" }, default);
        var challenge = Assert.Single(h.Writes, w => w.Sql.Contains("INSERT INTO UserAccountChallenges"));
        var outbox = Assert.Single(h.Writes, w => w.Sql.Contains("INSERT INTO UserAccountMessages"));
        var payload = AccountHarness.Field<string>(outbox.Parameters, "payload");
        var message = JsonSerializer.Deserialize<AccountMessage>(h.Protection.CreateProtector("EasyTolet.AccountDelivery.v1").Unprotect(payload))!;
        Assert.Equal(43, message.Token.Length); Assert.Equal("reset", message.Purpose); Assert.Equal("email", message.Channel);
        Assert.Equal(LoginService.Hash(message.Token), AccountHarness.Field<string>(challenge.Parameters, "hash"));
        Assert.DoesNotContain(message.Token, payload); Assert.True(h.Committed);
    }

    [Theory]
    [InlineData("UserAccountMessages")]
    [InlineData("UserLoginSessions")]
    [InlineData("commit")]
    public async Task DeliveryOrRevocationOrCommitFailureDoesNotReportSuccess(string failure)
    {
        using var h = new AccountHarness();
        if (failure == "commit") h.CommitFailure = new InvalidOperationException("commit failure");
        else h.FailWriteContaining = failure;
        await Assert.ThrowsAsync<InvalidOperationException>(() => failure == "UserLoginSessions"
            ? h.Service.ChangePasswordAsync(h.Actor, new() { CurrentPassword = "Original-password-123!", NewPassword = "Replacement-password-123!" }, default)
            : h.Service.ForgotPasswordAsync(new() { Email = "user@example.com" }, default));
        Assert.True(h.RolledBack); Assert.False(h.Committed);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("consumed")]
    [InlineData("purpose")]
    [InlineData("credential")]
    [InlineData("contact")]
    [InlineData("owner")]
    [InlineData("missing")]
    public async Task ResetRejectsInvalidChallengeWithoutChangingPassword(string kind)
    {
        using var h = new AccountHarness(); h.Challenge = h.ValidChallenge();
        if (kind == "expired") h.Challenge.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        if (kind == "consumed") h.Challenge.ConsumedAtUtc = DateTime.UtcNow;
        if (kind == "purpose") h.Challenge.Purpose = "email";
        if (kind == "credential") h.Challenge.CredentialStamp = "old";
        if (kind == "contact") h.Challenge.OriginalDestination = "old@example.com";
        if (kind == "owner") h.Challenge.UserId = 8;
        if (kind == "missing") h.Challenge = null;
        await Assert.ThrowsAsync<ApiException>(() => h.Service.ResetPasswordAsync(new() { Token = Secret, NewPassword = "Replacement-password-123!" }, default));
        Assert.Empty(h.Writes); Assert.False(h.Committed);
    }

    [Fact]
    public async Task SuccessfulResetConsumesTokenChangesPasswordAndRevokesEverySession()
    {
        using var h = new AccountHarness(); h.Challenge = h.ValidChallenge();
        await h.Service.ResetPasswordAsync(new() { Token = Secret, NewPassword = "Replacement-password-123!" }, default);
        Assert.Equal(4, h.Writes.Count); Assert.True(h.Committed);
        Assert.Contains(h.Writes, w => w.Sql.Contains("SET PasswordHash"));
        Assert.Contains(h.Writes, w => w.Sql.Contains("UPDATE UserLoginSessions"));
    }

    [Theory]
    [InlineData("email", "new@example.com")]
    [InlineData("phone", "+14155552671")]
    public async Task VerificationQueuesNewDestinationWithoutChangingContactBeforeConfirmation(string channel, string destination)
    {
        using var h = new AccountHarness();
        await h.Service.RequestVerificationAsync(h.Actor, new() { Channel = channel, Destination = destination }, default);
        Assert.DoesNotContain(h.Writes, w => w.Sql.Contains("UPDATE Users"));
        var write = Assert.Single(h.Writes, w => w.Sql.Contains("INSERT INTO UserAccountChallenges"));
        Assert.Equal(destination, AccountHarness.Field<string>(write.Parameters, "destination"));
    }

    [Theory]
    [InlineData("email", "invalid")]
    [InlineData("phone", "12345")]
    [InlineData("other", "user@example.com")]
    public async Task InvalidVerificationDestinationIsRejected(string channel, string destination)
    {
        using var h = new AccountHarness();
        await Assert.ThrowsAsync<ApiException>(() => h.Service.RequestVerificationAsync(h.Actor, new() { Channel = channel, Destination = destination }, default));
        Assert.Empty(h.Writes);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("phone")]
    public async Task ConfirmingContactUpdatesOnlyProvenContactAndRevokesSessionsOnChange(string channel)
    {
        using var h = new AccountHarness(); h.Challenge = h.ValidChallenge(channel);
        h.Challenge.OriginalDestination = channel == "phone" ? h.User!.MobileNumber : h.User!.EmailId;
        h.Challenge.Destination = channel == "phone" ? "+14155552671" : "new@example.com";
        await h.Service.ConfirmVerificationAsync(h.Actor, new() { Token = Secret }, default);
        var write = Assert.Single(h.Writes, w => w.Sql.Contains("UPDATE Users"));
        Assert.Contains(channel == "phone" ? "MobileNumber" : "EmailId", write.Sql);
        Assert.Contains(h.Writes, w => w.Sql.Contains("INSERT INTO UserVerifiedContacts"));
        Assert.Contains(h.Writes, w => w.Sql.Contains("UPDATE UserLoginSessions")); Assert.True(h.Committed);
    }

    [Fact]
    public async Task VerificationStatusDoesNotTrustPreviouslyVerifiedDifferentContact()
    {
        using var h = new AccountHarness();
        h.Contacts = [new() { Channel = "email", Destination = "old@example.com" }, new() { Channel = "phone", Destination = h.User!.MobileNumber }];
        Assert.Equal(new AccountStatus(false, true), await h.Service.StatusAsync(h.Actor, default));
    }

    [Fact]
    public async Task DeletionRequiresCorrectPurposeThenDeactivatesAndRevokesAtomically()
    {
        using var h = new AccountHarness(); h.Challenge = h.ValidChallenge("reset");
        await Assert.ThrowsAsync<ApiException>(() => h.Service.DeleteAsync(h.Actor, new() { Token = Secret }, default));
        Assert.Empty(h.Writes);
        h.Challenge.Purpose = "delete";
        await h.Service.DeleteAsync(h.Actor, new() { Token = Secret }, default);
        Assert.Contains(h.Writes, w => w.Sql.Contains("IsActive=FALSE"));
        Assert.Contains(h.Writes, w => w.Sql.Contains("UPDATE UserLoginSessions")); Assert.True(h.Committed);
    }

    [Fact]
    public async Task SessionListMarksCurrentSessionAndRevokeAlwaysScopesToOwner()
    {
        using var h = new AccountHarness();
        h.Sessions = [new() { Id = h.Actor.SessionId.ToString("D") }, new() { Id = Guid.NewGuid().ToString("D") }];
        var result = await h.Service.SessionsAsync(h.Actor, default);
        Assert.True(result[0].IsCurrent); Assert.False(result[1].IsCurrent);
        await h.Service.RevokeSessionAsync(h.Actor, Guid.NewGuid(), default);
        var write = Assert.Single(h.Writes);
        Assert.Contains("WHERE UserId=@id AND Id=@session", write.Sql);
        Assert.Equal(h.Actor.UserId, AccountHarness.Field<long>(write.Parameters, "id"));
    }
}
