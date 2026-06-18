using System.ServiceModel;
using Despachos.Api.Services;

namespace Despachos.Api.SoapInbound;

public sealed class PlanificaCargaService : IPlanificaCargaService
{
    private readonly DespachoService _despachoService;
    private readonly ILogger<PlanificaCargaService> _logger;

    public PlanificaCargaService(DespachoService despachoService, ILogger<PlanificaCargaService> logger)
    {
        _despachoService = despachoService;
        _logger = logger;
    }

    public async Task<SIS_Planifica_CargaResponse> SIS_Planifica_Carga(SIS_Planifica_CargaRequest request)
    {
        var inner = request?.MT_Planifica_Carga_Request;
        if (inner is null)
        {
            _logger.LogWarning("Request SOAP 3.1 nulo o sin body");
            return Fault("Request nulo o mal formado");
        }

        _logger.LogInformation("Recibida planificacion SAP para NroTransporte {Nro}", inner.I_NRO_TRANSPORTE);

        try
        {
            var result = await _despachoService.ProcesarPlanificacionAsync(inner, CancellationToken.None);

            return result.Match(
                errors =>
                {
                    var msg = string.Join("; ", errors.Select(e => $"{e.Field}: {e.Message}"));
                    _logger.LogWarning("Validacion fallida para {Nro}: {Errors}", inner.I_NRO_TRANSPORTE, msg);
                    return Fault(msg);
                },
                order =>
                {
                    _logger.LogInformation("Planificacion {Nro} guardada en estado {Estado}",
                        order.NroTransporte, order.Estado);
                    return Ok($"Orden {order.NroTransporte} guardada en estado {order.Estado}");
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando planificacion {Nro}", inner.I_NRO_TRANSPORTE);
            return Fault($"Error interno: {ex.Message}");
        }
    }

    private static SIS_Planifica_CargaResponse Ok(string message) =>
        new(new MT_Planifica_Carga_Response
        {
            Return = new DT_RETURN { TYPE = "S", MESSAGE = message }
        });

    private static SIS_Planifica_CargaResponse Fault(string message) =>
        new(new MT_Planifica_Carga_Response
        {
            Return = new DT_RETURN { TYPE = "E", MESSAGE = message }
        });
}
