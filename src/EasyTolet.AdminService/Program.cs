using EasyTolet.Shared.Hosting;
using EasyTolet.Shared.Security;
using EasyTolet.Shared.OpenApi;
using EasyTolet.Shared.Database;
using EasyTolet.Shared.Messaging;
using EasyTolet.Shared.Exceptions;
using EasyTolet.AdminService;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTrustedGatewayHeaders(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddServiceApiDocumentation("EasyTolet Admin API");

builder.Services.AddMySqlDatabase<AppDbContext>(builder.Configuration, builder.Environment);

builder.Services.Configure<RabbitMqSettings>(builder.Configuration.GetSection("RabbitMq"));

builder.Services.AddGlobalExceptionHandling(builder.Configuration);
builder.Services.AddAdminApplication();
builder.Services.AddAdminMessaging(builder.Configuration);

builder.Services.AddServiceSecurity(builder.Configuration);

builder.Services.AddAdminAuthorization();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseGlobalExceptionHandling();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseServiceSecurity();
app.MapControllers();
app.Run();
