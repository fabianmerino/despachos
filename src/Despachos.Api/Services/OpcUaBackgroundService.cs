using System.Threading.Channels;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Despachos.Api.Services;

namespace Despachos.Api.Services;

public sealed class OpcUaBackgroundService : BackgroundService
{
    private readonly ILogger<OpcUaBackgroundService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly Channel<string> _completadosChannel;
    private Session? _session;
    private SessionReconnectHandler? _reconnectHandler;
    private volatile bool _connected;

    public ChannelReader<string> CompletadosReader => _completadosChannel.Reader;
    public bool IsConnected => _connected;

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
        var useSecurity = _config.GetValue<bool?>("OpcUa:UseSecurity") ?? false;
        var autoAcceptUntrusted = _config.GetValue<bool?>("OpcUa:AutoAcceptUntrustedCertificates") ?? false;

        _logger.LogInformation("Conectando a OPC-UA {Endpoint}", endpointUrl);

        if (autoAcceptUntrusted)
            _logger.LogWarning("OPC-UA: AutoAcceptUntrustedCertificates esta activo. Usar solo en pruebas.");

        var pkiRoot = _config["OpcUa:PkiRootPath"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Despachos", "pki");

        var config = new ApplicationConfiguration
        {
            ApplicationName = "Despachos.Service",
            ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:DespachosService",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(pkiRoot, "own"),
                    SubjectName = $"CN=Despachos.Service, DC={System.Net.Dns.GetHostName()}"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(pkiRoot, "issuers")
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(pkiRoot, "trusted")
                },
                RejectedCertificateStore = new CertificateStoreIdentifier
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(pkiRoot, "rejected")
                },
                AutoAcceptUntrustedCertificates = autoAcceptUntrusted,
                AddAppCertToTrustedStore = true
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
            TraceConfiguration = new TraceConfiguration()
        };

        await config.Validate(ApplicationType.Client);

        var application = new ApplicationInstance
        {
            ApplicationName = config.ApplicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationConfiguration = config
        };

        var haveAppCertificate = await application.CheckApplicationInstanceCertificate(false, 0);
        if (!haveAppCertificate)
            _logger.LogWarning("No se pudo crear o validar el certificado de aplicacion OPC-UA en {PkiRoot}", pkiRoot);

        var endpoint = CoreClientUtils.SelectEndpoint(endpointUrl, useSecurity: useSecurity);
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

        _session.TransferSubscriptionsOnReconnect = true;
        _connected = true;
        _session.KeepAlive += Session_KeepAlive;

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
            _connected = false;
            _reconnectHandler?.Dispose();
            _reconnectHandler = null;

            if (_session is not null)
            {
                _session.KeepAlive -= Session_KeepAlive;
                try
                {
                    subscription.RemoveItems(subscription.MonitoredItems);
                    _session.RemoveSubscription(subscription);
                    _session.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error cerrando sesion OPC-UA");
                }
                _session.Dispose();
                _session = null;
            }
        }
    }

    private void Session_KeepAlive(Opc.Ua.Client.ISession session, KeepAliveEventArgs e)
    {
        if (!ServiceResult.IsBad(e.Status))
        {
            _connected = true;
            return;
        }

        _connected = false;
        _logger.LogWarning("OPC-UA KeepAlive error: {Status}", e.Status);

        if (_reconnectHandler is not null)
            return;

        _logger.LogWarning("Sesion OPC-UA caida, iniciando reconexion");
        _reconnectHandler = new SessionReconnectHandler(true, 30000);
        _reconnectHandler.BeginReconnect(session, 10000, OnReconnectComplete);
    }

    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _reconnectHandler))
            return;

        if (_reconnectHandler?.Session is Session reconnectedSession)
        {
            _session = reconnectedSession;
            _connected = true;
            _logger.LogInformation("OPC-UA reconectado exitosamente");
        }

        _reconnectHandler?.Dispose();
        _reconnectHandler = null;
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
