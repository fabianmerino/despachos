using System.Xml.Serialization;
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
            .AnyAsync(o => o.NroTransporte == nroTransporte, ct);

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

        var innerRequest = ArmarRequestConfirmacion(header, confirmaciones).MT_Confirma_Carga_Request!;

        var outboxEntry = new OutboxConfirmacion
        {
            NroTransporte = nroTransporte,
            Payload = SerializarPayload(innerRequest),
            Reintentos = 0,
            MaxReintentos = 3,
            Estado = OutboxEstado.Pendiente,
            CreadoEn = DateTime.UtcNow
        };

        _db.OutboxConfirmaciones.Add(outboxEntry);

        if (header.Estado != EstadoDespacho.Confirmado && header.Estado != EstadoDespacho.Cancelado)
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
        var pendientes = await _db.ConfirmacionesDespacho
            .Select(c => c.NroTransporte)
            .Distinct()
            .Where(nro => !_db.OutboxConfirmaciones.Any(o => o.NroTransporte == nro))
            .ToListAsync(ct);

        return pendientes;
    }

    // El operador del ACCULOAD tipea el NroTransporte a mano y sistematicamente omite el
    // segundo digito (ej. el real "8004676326" queda escrito como "804676326"), asi que
    // guia_factura de despachos_completos no siempre calza exacto contra
    // despachos_header.NroTransporte. Se intenta, en orden: (1) match exacto, por si la guia
    // vino completa, (2) reconstruccion insertando un "0" despues del primer caracter, que es
    // el patron de truncamiento confirmado, (3) match por sufijo como respaldo para otros
    // patrones de truncamiento no confirmados. Si ninguno resuelve a un unico candidato, se
    // devuelve null a proposito: mejor no confirmar automaticamente que confirmar contra el
    // transporte equivocado.
    internal static string? MatchNroTransporte(string guiaFactura, IEnumerable<string> candidatos)
    {
        if (string.IsNullOrWhiteSpace(guiaFactura))
            return null;

        var candidatosList = candidatos.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();

        var exacto = candidatosList.Where(n => n == guiaFactura).ToList();
        if (exacto.Count == 1)
            return exacto[0];

        if (guiaFactura.Length >= 1)
        {
            var reconstruido = guiaFactura[0] + "0" + guiaFactura[1..];
            var porReconstruccion = candidatosList.Where(n => n == reconstruido).ToList();
            if (porReconstruccion.Count == 1)
                return porReconstruccion[0];
        }

        return MatchNroTransportePorSufijo(guiaFactura, candidatosList);
    }

    internal static string? MatchNroTransportePorSufijo(string guiaFactura, IEnumerable<string> candidatos)
    {
        if (string.IsNullOrWhiteSpace(guiaFactura))
            return null;

        var coincidencias = candidatos
            .Where(n => !string.IsNullOrWhiteSpace(n)
                && n.EndsWith(guiaFactura, StringComparison.Ordinal))
            .Distinct()
            .ToList();

        return coincidencias.Count == 1 ? coincidencias[0] : null;
    }

    // DT_Confirma_Carga_Request.Detalle es un array "jagged" (DT_Confirma_Carga_DetItem[][]) generado por
    // svcutil cuyo XmlArrayItemAttribute no coincide con el tipo real del array interno, lo que hace que
    // XmlSerializer falle al generar el serializador. Por eso el payload persistido usa un DTO plano propio.
    internal static string SerializarPayload(DT_Confirma_Carga_Request request)
    {
        var payload = new ConfirmacionPayload
        {
            NroTransporte = request.I_NRO_TRANSPORTE ?? "",
            Items = (request.Detalle?.SelectMany(d => d ?? Array.Empty<DT_Confirma_Carga_DetItem>())
                    ?? Enumerable.Empty<DT_Confirma_Carga_DetItem>())
                .Select(i => new ConfirmacionPayloadItem
                {
                    NroTrans = i.NRO_TRANS ?? "",
                    NroEntrega = i.NRO_ENTREGA ?? "",
                    Compartimento = i.COMPARTIMENTO ?? "",
                    ProdComer = i.PROD_COMER ?? "",
                    TDespacho = i.T_DESPACHO ?? "",
                    ApiDespacho = i.API_DESPACHO ?? "",
                    VolDespaObs = i.VOL_DESPA_OBS ?? "",
                    Umvol = i.UMVOL ?? "",
                    VolDespa60 = i.VOL_DESPA_60 ?? ""
                }).ToList()
        };

        var serializer = new XmlSerializer(typeof(ConfirmacionPayload));
        using var writer = new StringWriter();
        serializer.Serialize(writer, payload);
        return writer.ToString();
    }

    internal static DT_Confirma_Carga_Request DeserializarPayload(string payload)
    {
        var serializer = new XmlSerializer(typeof(ConfirmacionPayload));
        using var reader = new StringReader(payload);
        var parsed = (ConfirmacionPayload)serializer.Deserialize(reader)!;

        var items = parsed.Items.Select(i => new DT_Confirma_Carga_DetItem
        {
            NRO_TRANS = i.NroTrans,
            NRO_ENTREGA = i.NroEntrega,
            COMPARTIMENTO = i.Compartimento,
            PROD_COMER = i.ProdComer,
            T_DESPACHO = i.TDespacho,
            API_DESPACHO = i.ApiDespacho,
            VOL_DESPA_OBS = i.VolDespaObs,
            UMVOL = i.Umvol,
            VOL_DESPA_60 = i.VolDespa60
        }).ToArray();

        return new DT_Confirma_Carga_Request
        {
            I_NRO_TRANSPORTE = parsed.NroTransporte,
            Detalle = new[] { items }
        };
    }
}

[XmlRoot("ConfirmacionPayload")]
public sealed class ConfirmacionPayload
{
    public string NroTransporte { get; set; } = "";

    [XmlArrayItem("Item")]
    public List<ConfirmacionPayloadItem> Items { get; set; } = new();
}

public sealed class ConfirmacionPayloadItem
{
    public string NroTrans { get; set; } = "";
    public string NroEntrega { get; set; } = "";
    public string Compartimento { get; set; } = "";
    public string ProdComer { get; set; } = "";
    public string TDespacho { get; set; } = "";
    public string ApiDespacho { get; set; } = "";
    public string VolDespaObs { get; set; } = "";
    public string Umvol { get; set; } = "";
    public string VolDespa60 { get; set; } = "";
}
