using System.Text;
using Microsoft.EntityFrameworkCore;
using Despachos.Api.Data;
using Despachos.Api.Models;
using Despachos.Api.SoapInbound;

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
        MT_Planifica_Carga_Request request, CancellationToken ct)
    {
        if (request is null)
            return new ValidationErrors { new("Request", "Request nulo") };

        var errors = ValidarHeader(request);

        var items = (request.Detalle?.SelectMany(d => d ?? Array.Empty<DT_Planifica_Carga_DetItem>())
            ?? Enumerable.Empty<DT_Planifica_Carga_DetItem>()).ToList();

        if (items.Count == 0)
            errors.Add(new("Detalle", "Se requiere al menos un item en Detalle"));
        else
            errors.AddRange(ValidarDetails(items));

        if (errors.Count > 0)
            return errors;

        if (!DateTime.TryParseExact(request.I_FECHA_CARGA, "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.None, out var fechaCarga))
            return new ValidationErrors { new("I_FECHA_CARGA",
                $"Formato invalido: {request.I_FECHA_CARGA}. Esperado: yyyyMMdd") };

        var nroTransporte = request.I_NRO_TRANSPORTE!;

        var existing = await _db.DespachosHeaders
            .Include(h => h.Details)
            .FirstOrDefaultAsync(h => h.NroTransporte == nroTransporte, ct);

        if (existing is not null)
        {
            if (existing.Estado != EstadoDespacho.Pendiente)
                return new ValidationErrors { new("I_NRO_TRANSPORTE",
                    $"Orden {nroTransporte} en estado {existing.Estado}, no se puede modificar") };

            _db.DespachosHeaders.Remove(existing);
            _logger.LogInformation("Actualizando orden existente {NroTransporte}", nroTransporte);
        }

        var order = MapToEntity(request, fechaCarga, items);
        _db.DespachosHeaders.Add(order);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Orden {NroTransporte} guardada con {Count} compartimentos",
            nroTransporte, items.Count);

        return order;
    }

    private static ValidationErrors ValidarHeader(MT_Planifica_Carga_Request h)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(h.I_NRO_TRANSPORTE))
            errors.Add(new("I_NRO_TRANSPORTE", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.I_TERMINAL))
            errors.Add(new("I_TERMINAL", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.I_MAYORISTA))
            errors.Add(new("I_MAYORISTA", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.I_PLACA_VEH))
            errors.Add(new("I_PLACA_VEH", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.I_FECHA_CARGA))
            errors.Add(new("I_FECHA_CARGA", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.I_DNI))
            errors.Add(new("I_DNI", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.I_DESTINO))
            errors.Add(new("I_DESTINO", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.I_IND_VIAJE))
            errors.Add(new("I_IND_VIAJE", "Requerido"));
        if (string.IsNullOrWhiteSpace(h.I_BAY_QUEUE_PRIORITY))
            errors.Add(new("I_BAY_QUEUE_PRIORITY", "Requerido"));
        return errors;
    }

    private static ValidationErrors ValidarDetails(List<DT_Planifica_Carga_DetItem> details)
    {
        var errors = new ValidationErrors();
        var compartimentos = new HashSet<string>();

        for (int i = 0; i < details.Count; i++)
        {
            var d = details[i];
            var prefix = $"Detalle[{i}].";

            if (string.IsNullOrWhiteSpace(d.COMPARTIMENTO))
                errors.Add(new($"{prefix}COMPARTIMENTO", "Requerido"));
            else if (!compartimentos.Add(d.COMPARTIMENTO))
                errors.Add(new($"{prefix}COMPARTIMENTO", $"Duplicado: {d.COMPARTIMENTO}"));

            if (string.IsNullOrWhiteSpace(d.NRO_ENTREGA))
                errors.Add(new($"{prefix}NRO_ENTREGA", "Requerido"));
            if (string.IsNullOrWhiteSpace(d.PROD_COMER))
                errors.Add(new($"{prefix}PROD_COMER", "Requerido"));
            if (string.IsNullOrWhiteSpace(d.UMVOL))
                errors.Add(new($"{prefix}UMVOL", "Requerido"));

            if (!decimal.TryParse(d.VOLUMEN,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var vol) || vol <= 0)
                errors.Add(new($"{prefix}VOLUMEN", $"Debe ser mayor a 0: {d.VOLUMEN}"));
        }
        return errors;
    }

    private static DespachoHeader MapToEntity(MT_Planifica_Carga_Request h, DateTime fechaCarga,
        List<DT_Planifica_Carga_DetItem> details)
    {
        var header = new DespachoHeader
        {
            NroTransporte = h.I_NRO_TRANSPORTE!,
            Terminal = h.I_TERMINAL!,
            Mayorista = h.I_MAYORISTA!,
            PlacaVeh = h.I_PLACA_VEH!,
            FechaCarga = fechaCarga,
            DNI = h.I_DNI!,
            Destino = h.I_DESTINO!,
            IndViaje = h.I_IND_VIAJE!,
            BayQueuePriority = h.I_BAY_QUEUE_PRIORITY!,
            Estado = EstadoDespacho.Pendiente,
            CreadoEn = DateTime.UtcNow
        };

        foreach (var d in details)
        {
            decimal.TryParse(d.VOLUMEN,
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
                NroTransporte = h.I_NRO_TRANSPORTE!,
                NroEntrega = d.NRO_ENTREGA!,
                CustomerCode = d.CUSTOMER_CODE,
                Destinatario = d.DESTINATARIO,
                SCOP = d.SCOP,
                NroCompartimento = d.COMPARTIMENTO!,
                Producto = d.PROD_COMER!,
                Volumen = volumen,
                UMVol = d.UMVOL!,
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
