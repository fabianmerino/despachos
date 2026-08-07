using System.ServiceModel;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Despachos.Api.Data;
using Despachos.Api.Models;
using Despachos.Api.SoapSap;

namespace Despachos.Api.Services;

public sealed class OutboxWorker : BackgroundService
{
    private static readonly TimeSpan DrenadoPeriodico = TimeSpan.FromSeconds(15);

    private readonly ILogger<OutboxWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ChannelReader<string> _completadosReader;

    public OutboxWorker(
        ILogger<OutboxWorker> logger,
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        OpcUaBackgroundService opcUaService)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _config = config;
        _completadosReader = opcUaService.CompletadosReader;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxWorker iniciando");

        await EjecutarStartupScanAsync(stoppingToken);

        var notificacionesTask = ProcesarNotificacionesAsync(stoppingToken);
        var drenadoTask = DrenarPeriodicamenteAsync(stoppingToken);

        await Task.WhenAll(notificacionesTask, drenadoTask);

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

    private async Task EjecutarStartupScanAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var confirmacionService = scope.ServiceProvider.GetRequiredService<ConfirmacionService>();
        var pendientes = await confirmacionService.ObtenerCompletadosPendientesAsync(ct);

        foreach (var nro in pendientes)
        {
            try
            {
                _logger.LogInformation("Startup scan: procesando completado pendiente {NroTransporte}", nro);
                using var innerScope = _scopeFactory.CreateScope();
                var innerConfirmacion = innerScope.ServiceProvider.GetRequiredService<ConfirmacionService>();
                await innerConfirmacion.ProcesarDespachoCompletadoAsync(nro, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en startup scan procesando {NroTransporte}", nro);
            }
        }
    }

    private async Task ProcesarNotificacionesAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            string nroTransporte;
            try
            {
                nroTransporte = await _completadosReader.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                _logger.LogInformation("Procesando notificacion OPC-UA: {NroTransporte}", nroTransporte);

                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<ConfirmacionService>();
                await svc.ProcesarDespachoCompletadoAsync(nroTransporte, stoppingToken);

                await ProcesarOutboxAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando completado {NroTransporte}", nroTransporte);
            }
        }
    }

    private async Task DrenarPeriodicamenteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(DrenadoPeriodico);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ProcesarOutboxAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error en drenado periodico del outbox");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ProcesarOutboxAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DespachosDbContext>();

        var ahora = DateTime.UtcNow;
        var pendientes = await db.OutboxConfirmaciones
            .Where(o => o.Estado == OutboxEstado.Pendiente)
            .Where(o => o.ProximoIntentoEn == null || o.ProximoIntentoEn <= ahora)
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
            await EnviarUnoAsync(outbox, sapConfig, db, ct);
            await db.SaveChangesAsync(ct);
        }
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
        DespachosDbContext db, CancellationToken ct)
    {
        SIS_Confirma_CargaClient? client = null;
        try
        {
            if (string.IsNullOrWhiteSpace(outbox.Payload))
            {
                _logger.LogWarning("Outbox {NroTransporte} sin payload, marcando error", outbox.NroTransporte);
                outbox.Estado = OutboxEstado.Error;
                return;
            }

            var innerRequest = ConfirmacionService.DeserializarPayload(outbox.Payload);
            var request = new SIS_Confirma_CargaRequest(innerRequest);

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
                outbox.ProximoIntentoEn = DateTime.UtcNow + CalcularBackoff(outbox.Reintentos);
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
        else
            outbox.ProximoIntentoEn = DateTime.UtcNow + CalcularBackoff(outbox.Reintentos);
    }

    private static TimeSpan CalcularBackoff(int reintento) => reintento switch
    {
        1 => TimeSpan.FromSeconds(10),
        2 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromSeconds(60)
    };

    private sealed record SapSoapConfig(
        string Endpoint,
        string Username,
        string Password,
        SIS_Confirma_CargaClient.EndpointConfiguration EndpointKind)
    {
        public bool UseHttps => EndpointKind == SIS_Confirma_CargaClient.EndpointConfiguration.HTTPS_Port;
    }
}
