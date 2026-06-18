using System.ServiceModel;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Despachos.Api.Data;
using Despachos.Api.Models;
using Despachos.Api.SoapSap;

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
        var confirmacionService = scope.ServiceProvider.GetRequiredService<ConfirmacionService>();

        var pendientes = await db.OutboxConfirmaciones
            .Where(o => o.Estado == OutboxEstado.Pendiente)
            .OrderBy(o => o.CreadoEn)
            .ToListAsync(ct);

        if (pendientes.Count == 0)
            return;

        var sapConfig = ConstruirConfigSap();
        if (sapConfig is null)
        {
            _logger.LogError("Configuracion SAP incompleta (endpoint/creds faltantes). No se envia outbox.");
            return;
        }

        foreach (var outbox in pendientes)
        {
            ct.ThrowIfCancellationRequested();
            await EnviarUnoAsync(outbox, sapConfig, db, confirmacionService, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private SapSoapConfig? ConstruirConfigSap()
    {
        var endpoint = _config["Sap:ConfirmacionEndpoint"];
        var username = _config["Sap:Username"];
        var password = _config["Sap:Password"];

        if (string.IsNullOrWhiteSpace(endpoint)
            || string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var useHttps = endpoint.StartsWith("https", StringComparison.OrdinalIgnoreCase);
        return new SapSoapConfig(
            endpoint,
            username,
            password,
            useHttps ? SIS_Confirma_CargaClient.EndpointConfiguration.HTTPS_Port
                     : SIS_Confirma_CargaClient.EndpointConfiguration.HTTP_Port);
    }

    private async Task EnviarUnoAsync(OutboxConfirmacion outbox, SapSoapConfig sapConfig,
        DespachosDbContext db, ConfirmacionService confirmacionService, CancellationToken ct)
    {
        SIS_Confirma_CargaClient? client = null;
        try
        {
            var request = await confirmacionService.ConstruirRequestAsync(outbox.NroTransporte, ct);
            if (request is null)
            {
                _logger.LogWarning("No se pudo construir request para {NroTransporte}, marcando error", outbox.NroTransporte);
                outbox.Estado = OutboxEstado.Error;
                return;
            }

            var binding = new BasicHttpBinding
            {
                MaxBufferSize = int.MaxValue,
                MaxReceivedMessageSize = int.MaxValue,
                ReaderQuotas = System.Xml.XmlDictionaryReaderQuotas.Max,
                AllowCookies = true,
                SendTimeout = TimeSpan.FromSeconds(30),
                ReceiveTimeout = TimeSpan.FromSeconds(30),
                OpenTimeout = TimeSpan.FromSeconds(30),
                CloseTimeout = TimeSpan.FromSeconds(15)
            };
            if (sapConfig.UseHttps)
                binding.Security.Mode = BasicHttpSecurityMode.Transport;

            client = new SIS_Confirma_CargaClient(binding, new EndpointAddress(sapConfig.Endpoint));
            client.ClientCredentials.UserName.UserName = sapConfig.Username;
            client.ClientCredentials.UserName.Password = sapConfig.Password;

            try
            {
                await client.OpenAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo abrir canal SOAP a SAP para {NroTransporte}", outbox.NroTransporte);
                RegistrarFalloTransitorio(outbox);
                return;
            }

            SIS_Confirma_CargaResponse response;
            try
            {
                response = await client.SIS_Confirma_CargaAsync(request);
            }
            catch (ProtocolException pex)
            {
                _logger.LogWarning(pex, "SAP devolvio error HTTP (posible 5xx/auth) para {NroTransporte}, reintento",
                    outbox.NroTransporte);
                RegistrarFalloTransitorio(outbox);
                return;
            }
            catch (FaultException fex)
            {
                _logger.LogError(fex, "SAP devolvio SOAP Fault para {NroTransporte}, error de negocio no reintenta",
                    outbox.NroTransporte);
                outbox.Estado = OutboxEstado.Error;
                return;
            }
            catch (CommunicationException cex)
            {
                _logger.LogWarning(cex, "Error de comunicacion SOAP para {NroTransporte}, reintento",
                    outbox.NroTransporte);
                RegistrarFalloTransitorio(outbox);
                return;
            }

            outbox.Reintentos++;
            outbox.UltimoIntentoEn = DateTime.UtcNow;

            var returnNode = response?.MT_Confirma_Carga_Response?.Return;
            var type = returnNode?.TYPE?.Trim().ToUpperInvariant();
            var message = returnNode?.MESSAGE ?? "";

            if (type == "S")
            {
                outbox.Estado = OutboxEstado.Enviado;

                var header = await db.DespachosHeaders
                    .FirstOrDefaultAsync(h => h.NroTransporte == outbox.NroTransporte, ct);
                if (header is not null && header.Estado == EstadoDespacho.Completado)
                    header.Estado = EstadoDespacho.Confirmado;

                _logger.LogInformation(
                    "Confirmacion {NroTransporte} enviada a SAP exitosamente (Return.TYPE=S)",
                    outbox.NroTransporte);
            }
            else if (type == "W")
            {
                outbox.Estado = OutboxEstado.Enviado;

                var header = await db.DespachosHeaders
                    .FirstOrDefaultAsync(h => h.NroTransporte == outbox.NroTransporte, ct);
                if (header is not null && header.Estado == EstadoDespacho.Completado)
                    header.Estado = EstadoDespacho.Confirmado;

                _logger.LogInformation(
                    "Confirmacion {NroTransporte} enviada a SAP con advertencia (Return.TYPE=W): {Msg}",
                    outbox.NroTransporte, message);
            }
            else
            {
                outbox.Estado = OutboxEstado.Error;
                _logger.LogError(
                    "Confirmacion {NroTransporte} rechazada por SAP (Return.TYPE={Type}): {Msg}",
                    outbox.NroTransporte, type ?? "(null)", message);
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
        finally
        {
            if (client is not null)
            {
                try
                {
                    if (client.State == CommunicationState.Opened
                        || client.State == CommunicationState.Opening)
                        await client.CloseAsync();
                    else
                        client.Abort();
                }
                catch
                {
                    client.Abort();
                }
            }
        }
    }

    private static void RegistrarFalloTransitorio(OutboxConfirmacion outbox)
    {
        outbox.Reintentos++;
        outbox.UltimoIntentoEn = DateTime.UtcNow;

        if (outbox.Reintentos >= outbox.MaxReintentos)
            outbox.Estado = OutboxEstado.Error;
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

    private sealed record SapSoapConfig(
        string Endpoint,
        string Username,
        string Password,
        SIS_Confirma_CargaClient.EndpointConfiguration EndpointKind)
    {
        public bool UseHttps => EndpointKind == SIS_Confirma_CargaClient.EndpointConfiguration.HTTPS_Port;
    }
}
