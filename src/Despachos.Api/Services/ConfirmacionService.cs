using System.Text;
using System.Xml.Serialization;
using Microsoft.EntityFrameworkCore;
using Despachos.Api.Data;
using Despachos.Api.Models;

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

        var payloadXml = ArmarPayloadConfirmacion(header, confirmaciones);
        var outboxEntry = new OutboxConfirmacion
        {
            NroTransporte = nroTransporte,
            Payload = payloadXml,
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

    private static string ArmarPayloadConfirmacion(DespachoHeader header,
        List<ConfirmacionDespacho> confirmaciones)
    {
        var payload = new ConfirmacionCargaXml
        {
            Header = new ConfirmacionHeaderXml
            {
                NroTransporte = header.NroTransporte
            },
            Details = new List<ConfirmacionDetailXml>()
        };

        foreach (var conf in confirmaciones)
        {
            var detail = header.Details
                .FirstOrDefault(d => d.NroCompartimento == conf.NroCompartimento);

            payload.Details.Add(new ConfirmacionDetailXml
            {
                NroTransporte = header.NroTransporte,
                NroEntrega = detail?.NroEntrega ?? "",
                NroCompartimento = conf.NroCompartimento,
                Producto = detail?.Producto ?? "",
                Temperatura = conf.Temperatura?.ToString("F2",
                    System.Globalization.CultureInfo.InvariantCulture) ?? "0.00",
                APIDespachado = conf.APIDespachado?.ToString("F4",
                    System.Globalization.CultureInfo.InvariantCulture) ?? "0.0000",
                VolObservado = conf.VolObservado?.ToString("F2",
                    System.Globalization.CultureInfo.InvariantCulture) ?? "0.00",
                UMVol = detail?.UMVol ?? "",
                Vol60 = conf.Vol60?.ToString("F2",
                    System.Globalization.CultureInfo.InvariantCulture) ?? "0.00"
            });
        }

        var serializer = new XmlSerializer(typeof(ConfirmacionCargaXml));
        var ns = new XmlSerializerNamespaces();
        ns.Add("", "");
        var settings = new System.Xml.XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        using var sw = new StringWriter();
        using var writer = System.Xml.XmlWriter.Create(sw, settings);
        serializer.Serialize(writer, payload, ns);
        return sw.ToString();
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
