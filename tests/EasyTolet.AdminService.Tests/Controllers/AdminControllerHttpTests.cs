using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using EasyTolet.AdminService.Contracts;
using EasyTolet.AdminService.Controllers;
using EasyTolet.AdminService.Repositories;
using EasyTolet.Contracts.Admin;
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

namespace EasyTolet.AdminService.Tests.Controllers;

public sealed class AdminControllerHttpTests : IAsyncLifetime
{
    private const string Key = "Test-only-key-with-at-least-32-bytes-12345";
    private IHost _host = null!;
    private HttpClient _client = null!;
    private readonly FakeRoles _roles = new();
    public async Task InitializeAsync()
    {
        _host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(AdminController).Assembly);
            services.AddScoped(_ => new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().Options));
            services.AddSingleton<IRoleBusinessRepository>(_roles);
            services.AddGlobalExceptionHandling(new ConfigurationBuilder().Build());
            services.AddSingleton<IExceptionReporter, NoopReporter>();
            services.AddAuthentication("Bearer").AddJwtBearer("Bearer", options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = "test-issuer",
                    ValidateAudience = true, ValidAudience = "admin-api",
                    ValidateLifetime = true, ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key))
                };
            });
            services.AddAdminAuthorization();
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/admin/roles")).StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid.jwt.token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/admin/roles")).StatusCode);
        Assert.Equal(0, _roles.Calls);
    }

    [Fact]
    public async Task MissingPermissionIsForbidden()
    {
        Authenticate(permission: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/admin/roles")).StatusCode);
        Assert.Equal(0, _roles.Calls);
    }

    [Fact]
    public async Task MissingNumericAuditClaimBlocksWrites()
    {
        Authenticate(actor: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/admin/roles", Input())).StatusCode);
        Assert.Equal(0, _roles.Calls);
    }

    [Fact]
    public async Task CreateReturnsLocationAndUsesSignedActorInsteadOfRequestAuditValues()
    {
        Authenticate();
        var response = await _client.PostAsJsonAsync("/api/admin/roles", new { roleCode = "PO", roleName = "Owner", createdBy = 999 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith("/api/admin/roles/1", response.Headers.Location!.ToString());
        Assert.Equal(7, _roles.Actor);
        var role = await response.Content.ReadFromJsonAsync<RoleSnapshot>();
        Assert.Equal(1, role!.RoleId);
    }

    [Fact]
    public async Task ReadUpdateDeleteRoutesWorkWithAdminPermission()
    {
        Authenticate();
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/admin/roles?page=1&pageSize=20")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/admin/roles/1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/admin/roles/1", Input())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/admin/roles/1")).StatusCode);
    }

    [Fact]
    public async Task InvalidBodyReturns400BeforeRepositoryAccess()
    {
        Authenticate();
        var response = await _client.PostAsJsonAsync("/api/admin/roles", new { roleCode = "TOOLONGCODE1", roleName = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, _roles.Calls);
    }

    private void Authenticate(bool permission = true, bool actor = true)
    {
        var claims = new List<Claim> { new("sub", "UETID_00000007") };
        if (permission) claims.Add(new Claim("permission", "admin.roles.manage"));
        if (actor) claims.Add(new Claim("user_id", "7"));
        var token = new JwtSecurityToken("test-issuer", "admin-api", claims, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
    private static SaveRoleRequest Input() => new() { RoleCode = "PO", RoleName = "Owner" };
    private sealed class NoopReporter : IExceptionReporter { public Task ReportAsync(ExceptionLogEntry entry) => Task.CompletedTask; }
    private sealed class FakeRoles : IRoleBusinessRepository
    {
        public int Calls { get; private set; }
        public int Actor { get; private set; }
        private static RoleSnapshot Role => new(1, "PO", "Owner", null, null, true, DateTime.UtcNow, null, 7, null);
        public Task<RoleSnapshot> GetAsync(int id, CancellationToken ct) { Calls++; return Task.FromResult(Role); }
        public Task<PagedResult<RoleSnapshot>> ListAsync(int page, int pageSize, CancellationToken ct)
        { Calls++; return Task.FromResult(new PagedResult<RoleSnapshot>([Role], page, pageSize, 1)); }
        public Task<RoleSnapshot> CreateAsync(SaveRoleRequest request, int actorId, CancellationToken ct)
        { Calls++; Actor = actorId; return Task.FromResult(Role); }
        public Task<RoleSnapshot> UpdateAsync(int id, SaveRoleRequest request, int actorId, CancellationToken ct) => CreateAsync(request, actorId, ct);
        public Task DeleteAsync(int id, int actorId, CancellationToken ct) { Calls++; return Task.CompletedTask; }
    }
}
