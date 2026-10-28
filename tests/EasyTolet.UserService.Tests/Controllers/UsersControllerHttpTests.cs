using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using EasyTolet.UserService.Contracts;
using EasyTolet.UserService.Controllers;
using EasyTolet.UserService.Repositories;
using EasyTolet.Shared.Exceptions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EasyTolet.UserService.Tests.Controllers;

public sealed class UsersControllerHttpTests : IAsyncLifetime
{
    private const string Key = "Test-only-key-with-at-least-32-bytes-12345";
    private IHost _host = null!;
    private HttpClient _client = null!;
    private readonly FakeUsers _users = new();
    public async Task InitializeAsync()
    {
        _host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(UsersController).Assembly);
            services.AddScoped(_ => new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().Options));
            services.AddSingleton<IUserBusinessRepository>(_users);
            services.AddGlobalExceptionHandling(new ConfigurationBuilder().Build());
            services.AddSingleton<IExceptionReporter>(_reporter);
            services.AddAuthentication("Bearer").AddJwtBearer("Bearer", options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = "test-issuer",
                    ValidateAudience = true, ValidAudience = "user-api",
                    ValidateLifetime = true, ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key))
                };
            });
            services.AddUserAuthorization();
        }).Configure(app =>
        {
            app.UseGlobalExceptionHandling(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        })).StartAsync();
        _client = _host.GetTestClient();
    }
    public async Task DisposeAsync() { _client.Dispose(); await _host.StopAsync(); _host.Dispose(); }

    [Fact]
    public async Task AnonymousAndInvalidJwtAreRejectedBeforeRepositoryAccess()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/users")).StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid.jwt.token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/users")).StatusCode);
        Assert.Equal(0, _users.Calls);
    }

    [Fact]
    public async Task MissingPermissionIsForbidden()
    {
        Authenticate(permission: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/users")).StatusCode);
        Assert.Equal(0, _users.Calls);
    }

    [Fact]
    public async Task MissingNumericAuditClaimBlocksWrites()
    {
        Authenticate(actor: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PutAsJsonAsync("/api/users/1", Input())).StatusCode);
        Assert.Equal(0, _users.Calls);
    }

    [Fact]
    public async Task AnonymousRegistrationReturnsCreatedAndLocation()
    {
        var response = await _client.PostAsJsonAsync("/api/users/register", Input());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith("/api/users/1", response.Headers.Location!.ToString());
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<UserResponse>())!.Id);
    }

    [Fact]
    public async Task ReadUpdateDeleteRoutesWorkWithManagementPermission()
    {
        Authenticate();
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/users/1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/users/1", Input())).StatusCode);
        Assert.Equal(7, _users.Actor);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/users/1")).StatusCode);
    }

    [Fact]
    public async Task UserCanReadOwnProfileButCannotReadOthersOrMutateRoles()
    {
        Authenticate(permission: false);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/users/7")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/users/8")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PutAsJsonAsync("/api/users/7", Input())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.DeleteAsync("/api/users/7")).StatusCode);
        Assert.Equal(1, _users.Calls);
    }

    [Fact]
    public async Task InvalidBodyReturns400BeforeRepositoryAccess()
    {
        var response = await _client.PostAsJsonAsync("/api/users/register", new { firstName = "", emailId = "invalid" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _users.Calls);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(500)]
    public async Task ExceptionsAreReportedOnceAndTranslatedByMiddleware(int status)
    {
        Authenticate();
        _users.Error = status == 500 ? new InvalidOperationException("private failure") : new ApiException("Expected failure", status);
        var response = await _client.GetAsync("/api/users/1");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(1, _reporter.Calls);
        Assert.DoesNotContain("private failure", await response.Content.ReadAsStringAsync());
    }

    private void Authenticate(bool permission = true, bool actor = true)
    {
        var claims = new List<Claim> { new("sub", "UETID_00000007") };
        if (permission) claims.Add(new Claim("permission", "users.manage"));
        if (actor) claims.Add(new Claim("user_id", "7"));
        var token = new JwtSecurityToken("test-issuer", "user-api", claims, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
    private static SaveUserRequest Input() => new()
    {
        Password = "Test-only-password-123!", FirstName = "Test", Address1 = "Road", Country = "India", State = "Telangana", City = "Hyderabad",
        PinNumber = "500001", MobileNumber = "1234567890", EmailId = "test@example.com", RoleId = 2
    };
    private readonly NoopReporter _reporter = new();
    private sealed class NoopReporter : IExceptionReporter
    {
        public int Calls;
        public Task ReportAsync(ExceptionLogEntry entry) { Calls++; return Task.CompletedTask; }
    }
    private sealed class FakeUsers : IUserBusinessRepository
    {
        public int Calls { get; private set; }
        public long Actor { get; private set; }
        public Exception? Error;
        private static UserResponse User => new() { Id = 1, UserId = "UETID_00000001", RoleId = 2 };
        public Task<UserResponse> GetAsync(long id, CancellationToken ct)
        { Calls++; if (Error is not null) throw Error; return Task.FromResult(User); }
        public Task<IReadOnlyList<UserResponse>> ListAsync(string? userId, CancellationToken ct)
        { Calls++; return Task.FromResult<IReadOnlyList<UserResponse>>([User]); }
        public Task<UserResponse> RegisterAsync(RegisterUserRequest request, CancellationToken ct)
        { Calls++; return Task.FromResult(User); }
        public Task<UserResponse> UpdateAsync(long id, SaveUserRequest request, long actorId, CancellationToken ct)
        { Calls++; Actor = actorId; return Task.FromResult(User); }
        public Task DeleteAsync(long id, long actorId, CancellationToken ct) { Calls++; Actor = actorId; return Task.CompletedTask; }
    }
}
