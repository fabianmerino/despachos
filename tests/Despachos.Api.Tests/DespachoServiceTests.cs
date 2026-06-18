using Microsoft.EntityFrameworkCore;
using Despachos.Api.Models;
using Despachos.Api.Services;
using Despachos.Api.SoapInbound;

namespace Despachos.Api.Tests;

public class DespachoServiceTests
{
    [Fact]
    public async Task ProcesarPlanificacion_ConRequestValido_GuardaEnEstadoPendiente()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var service = TestFactory.CreateDespachoService(db);
        var request = TestFactory.BuildValidRequest("0001234567");

        var result = await service.ProcesarPlanificacionAsync(request, CancellationToken.None);

        Assert.False(result.IsLeft);
        var order = result.Right!;
        Assert.Equal("0001234567", order.NroTransporte);
        Assert.Equal(EstadoDespacho.Pendiente, order.Estado);
        Assert.Single(order.Details);

        var fromDb = await db.DespachosHeaders.Include(h => h.Details)
            .FirstAsync(h => h.NroTransporte == "0001234567");
        Assert.Equal(EstadoDespacho.Pendiente, fromDb.Estado);
        Assert.Single(fromDb.Details);
        Assert.Equal("ENT001", fromDb.Details.First().NroEntrega);
    }

    [Fact]
    public async Task ProcesarPlanificacion_MapeaCamposHeaderYDetail()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var service = TestFactory.CreateDespachoService(db);
        var request = TestFactory.BuildValidRequest("0009999999",
            ("C1", "150.25", "ENT10"), ("C2", "200.00", "ENT11"));

        var result = await service.ProcesarPlanificacionAsync(request, CancellationToken.None);

        var order = result.Right!;
        Assert.Equal("T001", order.Terminal);
        Assert.Equal("M001", order.Mayorista);
        Assert.Equal("ABC123", order.PlacaVeh);
        Assert.Equal("DEST1", order.Destino);
        Assert.Equal("1", order.IndViaje);
        Assert.Equal("NORMAL", order.BayQueuePriority);
        Assert.Equal(new DateTime(2026, 6, 18), order.FechaCarga);

        var d1 = order.Details.Single(d => d.NroCompartimento == "C1");
        Assert.Equal("ENT10", d1.NroEntrega);
        Assert.Equal("G90", d1.Producto);
        Assert.Equal("GL", d1.UMVol);
        Assert.Equal(150.25m, d1.Volumen);
        Assert.Equal(59.5000m, d1.API);
        Assert.Equal("CC001", d1.CustomerCode);
        Assert.Equal("Dest", d1.Destinatario);
        Assert.Equal("SCOP1", d1.SCOP);
    }

    [Fact]
    public async Task ProcesarPlanificacion_FaltaCampoHeader_Requerido_DevuelveErrores()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var service = TestFactory.CreateDespachoService(db);
        var request = TestFactory.BuildValidRequest();
        request.I_NRO_TRANSPORTE = "";
        request.I_PLACA_VEH = "";
        request.I_DNI = "";

        var result = await service.ProcesarPlanificacionAsync(request, CancellationToken.None);

        Assert.True(result.IsLeft);
        var errors = result.Left!;
        Assert.Contains(errors, e => e.Field == "I_NRO_TRANSPORTE");
        Assert.Contains(errors, e => e.Field == "I_PLACA_VEH");
        Assert.Contains(errors, e => e.Field == "I_DNI");
    }

    [Fact]
    public async Task ProcesarPlanificacion_VolumenCeroOInvalido_DevuelveError()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var service = TestFactory.CreateDespachoService(db);
        var request = TestFactory.BuildValidRequest("000VOL01",
            ("C1", "0", "ENT001"));

        var result = await service.ProcesarPlanificacionAsync(request, CancellationToken.None);

        Assert.True(result.IsLeft);
        Assert.Contains(result.Left!, e => e.Field == "Detalle[0].VOLUMEN");
    }

    [Fact]
    public async Task ProcesarPlanificacion_VolumenNegativo_DevuelveError()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var service = TestFactory.CreateDespachoService(db);
        var request = TestFactory.BuildValidRequest("000VOL02",
            ("C1", "-5", "ENT001"));

        var result = await service.ProcesarPlanificacionAsync(request, CancellationToken.None);

        Assert.True(result.IsLeft);
        Assert.Contains(result.Left!, e => e.Field == "Detalle[0].VOLUMEN");
    }

    [Fact]
    public async Task ProcesarPlanificacion_CompartimentoDuplicado_DevuelveError()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var service = TestFactory.CreateDespachoService(db);
        var request = TestFactory.BuildValidRequest("000DUP01",
            ("C1", "100", "ENT001"), ("C1", "200", "ENT002"));

        var result = await service.ProcesarPlanificacionAsync(request, CancellationToken.None);

        Assert.True(result.IsLeft);
        Assert.Contains(result.Left!, e => e.Field == "Detalle[1].COMPARTIMENTO" && e.Message.Contains("Duplicado"));
    }

    [Fact]
    public async Task ProcesarPlanificacion_SinDetalle_DevuelveError()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var service = TestFactory.CreateDespachoService(db);
        var request = TestFactory.BuildValidRequest();
        request.Detalle = Array.Empty<DT_Planifica_Carga_DetItem[]>();

        var result = await service.ProcesarPlanificacionAsync(request, CancellationToken.None);

        Assert.True(result.IsLeft);
        Assert.Contains(result.Left!, e => e.Field == "Detalle");
    }

    [Fact]
    public async Task ProcesarPlanificacion_FechaCargaFormatoInvalido_DevuelveError()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var service = TestFactory.CreateDespachoService(db);
        var request = TestFactory.BuildValidRequest();
        request.I_FECHA_CARGA = "18/06/2026";

        var result = await service.ProcesarPlanificacionAsync(request, CancellationToken.None);

        Assert.True(result.IsLeft);
        Assert.Contains(result.Left!, e => e.Field == "I_FECHA_CARGA");
    }

    [Fact]
    public async Task ProcesarPlanificacion_OrdenExistentePendiente_Actualiza()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var service = TestFactory.CreateDespachoService(db);

        var first = TestFactory.BuildValidRequest("000UPD01", ("C1", "100", "ENT001"));
        await service.ProcesarPlanificacionAsync(first, CancellationToken.None);

        var updated = TestFactory.BuildValidRequest("000UPD01",
            ("C1", "150", "ENT001"), ("C2", "200", "ENT002"));
        var result = await service.ProcesarPlanificacionAsync(updated, CancellationToken.None);

        Assert.False(result.IsLeft);
        var fromDb = await db.DespachosHeaders.Include(h => h.Details)
            .FirstAsync(h => h.NroTransporte == "000UPD01");
        Assert.Equal(2, fromDb.Details.Count);
        Assert.Equal(150m, fromDb.Details.Single(d => d.NroCompartimento == "C1").Volumen);
    }

    [Fact]
    public async Task ProcesarPlanificacion_OrdenExistenteNoPendiente_DevuelveConflicto()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var service = TestFactory.CreateDespachoService(db);

        var first = TestFactory.BuildValidRequest("000CFL01");
        await service.ProcesarPlanificacionAsync(first, CancellationToken.None);

        var existing = await db.DespachosHeaders.FirstAsync(h => h.NroTransporte == "000CFL01");
        existing.Estado = EstadoDespacho.Completado;
        await db.SaveChangesAsync();

        var duplicate = TestFactory.BuildValidRequest("000CFL01");
        var result = await service.ProcesarPlanificacionAsync(duplicate, CancellationToken.None);

        Assert.True(result.IsLeft);
        Assert.Contains(result.Left!, e => e.Field == "I_NRO_TRANSPORTE" && e.Message.Contains("no se puede modificar"));
    }

    [Fact]
    public async Task ProcesarPlanificacion_RequestNulo_DevuelveError()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var service = TestFactory.CreateDespachoService(db);

        var result = await service.ProcesarPlanificacionAsync(null!, CancellationToken.None);

        Assert.True(result.IsLeft);
        Assert.Contains(result.Left!, e => e.Field == "Request");
    }

    [Fact]
    public async Task CancelarOrden_EnEstadoPendiente_PasaACancelado()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var service = TestFactory.CreateDespachoService(db);
        await service.ProcesarPlanificacionAsync(
            TestFactory.BuildValidRequest("000CAN01"), CancellationToken.None);

        var result = await service.CancelarOrdenAsync("000CAN01", CancellationToken.None);

        Assert.False(result.IsLeft);
        Assert.Equal(EstadoDespacho.Cancelado, result.Right!.Estado);
        var fromDb = await db.DespachosHeaders.FirstAsync(h => h.NroTransporte == "000CAN01");
        Assert.Equal(EstadoDespacho.Cancelado, fromDb.Estado);
    }

    [Fact]
    public async Task CancelarOrden_NoExistente_DevuelveError()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var service = TestFactory.CreateDespachoService(db);

        var result = await service.CancelarOrdenAsync("NOEXISTE", CancellationToken.None);

        Assert.True(result.IsLeft);
        Assert.Contains(result.Left!, e => e.Field == "NroTransporte" && e.Message.Contains("no encontrada"));
    }

    [Fact]
    public async Task CancelarOrden_EnEstadoCompletado_DevuelveError()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var service = TestFactory.CreateDespachoService(db);
        await service.ProcesarPlanificacionAsync(
            TestFactory.BuildValidRequest("000CAN02"), CancellationToken.None);

        var existing = await db.DespachosHeaders.FirstAsync(h => h.NroTransporte == "000CAN02");
        existing.Estado = EstadoDespacho.Completado;
        await db.SaveChangesAsync();

        var result = await service.CancelarOrdenAsync("000CAN02", CancellationToken.None);

        Assert.True(result.IsLeft);
        Assert.Contains(result.Left!, e => e.Message.Contains("no se puede cancelar", StringComparison.OrdinalIgnoreCase));
    }
}
