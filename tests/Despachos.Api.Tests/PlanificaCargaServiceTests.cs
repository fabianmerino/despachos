using Despachos.Api.SoapInbound;

namespace Despachos.Api.Tests;

public class PlanificaCargaServiceTests
{
    [Fact]
    public async Task SIS_Planifica_Carga_RequestValido_RetornaTypeS()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var despacho = TestFactory.CreateDespachoService(db);
        var sut = TestFactory.CreatePlanificaCargaService(despacho);

        var request = new SIS_Planifica_CargaRequest(TestFactory.BuildValidRequest("000SVC001"));

        var response = await sut.SIS_Planifica_Carga(request);

        Assert.Equal("S", response.MT_Planifica_Carga_Response!.Return.TYPE);
        Assert.Contains("000SVC001", response.MT_Planifica_Carga_Response!.Return.MESSAGE);
    }

    [Fact]
    public async Task SIS_Planifica_Carga_RequestConErroresValidacion_RetornaTypeE()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var despacho = TestFactory.CreateDespachoService(db);
        var sut = TestFactory.CreatePlanificaCargaService(despacho);

        var req = TestFactory.BuildValidRequest("000SVC002");
        req.I_NRO_TRANSPORTE = "";
        req.I_DNI = "";

        var response = await sut.SIS_Planifica_Carga(new SIS_Planifica_CargaRequest(req));

        Assert.Equal("E", response.MT_Planifica_Carga_Response!.Return.TYPE);
        Assert.Contains("I_NRO_TRANSPORTE", response.MT_Planifica_Carga_Response!.Return.MESSAGE);
        Assert.Contains("I_DNI", response.MT_Planifica_Carga_Response!.Return.MESSAGE);
    }

    [Fact]
    public async Task SIS_Planifica_Carga_OrdenExistenteNoPendiente_RetornaTypeE()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var despacho = TestFactory.CreateDespachoService(db);
        var sut = TestFactory.CreatePlanificaCargaService(despacho);

        await sut.SIS_Planifica_Carga(
            new SIS_Planifica_CargaRequest(TestFactory.BuildValidRequest("000SVC003")));

        var existing = await db.DespachosHeaders.FindAsync("000SVC003");
        existing!.Estado = "Completado";
        await db.SaveChangesAsync();

        var response = await sut.SIS_Planifica_Carga(
            new SIS_Planifica_CargaRequest(TestFactory.BuildValidRequest("000SVC003")));

        Assert.Equal("E", response.MT_Planifica_Carga_Response!.Return.TYPE);
        Assert.Contains("no se puede modificar", response.MT_Planifica_Carga_Response!.Return.MESSAGE);
    }

    [Fact]
    public async Task SIS_Planifica_Carga_RequestNulo_RetornaTypeE()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var despacho = TestFactory.CreateDespachoService(db);
        var sut = TestFactory.CreatePlanificaCargaService(despacho);

        var response = await sut.SIS_Planifica_Carga(null!);

        Assert.Equal("E", response.MT_Planifica_Carga_Response!.Return.TYPE);
    }

    [Fact]
    public async Task SIS_Planifica_Carga_RequestConInnerNulo_RetornaTypeE()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var despacho = TestFactory.CreateDespachoService(db);
        var sut = TestFactory.CreatePlanificaCargaService(despacho);

        var response = await sut.SIS_Planifica_Carga(new SIS_Planifica_CargaRequest());

        Assert.Equal("E", response.MT_Planifica_Carga_Response!.Return.TYPE);
    }

    [Fact]
    public async Task SIS_Planifica_Carga_RequestValido_PersisteEnBd()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var despacho = TestFactory.CreateDespachoService(db);
        var sut = TestFactory.CreatePlanificaCargaService(despacho);

        await sut.SIS_Planifica_Carga(
            new SIS_Planifica_CargaRequest(TestFactory.BuildValidRequest("000SVC004",
                ("C1", "100", "ENT1"), ("C2", "200", "ENT2"))));

        var fromDb = await db.DespachosHeaders.FindAsync("000SVC004");
        Assert.NotNull(fromDb);
        Assert.Equal("Pendiente", fromDb!.Estado);
        Assert.Equal(2, fromDb.Details.Count);
    }
}
