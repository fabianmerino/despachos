using System.Threading.Channels;
using Opc.Ua;
using Opc.Ua.Client;
using Despachos.Api.Services;

namespace Despachos.Api.Services;

public sealed class OpcUaBackgroundService : BackgroundService
{
    private readonly ILogger<OpcUaBackgroundService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly Channel<string> _completadosChannel;
    private Session? _session;

    public ChannelReader<string> CompletadosReader => _completadosChannel.Reader;

    public OpcUaBackgroundService(
        ILogger<OpcUaBackgroundService> logger,
        IServiceScopeFactory scopeFactory,
        IConfiguration config)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _config = config;
        _completadosChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OPC-UA Background Service iniciando");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConectarYSuscribirAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fallo conexion OPC-UA, reintentando en 30s");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("OPC-UA Background Service detenido");
    }

    private async Task ConectarYSuscribirAsync(CancellationToken ct)
    {
        var endpointUrl = _config["OpcUa:EndpointUrl"] ?? "opc.tcp://localhost:4840";
        var userName = _config["OpcUa:UserName"];
        var password = _config["OpcUa:Password"];
        var nodeId = _config["OpcUa:NodeId"] ?? "ns=2;s=Despachos.Completados";

        _logger.LogInformation("Conectando a OPC-UA {Endpoint}", endpointUrl);

        var config = new ApplicationConfiguration
        {
            ApplicationName = "Despachos.Service",
            ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:DespachosService",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                AutoAcceptUntrustedCertificates = true
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
            TraceConfiguration = new TraceConfiguration()
        };

        await config.Validate(ApplicationType.Client);

        var endpoint = CoreClientUtils.SelectEndpoint(endpointUrl, useSecurity: false);
        var endpointConfig = EndpointConfiguration.Create(config);
        var configuredEndpoint = new ConfiguredEndpoint(null, endpoint, endpointConfig);

        if (!string.IsNullOrWhiteSpace(userName))
        {
            _session = await Session.Create(config, configuredEndpoint, false,
                "Despachos OPC-UA Session", 60000,
                new UserIdentity(userName, password), null, ct);
        }
        else
        {
            _session = await Session.Create(config, configuredEndpoint, false,
                "Despachos OPC-UA Session", 60000,
                null, null, ct);
        }

        _logger.LogInformation("OPC-UA conectado exitosamente a {Endpoint}", endpointUrl);

        _session.KeepAlive += (sender, e) =>
        {
            if (ServiceResult.IsBad(e.Status))
                _logger.LogWarning("OPC-UA KeepAlive error: {Status}", e.Status);
        };

        var subscription = new Subscription(_session.DefaultSubscription)
        {
            PublishingInterval = 1000
        };

        _session.AddSubscription(subscription);
        subscription.Create();

        var node = NodeId.Parse(nodeId);
        var monitoredItem = new MonitoredItem(subscription.DefaultItem)
        {
            DisplayName = "Despachos.Completados",
            StartNodeId = node,
            AttributeId = Attributes.Value,
            SamplingInterval = 500
        };

        monitoredItem.Notification += OnCompletadoNotification;
        subscription.AddItem(monitoredItem);
        subscription.ApplyChanges();

        _logger.LogInformation("Suscrito a nodo OPC-UA {NodeId}", nodeId);

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Desconectando de OPC-UA");
        }
        finally
        {
            subscription.RemoveItems(subscription.MonitoredItems);
            _session.RemoveSubscription(subscription);
            _session.Close();
            _session.Dispose();
            _session = null;
        }
    }

    private void OnCompletadoNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        foreach (var value in item.DequeueValues())
        {
            var str = value.Value?.ToString() ?? "";
            _logger.LogInformation("OPC-UA notificacion recibida: {Value}", str);

            if (string.IsNullOrWhiteSpace(str))
                continue;

            var nroTransporte = ParseNroTransporte(str);
            if (nroTransporte is null)
            {
                _logger.LogWarning("Formato OPC-UA no reconocido: {Value}", str);
                continue;
            }

            _completadosChannel.Writer.TryWrite(nroTransporte);
        }
    }

    private static string? ParseNroTransporte(string value)
    {
        var parts = value.Split('|');
        if (parts.Length == 2 && parts[1] == "1" && !string.IsNullOrWhiteSpace(parts[0]))
            return parts[0];

        if (value.Length >= 10 && value.EndsWith("|1"))
            return value[..^2];

        if (decimal.TryParse(value, out _))
            return value;

        return null;
    }
}
