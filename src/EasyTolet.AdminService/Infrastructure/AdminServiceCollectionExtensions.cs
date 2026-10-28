using EasyTolet.AdminService.Messaging;
using EasyTolet.AdminService.Repositories;
using MassTransit;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EasyTolet.AdminService;

public static class AdminServiceCollectionExtensions
{
    public static IServiceCollection AddAdminApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IRoleBusinessRepository, RoleBusinessRepository>();
        services.AddScoped<AdminOutboxDispatcher>();
        return services;
    }

    public static IServiceCollection AddAdminMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        var address = configuration["RabbitMq:Address"] ?? "rabbitmq://localhost/";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("rabbitmq" or "rabbitmqs"))
            throw new InvalidOperationException("RabbitMq:Address must be a rabbitmq:// or rabbitmqs:// URI.");
        var username = configuration["RabbitMq:Username"];
        var password = configuration["RabbitMq:Password"];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("RabbitMQ credentials are required.");
        services.AddMassTransit(bus => bus.UsingRabbitMq((context, rabbit) =>
            rabbit.Host(uri, host => { host.Username(username); host.Password(password); })));
        services.AddScoped<IRoleEventPublisher, RabbitMqRoleEventPublisher>();
        services.AddHostedService<AdminOutboxWorker>();
        return services;
    }

    public static IServiceCollection AddAdminAuthorization(this IServiceCollection services) =>
        services.AddAuthorization(options => options.AddPolicy("AdminRoles", policy =>
            policy.RequireAuthenticatedUser().RequireClaim("permission", "admin.roles.manage")));
}
