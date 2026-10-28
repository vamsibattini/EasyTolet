using EasyTolet.Contracts.Notifications;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EasyTolet.Shared.Notifications;

public interface INotificationPublisher
{
    // Call from an outbox dispatcher after its business transaction commits, never from a controller.
    Task PublishAsync(NotificationRequestedV1 notification, CancellationToken ct);
}

public sealed class RabbitMqNotificationPublisher(ISendEndpointProvider endpoints, TimeProvider clock) : INotificationPublisher
{
    public async Task PublishAsync(NotificationRequestedV1 notification, CancellationToken ct)
    {
        var lifetime = notification.ExpiresAtUtc - clock.GetUtcNow().UtcDateTime;
        if (lifetime <= TimeSpan.Zero) return;
        // A durable command queue does not depend on NotificationService having started a subscription first.
        var endpoint = await endpoints.GetSendEndpoint(new Uri($"queue:{NotificationQueues.Delivery}"));
        await endpoint.Send(notification, context =>
        {
            context.MessageId = notification.NotificationId;
            context.TimeToLive = lifetime;
        }, ct);
    }
}

public static class NotificationMessagingExtensions
{
    /// <summary>For hosts without an existing MassTransit bus. Hosts with one call AddNotificationPublisher and configure that bus.</summary>
    public static IServiceCollection AddNotificationProducerMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddNotificationPublisher();
        services.AddMassTransit(bus => bus.UsingRabbitMq((context, rabbit) => rabbit.ConfigureNotificationRabbitMq(configuration)));
        return services;
    }

    public static IServiceCollection AddNotificationPublisher(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<INotificationPublisher, RabbitMqNotificationPublisher>();
        return services;
    }

    public static void ConfigureNotificationRabbitMq(this IRabbitMqBusFactoryConfigurator rabbit, IConfiguration configuration)
    {
        var address = configuration["RabbitMq:Address"];
        if (string.IsNullOrWhiteSpace(address)) address = $"rabbitmq://{configuration["RabbitMq:Host"] ?? "localhost"}/";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("rabbitmq" or "rabbitmqs") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("RabbitMq:Address must be a rabbitmq:// or rabbitmqs:// URI without embedded credentials.");
        var username = configuration["RabbitMq:Username"];
        var password = configuration["RabbitMq:Password"];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("RabbitMQ credentials are required.");
        rabbit.Host(uri, host => { host.Username(username); host.Password(password); });
    }
}
