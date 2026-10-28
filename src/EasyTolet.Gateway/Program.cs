using EasyTolet.Shared.Hosting;
using EasyTolet.Gateway.Routing;
using EasyTolet.Shared.Security;
using EasyTolet.Gateway;
using EasyTolet.Shared.Database;
using EasyTolet.Shared.Messaging;
using EasyTolet.Shared.Exceptions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTrustedGatewayHeaders(builder.Configuration);
builder.Services.AddGatewayRouting(builder.Configuration, builder.Environment.IsDevelopment());

builder.Services.AddControllers();

// The gateway context owns only its operational logging database.
builder.Services.AddMySqlDatabase<AppDbContext>(builder.Configuration, builder.Environment,
    "ExceptionLogging:Database", "ExceptionLogging");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<RabbitMqSettings>(builder.Configuration.GetSection("RabbitMq"));

builder.Services.AddGlobalExceptionHandling(builder.Configuration);

builder.Services.AddServiceSecurity(builder.Configuration);

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseGlobalExceptionHandling();
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/docs/admin/swagger.json", "Admin API");
        options.SwaggerEndpoint("/docs/user/swagger.json", "User API");
    });
}
app.UseServiceSecurity();
app.MapControllers();
app.MapReverseProxy();
app.Run();
