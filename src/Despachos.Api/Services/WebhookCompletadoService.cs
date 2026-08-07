namespace Despachos.Api.Services;

public sealed class WebhookCompletadoService
{
    private readonly DespachoCompletadoNotifier _notifier;
    private readonly ILogger<WebhookCompletadoService> _logger;

    public WebhookCompletadoService(DespachoCompletadoNotifier notifier, ILogger<WebhookCompletadoService> logger)
    {
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<Either<ValidationErrors, string>> ProcesarNotificacionAsync(
        string? nroTransporte, CancellationToken ct)
    {
        var nro = nroTransporte?.Trim();

        if (string.IsNullOrWhiteSpace(nro))
            return new ValidationErrors { new("NroTransporte", "Requerido") };

        if (nro.Length > 10)
            return new ValidationErrors { new("NroTransporte", $"Excede longitud maxima (10): {nro}") };

        await _notifier.Writer.WriteAsync(nro, ct);

        _logger.LogInformation("Webhook despacho-completado recibido: {NroTransporte}", nro);

        return nro;
    }
}
