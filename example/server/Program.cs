using Npgsql;
using Server;
using Typesafe.PgSignal;
using TypedSignalR.Client.DevTools;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing (appsettings.json).");

builder.Services.AddSingleton(new Db.QueriesSql(connectionString));   // sqlc, generated into gen/Db
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

builder.Services.AddSignalR();

// Live streams for the hub: a singleton the hub uses, which also runs in the background (LISTEN).
builder.Services.AddSingleton<PgSignal>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PgSignal>());

// The Bun dev server runs on another port. SignalR sends credentials, so the origin must be named (no AllowAnyOrigin).
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:3000")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

app.UseCors();

// A Swagger-like GUI for the hub at /signalr-dev: call hub methods, see what the server sends. Development only.
if (app.Environment.IsDevelopment())
{
    app.UseSignalRHubSpecification();
    app.UseSignalRHubDevelopmentUI();
}

app.MapHub<OrderHub>("/hubs/orders");   // DevTools reads this path at compile time, so keep it a string literal
app.MapGet("/", () => "SignalR server running. Open the client at http://localhost:3000");

app.Run();
