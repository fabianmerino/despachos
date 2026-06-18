using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Despachos.Api.Data;
using Despachos.Api.Models;
using Despachos.Api.Services;
using Despachos.Api.SoapInbound;

namespace Despachos.Api.Tests;

internal static class TestFactory
{
    public static DespachosDbContext CreateInMemoryDb(string? name = null)
    {
        var dbId = name ?? $"despachos-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<DespachosDbContext>()
            .UseInMemoryDatabase(dbId)
            .Options;
        var db = new DespachosDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    public static DespachoService CreateDespachoService(DespachosDbContext db) =>
        new(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<DespachoService>.Instance);

    public static ConfirmacionService CreateConfirmacionService(DespachosDbContext db) =>
        new(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfirmacionService>.Instance);

    public static PlanificaCargaService CreatePlanificaCargaService(DespachoService despacho) =>
        new(despacho, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlanificaCargaService>.Instance);

    public static MT_Planifica_Carga_Request BuildValidRequest(string nroTransporte = "0001234567",
        params (string compartimento, string volumen, string entrega)[] compartimentos)
    {
        (string compartimento, string volumen, string entrega)[] items =
            compartimentos.Length == 0
                ? new[] { (compartimento: "C1", volumen: "100.50", entrega: "ENT001") }
                : compartimentos;

        var dtItems = items
            .Select(t => new DT_Planifica_Carga_DetItem
            {
                NRO_TRANS = nroTransporte,
                NRO_ENTREGA = t.entrega,
                COMPARTIMENTO = t.compartimento,
                PROD_COMER = "G90",
                VOLUMEN = t.volumen,
                UMVOL = "GL",
                CUSTOMER_CODE = "CC001",
                DESTINATARIO = "Dest",
                SCOP = "SCOP1",
                API = "59.5000"
            }).ToArray();

        return new MT_Planifica_Carga_Request
        {
            I_NRO_TRANSPORTE = nroTransporte,
            I_TERMINAL = "T001",
            I_MAYORISTA = "M001",
            I_PLACA_VEH = "ABC123",
            I_FECHA_CARGA = "20260618",
            I_DNI = "12345678",
            I_DESTINO = "DEST1",
            I_IND_VIAJE = "1",
            I_BAY_QUEUE_PRIORITY = "NORMAL",
            Detalle = new[] { dtItems }
        };
    }
}
