using Flattiverse.Gateway.Application;
using Flattiverse.Gateway.Host.Endpoints;
using Flattiverse.Gateway.Host.Transport;
using Flattiverse.Gateway.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton<WebSocketGatewayConnectionHandler>();

var app = builder.Build();

app.UseWebSockets();
app.MapGatewayEndpoints();

app.Run();

public partial class Program;
