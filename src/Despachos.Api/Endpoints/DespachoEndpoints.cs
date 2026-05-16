using System.Xml.Serialization;
using Despachos.Api.Data;
using Despachos.Api.Models;
using Despachos.Api.Services;

namespace Despachos.Api.Endpoints;

public static class DespachoEndpoints
{
    public static void MapDespachoEndpoints(this WebApplication app)
    {
        app.MapPost("/planificacion", async (
            HttpRequest request,
            DespachoService service,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var xmlBody = await reader.ReadToEndAsync(ct);

            logger.LogInformation("Recibida planificacion SAP");

            var result = await service.ProcesarPlanificacionAsync(xmlBody, ct);

            return result.Match(
                errors =>
                {
                    logger.LogWarning("Validacion fallida: {Errors}", errors.Count);
                    return Results.Text(errors.ToXml(), "application/xml",
                        System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
                },
                order =>
                {
                    logger.LogInformation("Planificacion {Nro} guardada exitosamente", order.NroTransporte);
                    var responseXml = "<PlanificacionResponse><Status>OK</Status>" +
                        $"<NroTransporte>{System.Security.SecurityElement.Escape(order.NroTransporte)}</NroTransporte>" +
                        $"<Estado>{System.Security.SecurityElement.Escape(order.Estado)}</Estado>" +
                        "</PlanificacionResponse>";
                    return Results.Text(responseXml, "application/xml",
                        System.Text.Encoding.UTF8, StatusCodes.Status200OK);
                });
        })
        .WithName("PlanificacionCarga")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status409Conflict);

        app.MapPost("/cancelacion", async (
            HttpRequest request,
            DespachoService service,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var xmlBody = await reader.ReadToEndAsync(ct);

            NroTransporteXml? cancelReq;
            try
            {
                var serializer = new XmlSerializer(typeof(NroTransporteXml));
                using var sr = new StringReader(xmlBody);
                cancelReq = (NroTransporteXml)serializer.Deserialize(sr)!;
            }
            catch
            {
                return Results.Text(
                    "<ErrorResponse><Status>ERROR</Status><Messages><Message Field=\"XML\">XML mal formado</Message></Messages></ErrorResponse>",
                    "application/xml", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            }

            if (string.IsNullOrWhiteSpace(cancelReq?.NroTransporte))
                return Results.Text(
                    "<ErrorResponse><Status>ERROR</Status><Messages><Message Field=\"NroTransporte\">Requerido</Message></Messages></ErrorResponse>",
                    "application/xml", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            logger.LogInformation("Cancelacion solicitada para {NroTransporte}", cancelReq.NroTransporte);

            var result = await service.CancelarOrdenAsync(cancelReq.NroTransporte, ct);

            return result.Match(
                errors =>
                {
                    logger.LogWarning("Cancelacion fallida: {Error}", errors[0].Message);
                    return Results.Text(errors.ToXml(), "application/xml",
                        System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
                },
                order =>
                {
                    logger.LogInformation("Orden {NroTransporte} cancelada", order.NroTransporte);
                    var respXml = "<CancelacionResponse><Status>OK</Status>" +
                        $"<NroTransporte>{System.Security.SecurityElement.Escape(order.NroTransporte)}</NroTransporte>" +
                        $"<Estado>{System.Security.SecurityElement.Escape(order.Estado)}</Estado>" +
                        "</CancelacionResponse>";
                    return Results.Text(respXml, "application/xml",
                        System.Text.Encoding.UTF8, StatusCodes.Status200OK);
                });
        })
        .WithName("Cancelacion")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);
    }
}

[XmlRoot("Cancelacion")]
public sealed class NroTransporteXml
{
    [XmlElement("NroTransporte")]
    public string NroTransporte { get; set; } = null!;
}
