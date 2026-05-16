using System.Xml.Serialization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Despachos.Api.Data;
using Despachos.Api.Models;

namespace Despachos.Api.Services;

public sealed class DespachoService
{
    private readonly DespachosDbContext _db;
    private readonly ILogger<DespachoService> _logger;

    public DespachoService(DespachosDbContext db, ILogger<DespachoService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Either<ValidationErrors, DespachoHeader>> ProcesarPlanificacionAsync(
        string xmlBody, CancellationToken ct)
    {
        PlanificacionCargaXml planificacion;
        try
        {
            var serializer = new XmlSerializer(typeof(PlanificacionCargaXml));
            using var reader = new StringReader(xmlBody);
            planificacion = (PlanificacionCargaXml)serializer.Deserialize(reader)!;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "XML mal formado en planificacion");
            return new ValidationErrors { new("XML", "XML mal formado o estructura invalida") };
        }

        var header = planificacion.Header;
        if (header is null)
            return new ValidationErrors { new("Header", "Header es obligatorio") };

        var errors = ValidarHeader(header);
        if (planificacion.Details is null || planificacion.Details.Count == 0)
            errors.Add(new("Details", "Se requiere al menos un Detail"));
        else
            errors.AddRange(ValidarDetails(planificacion.Details));

        if (errors.Count > 0)
            return errors;

        if (!DateTime.TryParseExact(header.FechaCarga, "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.None, out var fechaCarga))
            return new ValidationErrors { new("FechaCarga", $"Formato invalido: {header.FechaCarga}. Esperado: yyyyMMdd") };

        var existing = await _db.DespachosHeaders
            .Include(h => h.Details)
            .FirstOrDefaultAsync(h => h.NroTransporte == header.NroTransporte, ct);

        if (existing is not null)
        {
            if (existing.Estado != EstadoDespacho.Pendiente)
                return new ValidationErrors { new("NroTransporte",
                    $"Orden {header.NroTransporte} en estado {existing.Estado}, no se puede modificar") };

            _db.DespachosHeaders.Remove(existing);
            _logger.LogInformation("Actualizando orden existente {NroTransporte}", header.NroTransporte);
        }

        var order = MapToEntity(header, fechaCarga, planificacion.Details!);
        _db.DespachosHeaders.Add(order);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Orden {NroTransporte} guardada con {Count} compartimentos",
            header.NroTransporte, planificacion.Details!.Count);

        return order;
    }

    private static ValidationErrors ValidarHeader(PlanificacionHeaderXml h)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(h.NroTransporte))
            errors.Add(new("NroTransporte", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.Terminal))
            errors.Add(new("Terminal", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.Mayorista))
            errors.Add(new("Mayorista", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.PlacaVeh))
            errors.Add(new("PlacaVeh", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.FechaCarga))
            errors.Add(new("FechaCarga", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.DNI))
            errors.Add(new("DNI", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.Destino))
            errors.Add(new("Destino", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.IndViaje))
            errors.Add(new("IndViaje", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.BayQueuePriority))
            errors.Add(new("BayQueuePriority", "Requerido"));
        return errors;
    }

    private static ValidationErrors ValidarDetails(List<PlanificacionDetailXml> details)
    {
        var errors = new ValidationErrors();
        var compartimentos = new HashSet<string>();

        for (int i = 0; i < details.Count; i++)
        {
            var d = details[i];
            var prefix = $"Detail[{i}].";

            if (string.IsNullOrWhiteSpace(d.NroCompartimento))
                errors.Add(new($"{prefix}NroCompartimento", "Requerido"));
            else if (!compartimentos.Add(d.NroCompartimento))
                errors.Add(new($"{prefix}NroCompartimento", $"Duplicado: {d.NroCompartimento}"));

            if (string.IsNullOrWhiteSpace(d.NroEntrega))
                errors.Add(new($"{prefix}NroEntrega", "Requerido"));
            if (string.IsNullOrWhiteSpace(d.Producto))
                errors.Add(new($"{prefix}Producto", "Requerido"));
            if (string.IsNullOrWhiteSpace(d.UMVol))
                errors.Add(new($"{prefix}UMVol", "Requerido"));

            if (!decimal.TryParse(d.Volumen,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var vol) || vol <= 0)
                errors.Add(new($"{prefix}Volumen", $"Debe ser mayor a 0: {d.Volumen}"));
        }
        return errors;
    }

    private static DespachoHeader MapToEntity(PlanificacionHeaderXml h, DateTime fechaCarga,
        List<PlanificacionDetailXml> details)
    {
        var header = new DespachoHeader
        {
            NroTransporte = h.NroTransporte,
            Terminal = h.Terminal,
            Mayorista = h.Mayorista,
            PlacaVeh = h.PlacaVeh,
            FechaCarga = fechaCarga,
            DNI = h.DNI,
            Destino = h.Destino,
            IndViaje = h.IndViaje,
            BayQueuePriority = h.BayQueuePriority,
            Estado = EstadoDespacho.Pendiente,
            CreadoEn = DateTime.UtcNow
        };

        foreach (var d in details)
        {
            decimal.TryParse(d.Volumen,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var volumen);

            decimal? api = null;
            if (decimal.TryParse(d.API,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var apiVal))
                api = apiVal;

            header.Details.Add(new DespachoDetail
            {
                NroTransporte = h.NroTransporte,
                NroEntrega = d.NroEntrega,
                CustomerCode = d.CustomerCode,
                Destinatario = d.Destinatario,
                SCOP = d.SCOP,
                NroCompartimento = d.NroCompartimento,
                Producto = d.Producto,
                Volumen = volumen,
                UMVol = d.UMVol,
                API = api,
                Estado = EstadoDespacho.Pendiente
            });
        }

        return header;
    }

    public async Task<Either<ValidationErrors, DespachoHeader>> CancelarOrdenAsync(
        string nroTransporte, CancellationToken ct)
    {
        var order = await _db.DespachosHeaders.FindAsync([nroTransporte], ct);
        if (order is null)
            return new ValidationErrors { new("NroTransporte",
                $"Orden {nroTransporte} no encontrada") };

        if (order.Estado != EstadoDespacho.Pendiente)
            return new ValidationErrors { new("NroTransporte",
                $"No se puede cancelar orden en estado {order.Estado}") };

        order.Estado = EstadoDespacho.Cancelado;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Orden {NroTransporte} cancelada", nroTransporte);
        return order;
    }
}

public sealed class ValidationErrors : List<ValidationError>
{
    public string ToXml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<ErrorResponse>");
        sb.AppendLine("  <Status>ERROR</Status>");
        sb.AppendLine("  <Messages>");
        foreach (var err in this)
            sb.AppendLine($"    <Message Field=\"{System.Security.SecurityElement.Escape(err.Field)}\">{System.Security.SecurityElement.Escape(err.Message)}</Message>");
        sb.AppendLine("  </Messages>");
        sb.AppendLine("</ErrorResponse>");
        return sb.ToString();
    }
}

public sealed record ValidationError(string Field, string Message);

public sealed class Either<TLeft, TRight>
{
    public TLeft? Left { get; }
    public TRight? Right { get; }
    public bool IsLeft { get; }

    private Either(TLeft left) { Left = left; IsLeft = true; }
    private Either(TRight right) { Right = right; IsLeft = false; }

    public static implicit operator Either<TLeft, TRight>(TLeft left) => new(left);
    public static implicit operator Either<TLeft, TRight>(TRight right) => new(right);

    public TResult Match<TResult>(Func<TLeft, TResult> leftFn, Func<TRight, TResult> rightFn)
        => IsLeft ? leftFn(Left!) : rightFn(Right!);
}
