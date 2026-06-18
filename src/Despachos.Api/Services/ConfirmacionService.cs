using Microsoft.EntityFrameworkCore;
using Despachos.Api.Data;
using Despachos.Api.Models;
using Despachos.Api.SoapSap;

namespace Despachos.Api.Services;

public sealed class ConfirmacionService
{
    private readonly DespachosDbContext _db;
    private readonly ILogger<ConfirmacionService> _logger;

    public ConfirmacionService(DespachosDbContext db, ILogger<ConfirmacionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task ProcesarDespachoCompletadoAsync(string nroTransporte, CancellationToken ct)
    {
        var existeEnOutbox = await _db.OutboxConfirmaciones
            .AnyAsync(o => o.NroTransporte == nroTransporte
                && o.Estado != OutboxEstado.Error, ct);

        if (existeEnOutbox)
        {
            _logger.LogInformation("Despacho {NroTransporte} ya encolado en outbox, omitiendo", nroTransporte);
            return;
        }

        var header = await _db.DespachosHeaders
            .Include(h => h.Details)
            .FirstOrDefaultAsync(h => h.NroTransporte == nroTransporte, ct);

        if (header is null)
        {
            _logger.LogWarning("Despacho {NroTransporte} no encontrado en BD para confirmacion", nroTransporte);
            return;
        }

        var confirmaciones = await _db.ConfirmacionesDespacho
            .Where(c => c.NroTransporte == nroTransporte)
            .ToListAsync(ct);

        if (confirmaciones.Count == 0)
        {
            _logger.LogWarning("Sin datos de confirmacion para {NroTransporte}", nroTransporte);
            return;
        }

        var outboxEntry = new OutboxConfirmacion
        {
            NroTransporte = nroTransporte,
            Payload = "",
            Reintentos = 0,
            MaxReintentos = 3,
            Estado = OutboxEstado.Pendiente,
            CreadoEn = DateTime.UtcNow
        };

        _db.OutboxConfirmaciones.Add(outboxEntry);
        header.Estado = EstadoDespacho.Completado;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Despacho {NroTransporte} encolado en outbox para envio a SAP", nroTransporte);
    }

    public async Task<SIS_Confirma_CargaRequest?> ConstruirRequestAsync(string nroTransporte, CancellationToken ct)
    {
        var header = await _db.DespachosHeaders
            .Include(h => h.Details)
            .FirstOrDefaultAsync(h => h.NroTransporte == nroTransporte, ct);

        if (header is null)
        {
            _logger.LogWarning("Despacho {NroTransporte} no encontrado en BD para construir request", nroTransporte);
            return null;
        }

        var confirmaciones = await _db.ConfirmacionesDespacho
            .Where(c => c.NroTransporte == nroTransporte)
            .ToListAsync(ct);

        if (confirmaciones.Count == 0)
        {
            _logger.LogWarning("Sin datos de confirmacion para {NroTransporte} al construir request", nroTransporte);
            return null;
        }

        return ArmarRequestConfirmacion(header, confirmaciones);
    }

    internal static SIS_Confirma_CargaRequest ArmarRequestConfirmacion(DespachoHeader header,
        List<ConfirmacionDespacho> confirmaciones)
    {
        var items = new List<DT_Confirma_Carga_DetItem>();

        foreach (var conf in confirmaciones)
        {
            var detail = header.Details
                .FirstOrDefault(d => d.NroCompartimento == conf.NroCompartimento);

            items.Add(new DT_Confirma_Carga_DetItem
            {
                NRO_TRANS = header.NroTransporte,
                NRO_ENTREGA = detail?.NroEntrega ?? "",
                COMPARTIMENTO = conf.NroCompartimento,
                PROD_COMER = detail?.Producto ?? "",
                T_DESPACHO = conf.Temperatura?.ToString("F2",
                    System.Globalization.CultureInfo.InvariantCulture) ?? "",
                API_DESPACHO = conf.APIDespachado?.ToString("F4",
                    System.Globalization.CultureInfo.InvariantCulture) ?? "",
                VOL_DESPA_OBS = conf.VolObservado?.ToString("F2",
                    System.Globalization.CultureInfo.InvariantCulture) ?? "",
                UMVOL = detail?.UMVol ?? "",
                VOL_DESPA_60 = conf.Vol60?.ToString("F2",
                    System.Globalization.CultureInfo.InvariantCulture) ?? ""
            });
        }

        var inner = new DT_Confirma_Carga_Request
        {
            I_NRO_TRANSPORTE = header.NroTransporte,
            Detalle = new[] { items.ToArray() }
        };

        return new SIS_Confirma_CargaRequest(inner);
    }

    public async Task<List<string>> ObtenerCompletadosPendientesAsync(CancellationToken ct)
    {
        var completadosNoEnviados = await _db.DespachosHeaders
            .Where(h => h.Estado == EstadoDespacho.Completado)
            .Where(h => !_db.OutboxConfirmaciones
                .Any(o => o.NroTransporte == h.NroTransporte
                    && o.Estado == OutboxEstado.Enviado))
            .Select(h => h.NroTransporte)
            .ToListAsync(ct);

        return completadosNoEnviados;
    }
}
