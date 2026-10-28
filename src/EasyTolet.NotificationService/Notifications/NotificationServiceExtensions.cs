using EasyTolet.Contracts.Notifications;
using EasyTolet.Shared.Notifications;
using MassTransit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EasyTolet.NotificationService.Notifications;

public static class NotificationServiceExtensions
{
    public static IServiceCollection AddNotificationDelivery(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        var protection = services.AddDataProtection().SetApplicationName("EasyTolet.NotificationService");
        var directory = configuration["Notifications:DataProtectionKeyDirectory"];
        if (!string.IsNullOrWhiteSpace(directory)) protection.PersistKeysToFileSystem(new DirectoryInfo(directory));
        services.AddScoped<INotificationInbox, NotificationInbox>();
        services.AddScoped<IEmailTransport, SmtpEmailTransport>();
        services.AddScoped<INotificationSender, NotificationSender>();
        services.AddHttpClient<HttpNotificationTransport>(client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<NotificationDeliveryDispatcher>();
        services.AddHostedService<NotificationDeliveryWorker>();
        services.AddMassTransit(bus =>
        {
            bus.AddConsumer<NotificationRequestedConsumer>();
            bus.UsingRabbitMq((context, rabbit) =>
            {
                rabbit.ConfigureNotificationRabbitMq(configuration);
                rabbit.ReceiveEndpoint(NotificationQueues.Delivery, endpoint =>
                {
                    endpoint.Durable = true;
                    endpoint.AutoDelete = false;
                    endpoint.PrefetchCount = 16;
                    endpoint.UseMessageRetry(retry =>
                    {
                        retry.Ignore<ArgumentException>();
                        retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
                    });
                    endpoint.ConfigureConsumer<NotificationRequestedConsumer>(context);
                });
            });
        });
        return services;
    }
}
