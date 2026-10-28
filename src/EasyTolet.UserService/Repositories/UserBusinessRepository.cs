using System.ComponentModel.DataAnnotations;
using EasyTolet.Shared.Exceptions;
using EasyTolet.Shared.Interfaces;
using EasyTolet.UserService.Contracts;
using MySqlConnector;
using EasyTolet.UserService.Authentication;
using EasyTolet.UserService.Authentication.Tokens;

namespace EasyTolet.UserService.Repositories;

public sealed class UserBusinessRepository(AppDbContext database, IExceptionReporter exceptionReporter,
    ISqlExecutor sql, IUnitOfWork unitOfWork) : IUserBusinessRepository, ISsoUserRepository
{
    // Same scoped context as the shared executor/unit of work. Only middleware reports unhandled errors.
    private readonly AppDbContext _database = database;
    private readonly IExceptionReporter _exceptionReporter = exceptionReporter;

    public Task<UserResponse> RegisterAsync(RegisterUserRequest request, CancellationToken ct) =>
        RegisterCoreAsync(request, null, ct);

    public Task<UserResponse> RegisterSsoAsync(RegisterUserRequest request, SsoIdentity identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        return RegisterCoreAsync(request, identity, ct);
    }

    public async Task<UserResponse?> FindSsoAsync(SsoIdentity identity, CancellationToken ct)
    {
        identity.Validate();
        // The migration makes the provider/subject pair unique with case-sensitive comparison.
        // Do not search or link by email: email ownership and provider identity are different concepts.
        var matches = await sql.QueryAsync<SsoUserKey>(
            "SELECT Id FROM Users WHERE SSOProvider = @Provider AND SSOProviderUserId = @Subject LIMIT 2",
            new { identity.Provider, identity.Subject }, ct);
        if (matches.Count > 1) throw new ApiException("SSO identity is ambiguous; contact support.", 409);
        if (matches.Count == 0) return null;
        var user = await GetAsync(matches[0].Id, ct);
        if (!user.IsActive) throw new ApiException("This user is inactive.", 403);
        return user;
    }

    private async Task<UserResponse> RegisterCoreAsync(RegisterUserRequest request, SsoIdentity? identity, CancellationToken ct)
    {
        Validate(request);
        if (identity is null && string.IsNullOrWhiteSpace(request.Password))
            throw new ApiException("A password is required for email registration.", 400);
        return await WriteAsync(async token =>
        {
            if (identity is not null && await FindSsoAsync(identity, token) is not null)
                throw new ApiException("This SSO identity is already registered. Sign in again.", 409);
            var rows = await sql.QueryProcedureAsync<UserResponse>("SP_User",
                Parameters("I", request: request, roleId: request.RoleId, active: true, identity: identity), token);
            var inserted = rows.SingleOrDefault() ?? throw new InvalidOperationException("SP_User returned no inserted user.");
            // Insert returns a reduced projection. Read the complete profile and audit values before commit.
            return await GetAsync(inserted.Id, token);
        }, ct);
    }

    public Task<IReadOnlyList<UserResponse>> ListAsync(string? userId, CancellationToken ct)
    {
        if (userId is not null && (string.IsNullOrWhiteSpace(userId) || userId.Length > 20))
            throw new ApiException("Invalid user ID.", 400);
        return sql.QueryProcedureAsync<UserResponse>("SP_User", Parameters("R", userId: userId), ct);
    }

    public async Task<UserResponse> GetAsync(long id, CancellationToken ct)
    {
        ValidateId(id);
        var rows = await sql.QueryProcedureAsync<UserResponse>("SP_User", Parameters("R", id: id), ct);
        return rows.SingleOrDefault() ?? throw new ApiException("User not found.", 404);
    }

    public async Task<UserResponse> UpdateAsync(long id, SaveUserRequest request, long actorId, CancellationToken ct)
    {
        ValidateId(id); ValidateActor(actorId); Validate(request);
        return await WriteAsync(async token =>
        {
            await MutateAsync(Parameters("U", id, request: request, roleId: request.RoleId,
                active: request.IsActive, actorId: actorId), token);
            return await GetAsync(id, token);
        }, ct);
    }

    public async Task DeleteAsync(long id, long actorId, CancellationToken ct)
    {
        ValidateId(id); ValidateActor(actorId);
        await WriteAsync(async token =>
        {
            await MutateAsync(Parameters("D", id, actorId: actorId), token);
            return true;
        }, ct);
    }

    private async Task MutateAsync(object parameters, CancellationToken ct)
    {
        var result = await sql.QueryProcedureAsync<UserMutationResult>("SP_User", parameters, ct);
        var affected = result.SingleOrDefault() ?? throw new InvalidOperationException("SP_User returned no mutation result.");
        if (affected.RowsAffected == 0) throw new ApiException("User not found.", 404);
        if (affected.RowsAffected != 1) throw new InvalidOperationException("SP_User changed an unexpected number of users.");
    }

    private async Task<T> WriteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        T result = default!;
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async token => result = await operation(token), ct);
            return result;
        }
        catch (MySqlException error) when (error.Number is 1062 or 1451 or 1452 or 1213 or 1205)
        {
            throw new ApiException("The user change conflicts with existing data or another operation.", 409, error);
        }
    }

    private static void Validate(RegisterUserRequest request)
    {
        if (request is null || !Validator.TryValidateObject(request, new ValidationContext(request), [], true) ||
            (request.Password is not null && string.IsNullOrWhiteSpace(request.Password)))
            throw new ApiException("Invalid user fields.", 400);
    }
    private static void ValidateId(long id)
    {
        if (id <= 0) throw new ApiException("A positive user ID is required.", 400);
    }
    private static void ValidateActor(long actorId)
    {
        if (actorId <= 0) throw new ApiException("A numeric user_id claim is required.", 403);
    }

    // All 23 parameters are supplied for every action. SSO identity is managed by a trusted identity flow.
    private static object Parameters(string action, long? id = null, string? userId = null,
        RegisterUserRequest? request = null, int? roleId = null, bool? active = null, long? actorId = null, SsoIdentity? identity = null) => new
    {
        p_Action = action, p_Id = id, p_UserId = userId, p_RoleId = roleId,
        p_FirstName = request?.FirstName.Trim(), p_LastName = request?.LastName?.Trim(),
        p_Address1 = request?.Address1.Trim(), p_Address2 = request?.Address2?.Trim(), p_Address3 = request?.Address3?.Trim(),
        p_Country = request?.Country.Trim(), p_State = request?.State.Trim(), p_City = request?.City.Trim(),
        p_PinNumber = request?.PinNumber.Trim(), p_MobileNumber = request?.MobileNumber.Trim(),
        p_AlternateMobileNumber = request?.AlternateMobileNumber?.Trim(), p_EmailId = request?.EmailId.Trim(),
        p_SSOProvider = identity?.Provider, p_SSOProviderUserId = identity?.Subject,
        p_SSOEmailId = identity?.Email, p_SSOProfileData = identity?.ProfileJson(),
        p_IsActive = active, p_ActionBy = actorId,
        p_PasswordHash = request?.Password is { Length: > 0 } password ? PasswordHashing.Hash(password) : null
    };
}

public sealed class UserMutationResult
{
    public long RowsAffected { get; set; }
}

public sealed class SsoUserKey
{
    public long Id { get; set; }
}
