using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Despachos.Api.Data;
using Despachos.Api.Models;

namespace Despachos.Api.Services;

public sealed class OutboxWorker : BackgroundService
{
    private readonly ILogger<OutboxWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ChannelReader<string> _completadosReader;

    public OutboxWorker(
        ILogger<OutboxWorker> logger,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        OpcUaBackgroundService opcUaService)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _completadosReader = opcUaService.CompletadosReader;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxWorker iniciando");

        using var scope = _scopeFactory.CreateScope();
        var confirmacionService = scope.ServiceProvider.GetRequiredService<ConfirmacionService>();

        var pendientes = await confirmacionService.ObtenerCompletadosPendientesAsync(stoppingToken);
        foreach (var nro in pendientes)
        {
            _logger.LogInformation("Startup scan: procesando completado pendiente {NroTransporte}", nro);
            using var innerScope = _scopeFactory.CreateScope();
            var innerConfirmacion = innerScope.ServiceProvider.GetRequiredService<ConfirmacionService>();
            await innerConfirmacion.ProcesarDespachoCompletadoAsync(nro, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string? nroTransporte = null;
                try
                {
                    nroTransporte = await _completadosReader.ReadAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _logger.LogInformation("Procesando notificacion OPC-UA: {NroTransporte}", nroTransporte);

                using var scope2 = _scopeFactory.CreateScope();
                var svc = scope2.ServiceProvider.GetRequiredService<ConfirmacionService>();
                await svc.ProcesarDespachoCompletadoAsync(nroTransporte, stoppingToken);

                await ProcesarOutboxAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando completado");
            }
        }

        _logger.LogInformation("OutboxWorker: drenando mensajes pendientes (graceful shutdown)");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ProcesarOutboxAsync(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error en drain de outbox durante shutdown");
        }

        _logger.LogInformation("OutboxWorker detenido");
    }

    private async Task ProcesarOutboxAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DespachosDbContext>();

        var pendientes = await db.OutboxConfirmaciones
            .Where(o => o.Estado == OutboxEstado.Pendiente)
            .OrderBy(o => o.CreadoEn)
            .ToListAsync(ct);

        var sapUrl = _config["Sap:ConfirmacionUrl"] ?? "http://localhost:8080/sap/confirmacion";
        var apiKey = _config["Sap:ApiKey"] ?? "";
        var client = _httpClientFactory.CreateClient("SapClient");

        foreach (var outbox in pendientes)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, sapUrl)
                {
                    Content = new StringContent(outbox.Payload, System.Text.Encoding.UTF8, "application/xml")
                };
                request.Headers.Add("X-API-Key", apiKey);

                var response = await client.SendAsync(request, ct);
                outbox.Reintentos++;
                outbox.UltimoIntentoEn = DateTime.UtcNow;

                if (response.IsSuccessStatusCode)
                {
                    outbox.Estado = OutboxEstado.Enviado;

                    var header = await db.DespachosHeaders
                        .FirstOrDefaultAsync(h => h.NroTransporte == outbox.NroTransporte, ct);
                    if (header is not null && header.Estado == EstadoDespacho.Completado)
                        header.Estado = EstadoDespacho.Confirmado;

                    _logger.LogInformation("Confirmacion {NroTransporte} enviada a SAP exitosamente", outbox.NroTransporte);
                }
                else if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (outbox.Reintentos < outbox.MaxReintentos)
                    {
                        _logger.LogWarning("SAP error {Status} para {NroTransporte}, reintento {Reintento}/{Max}",
                            (int)response.StatusCode, outbox.NroTransporte, outbox.Reintentos, outbox.MaxReintentos);
                        await AplicarBackoffAsync(outbox.Reintentos, ct);
                    }
                    else
                    {
                        outbox.Estado = OutboxEstado.Error;
                        _logger.LogError("SAP error {Status} para {NroTransporte} agotado tras {Max} reintentos",
                            (int)response.StatusCode, outbox.NroTransporte, outbox.MaxReintentos);
                    }
                }
                else
                {
                    outbox.Estado = OutboxEstado.Error;
                    _logger.LogError("SAP error cliente {Status} para {NroTransporte}, no se reintenta",
                        (int)response.StatusCode, outbox.NroTransporte);
                }
            }
            catch (Exception ex)
            {
                outbox.Reintentos++;
                outbox.UltimoIntentoEn = DateTime.UtcNow;

                if (outbox.Reintentos < outbox.MaxReintentos)
                {
                    _logger.LogWarning(ex, "Error envio SAP para {NroTransporte}, reintento {Reintento}/{Max}",
                        outbox.NroTransporte, outbox.Reintentos, outbox.MaxReintentos);
                    await AplicarBackoffAsync(outbox.Reintentos, ct);
                }
                else
                {
                    outbox.Estado = OutboxEstado.Error;
                    _logger.LogError(ex, "Error envio SAP para {NroTransporte} agotado tras {Max} reintentos",
                        outbox.NroTransporte, outbox.MaxReintentos);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task AplicarBackoffAsync(int reintento, CancellationToken ct)
    {
        var delay = reintento switch
        {
            1 => TimeSpan.FromSeconds(10),
            2 => TimeSpan.FromSeconds(30),
            3 => TimeSpan.FromSeconds(60),
            _ => TimeSpan.FromSeconds(60)
        };
        await Task.Delay(delay, ct);
    }
}
