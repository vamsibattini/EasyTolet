using EasyTolet.Shared.Hosting;
using EasyTolet.Shared.Security;
using EasyTolet.Shared.Notifications;
using EasyTolet.Shared.Database;
using EasyTolet.Shared.Messaging;
using EasyTolet.Shared.Exceptions;
using EasyTolet.UserService;
using EasyTolet.UserService.Authentication;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTrustedGatewayHeaders(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddUserApiDocumentation();

builder.Services.AddMySqlDatabase<AppDbContext>(builder.Configuration, builder.Environment);

builder.Services.Configure<RabbitMqSettings>(builder.Configuration.GetSection("RabbitMq"));

builder.Services.AddGlobalExceptionHandling(builder.Configuration);
builder.Services.AddUserApplication();
builder.Services.AddNotificationProducerMessaging(builder.Configuration);
// Persist this directory on a private shared volume when running multiple replicas.
var keyDirectory = builder.Configuration["AccountOutbox:DataProtectionKeyDirectory"];
if (!string.IsNullOrWhiteSpace(keyDirectory))
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));

builder.Services.AddServiceSecurity(builder.Configuration);

builder.Services.AddUserAuthorization();
builder.Services.AddUserSso(builder.Configuration);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseGlobalExceptionHandling();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseServiceSecurity();
app.UseRateLimiter();
app.MapControllers();
app.Run();
