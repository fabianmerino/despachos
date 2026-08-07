using Despachos.Api.Services;

namespace Despachos.Api.Endpoints;

public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/webhooks/despacho-completado", async (
            DespachoCompletadoWebhookRequest? request,
            WebhookCompletadoService service,
            CancellationToken ct) =>
        {
            var result = await service.ProcesarNotificacionAsync(request?.NroTransporte, ct);

            return result.Match<IResult>(
                errors => Results.BadRequest(new
                {
                    errors = errors.Select(e => new { field = e.Field, message = e.Message })
                }),
                nroTransporte => Results.Accepted(value: new { nroTransporte }));
        });
    }
}

public sealed record DespachoCompletadoWebhookRequest(string? NroTransporte);
