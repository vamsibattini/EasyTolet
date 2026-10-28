using EasyTolet.Shared.Hosting;
using EasyTolet.Shared.Security;
using EasyTolet.Shared.Database;
using EasyTolet.Shared.Messaging;
using EasyTolet.Shared.Exceptions;
using EasyTolet.BookingService;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTrustedGatewayHeaders(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddMySqlDatabase<AppDbContext>(builder.Configuration, builder.Environment);

builder.Services.Configure<RabbitMqSettings>(builder.Configuration.GetSection("RabbitMq"));

builder.Services.AddGlobalExceptionHandling(builder.Configuration);

builder.Services.AddServiceSecurity(builder.Configuration);

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseGlobalExceptionHandling();
app.UseServiceSecurity();
app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();
app.Run();
