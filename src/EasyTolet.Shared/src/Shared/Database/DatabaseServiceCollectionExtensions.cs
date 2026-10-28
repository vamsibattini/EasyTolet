using EasyTolet.Shared.Infrastructure;
using EasyTolet.Shared.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EasyTolet.Shared.Database;

public static class DatabaseServiceCollectionExtensions
{
    /// <summary>Register once per service host. All data access shares its scoped DbContext.</summary>
    public static IServiceCollection AddMySqlDatabase<TContext>(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment,
        string sectionName = "Database", string connectionStringName = "DefaultConnection")
        where TContext : DbContext
    {
        if (services.Any(d => d.ServiceType == typeof(DbContext)))
            throw new InvalidOperationException("Only one service database may be registered in this host.");
        var settings = configuration.GetSection(sectionName).Get<MySqlDatabaseOptions>()
            ?? throw new InvalidOperationException($"Missing database settings section '{sectionName}'.");
        var connectionString = MySqlConnectionStringHelper.Build(settings, environment.IsDevelopment(),
            configuration.GetConnectionString(connectionStringName));
        if (!Version.TryParse(settings.ServerVersion, out var version) || version.Major < 8)
            throw new InvalidOperationException("Database:ServerVersion must specify your MySQL server version (8.0 or later).");

        services.AddDbContext<TContext>(options => options
            .UseMySql(connectionString, new MySqlServerVersion(version),
                mysql => mysql.CommandTimeout(settings.CommandTimeoutSeconds))
            .EnableSensitiveDataLogging(false)
            .EnableDetailedErrors(false));
        services.AddScoped<DbContext>(provider => provider.GetRequiredService<TContext>());
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ISqlExecutor, SqlExecutor>();
        return services;
    }
}
