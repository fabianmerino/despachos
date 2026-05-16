using Serilog;
using Microsoft.EntityFrameworkCore;
using Despachos.Api.Data;
using Despachos.Api.Endpoints;
using Despachos.Api.Middleware;
using Despachos.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) =>
{
    config
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File("logs/despachos-.log",
            rollingInterval: RollingInterval.Day,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Server=localhost;Database=Despachos;User=root;Password=;";

builder.Services.AddDbContext<DespachosDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<DespachosDbContext>("mysql", tags: new[] { "ready" })
    .AddCheck<OpcUaHealthCheck>("opcua", tags: new[] { "ready" })
    .AddCheck<OutboxHealthCheck>("outbox", tags: new[] { "ready" });

builder.Services.AddHttpClient("SapClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Accept.Add(
        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/xml"));
});

builder.Services.AddSingleton<DespachoService>();
builder.Services.AddSingleton<ConfirmacionService>();
builder.Services.AddSingleton<OpcUaBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OpcUaBackgroundService>());
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(35);
});

var app = builder.Build();

app.UseMiddleware<ApiKeyMiddleware>();

app.MapDespachoEndpoints();
app.MapHealthEndpoints();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DespachosDbContext>();
    try
    {
        await db.Database.EnsureCreatedAsync();
        Log.Information("Base de datos verificada/creada exitosamente");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "No se pudo conectar a MySQL en startup");
    }
}

app.Run();
