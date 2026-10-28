using EasyTolet.Shared.Exceptions;
using EasyTolet.Shared.Interfaces;
using EasyTolet.UserService.Authentication.Tokens;

namespace EasyTolet.UserService.Repositories;

public sealed class LoginUserRepository(AppDbContext database, IExceptionReporter exceptionReporter, ISqlExecutor sql) : ILoginUserRepository
{
    private readonly AppDbContext _database = database;
    private readonly IExceptionReporter _exceptionReporter = exceptionReporter;
    public async Task<LoginUser?> FindByEmailAsync(string email, CancellationToken ct) =>
        Map((await sql.QueryAsync<CredentialRow>("SELECT Id, UserId, PasswordHash, IsActive FROM Users WHERE EmailId = @email", new { email }, ct)).SingleOrDefault());
    public async Task<LoginUser?> GetAsync(long id, CancellationToken ct) =>
        Map((await sql.QueryAsync<CredentialRow>("SELECT Id, UserId, PasswordHash, IsActive FROM Users WHERE Id = @id", new { id }, ct)).SingleOrDefault());
    private static LoginUser? Map(CredentialRow? row) => row is null ? null : new(row.Id, row.UserId, row.PasswordHash, row.IsActive);
    public sealed class CredentialRow
    {
        public long Id { get; set; }
        public string UserId { get; set; } = "";
        public string? PasswordHash { get; set; }
        public bool IsActive { get; set; }
    }
}
