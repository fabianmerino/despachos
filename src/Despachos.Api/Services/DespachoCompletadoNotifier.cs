using System.Threading.Channels;

namespace Despachos.Api.Services;

// Reemplaza el rol de canal que antes cumplia OpcUaBackgroundService: desacopla la
// recepcion del webhook (Endpoints/WebhookEndpoints.cs) del consumo en OutboxWorker.
public sealed class DespachoCompletadoNotifier
{
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait });

    public ChannelWriter<string> Writer => _channel.Writer;
    public ChannelReader<string> Reader => _channel.Reader;
}
