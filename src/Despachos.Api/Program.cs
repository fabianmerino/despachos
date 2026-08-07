using Serilog;
using Microsoft.EntityFrameworkCore;
using SoapCore;
using Despachos.Api.Data;
using Despachos.Api.Endpoints;
using Despachos.Api.Middleware;
using Despachos.Api.Services;
using Despachos.Api.SoapInbound;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "DespachosPetroperu";
});

var logPath = builder.Configuration["Logging:FilePath"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Despachos", "logs", "despachos-.log");

builder.Host.UseSerilog((context, config) =>
{
    config
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(logPath,
            rollingInterval: RollingInterval.Day,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Server=localhost;Database=Despachos;User=root;Password=;";

// ServerVersion.AutoDetect() abriria una conexion sincrona a MySQL durante el arranque del host,
// lo que contradice el arranque degradado (ADR 17): si MySQL esta caido, la app no llegaria a
// levantar Kestrel. ServerVersion.Parse no toca la red, solo interpreta la version configurada.
var mySqlVersion = builder.Configuration["Database:MySqlServerVersion"] ?? "8.0.36-mysql";
var serverVersion = ServerVersion.Parse(mySqlVersion);

builder.Services.AddDbContext<DespachosDbContext>(options =>
    options.UseMySql(connectionString, serverVersion));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<DespachosDbContext>("mysql", tags: new[] { "ready" })
    .AddCheck<OpcUaHealthCheck>("opcua", tags: new[] { "ready" })
    .AddCheck<OutboxHealthCheck>("outbox", tags: new[] { "ready" });

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<DespachoService>();
builder.Services.AddScoped<ConfirmacionService>();
builder.Services.AddSingleton<OpcUaBackgroundService>();
builder.Services.AddScoped<IPlanificaCargaService, PlanificaCargaService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OpcUaBackgroundService>());
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(35);
});

var app = builder.Build();

if (string.IsNullOrWhiteSpace(app.Configuration["SapInbound:Username"]))
{
    Log.Fatal("SapInbound:Username no esta configurado. El servicio no arranca: sin credenciales, " +
        "el Basic Auth del inbound SOAP quedaria abierto sin autenticacion.");
    throw new InvalidOperationException("SapInbound:Username no esta configurado.");
}

app.UseMiddleware<BasicAuthMiddleware>();

IApplicationBuilder appBuilder = app;
appBuilder.UseSoapEndpoint<IPlanificaCargaService>(
    "/soap/planificacion-carga",
    new SoapEncoderOptions(),
    SoapSerializer.XmlSerializer);

app.MapHealthEndpoints();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DespachosDbContext>();
    try
    {
        await db.Database.MigrateAsync();
        Log.Information("Migraciones de base de datos aplicadas exitosamente");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "No se pudo conectar a MySQL en startup (arranque degradado, ver ADR 17)");
    }
}

app.Run();
