using EasyTolet.UserService.Authentication.Tokens;
using EasyTolet.UserService.Accounts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using EasyTolet.UserService.Repositories;

namespace EasyTolet.UserService;

public static class UserServiceCollectionExtensions
{
    public static IServiceCollection AddUserApplication(this IServiceCollection services)
    {
        services.AddScoped<UserBusinessRepository>();
        services.AddScoped<IUserBusinessRepository>(provider => provider.GetRequiredService<UserBusinessRepository>());
        services.AddScoped<ISsoUserRepository>(provider => provider.GetRequiredService<UserBusinessRepository>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ILoginService, LoginService>();
        services.AddScoped<ILoginUserRepository, LoginUserRepository>();
        services.AddScoped<ILoginSessionRepository, LoginSessionRepository>();
        services.AddScoped<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddDataProtection().SetApplicationName("EasyTolet.UserService");
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<AccountMessageDispatcher>();
        services.AddScoped<IAccountMessageSender, BrokerAccountMessageSender>();
        services.AddHostedService<AccountDeliveryWorker>();
        return services;
    }

    public static IServiceCollection AddUserAuthorization(this IServiceCollection services) =>
        services.AddAuthorization(options => options.AddPolicy("ManageUsers", policy =>
            policy.RequireAuthenticatedUser().RequireClaim("permission", "users.manage")));
}
