using Despachos.Api.Models;
using Despachos.Api.Services;

namespace Despachos.Api.Tests;

public class ConfirmacionServiceTests
{
    [Fact]
    public void ArmarRequest_MapeaNroTransporte_AI_NRO_TRANSPORTE()
    {
        var (header, _) = BuildHeaderAndConf("0007777777");

        var request = ConfirmacionService.ArmarRequestConfirmacion(header, new List<ConfirmacionDespacho>());

        Assert.Equal("0007777777", request.MT_Confirma_Carga_Request!.I_NRO_TRANSPORTE);
    }

    [Fact]
    public void ArmarRequest_MapeaCamposDelCompartimento()
    {
        var (header, conf) = BuildHeaderAndConf("0008888888");
        header.Details.First().NroCompartimento = "C5";
        header.Details.First().NroEntrega = "ENT777";
        header.Details.First().Producto = "DIESEL";
        header.Details.First().UMVol = "GL";
        conf[0].NroCompartimento = "C5";
        conf[0].Temperatura = 23.45m;
        conf[0].APIDespachado = 59.5000m;
        conf[0].VolObservado = 100.50m;
        conf[0].Vol60 = 98.75m;

        var request = ConfirmacionService.ArmarRequestConfirmacion(header, conf);
        var item = request.MT_Confirma_Carga_Request!.Detalle!.SelectMany(d => d).Single();

        Assert.Equal("0008888888", item.NRO_TRANS);
        Assert.Equal("ENT777", item.NRO_ENTREGA);
        Assert.Equal("C5", item.COMPARTIMENTO);
        Assert.Equal("DIESEL", item.PROD_COMER);
        Assert.Equal("GL", item.UMVOL);
        Assert.Equal("23.45", item.T_DESPACHO);
        Assert.Equal("59.5000", item.API_DESPACHO);
        Assert.Equal("100.50", item.VOL_DESPA_OBS);
        Assert.Equal("98.75", item.VOL_DESPA_60);
    }

    [Fact]
    public void ArmarRequest_MapeaMultiplesCompartimentos()
    {
        var header = new DespachoHeader
        {
            NroTransporte = "000MULTI01",
            Terminal = "T",
            Mayorista = "M",
            PlacaVeh = "P",
            FechaCarga = DateTime.Today,
            DNI = "12345678",
            Destino = "D",
            IndViaje = "1",
            BayQueuePriority = "N",
            Details = new List<DespachoDetail>
            {
                new() { NroCompartimento = "C1", NroEntrega = "E1", Producto = "G90", UMVol = "GL" },
                new() { NroCompartimento = "C2", NroEntrega = "E2", Producto = "DIE", UMVol = "GL" }
            }
        };
        var confs = new List<ConfirmacionDespacho>
        {
            new() { NroTransporte = "000MULTI01", NroCompartimento = "C1", Temperatura = 20m, APIDespachado = 60m, VolObservado = 100m, Vol60 = 99m },
            new() { NroTransporte = "000MULTI01", NroCompartimento = "C2", Temperatura = 21m, APIDespachado = 61m, VolObservado = 200m, Vol60 = 198m }
        };

        var request = ConfirmacionService.ArmarRequestConfirmacion(header, confs);
        var items = request.MT_Confirma_Carga_Request!.Detalle!.SelectMany(d => d).ToList();

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.COMPARTIMENTO == "C1" && i.PROD_COMER == "G90");
        Assert.Contains(items, i => i.COMPARTIMENTO == "C2" && i.PROD_COMER == "DIE");
    }

    [Fact]
    public void ArmarRequest_ValoresNulos_UsanCadenaVacia()
    {
        var header = new DespachoHeader
        {
            NroTransporte = "000NULL01",
            Terminal = "T",
            Mayorista = "M",
            PlacaVeh = "P",
            FechaCarga = DateTime.Today,
            DNI = "12345678",
            Destino = "D",
            IndViaje = "1",
            BayQueuePriority = "N",
            Details = new List<DespachoDetail>
            {
                new() { NroCompartimento = "C1" }
            }
        };
        var confs = new List<ConfirmacionDespacho>
        {
            new() { NroTransporte = "000NULL01", NroCompartimento = "C1", Temperatura = null, APIDespachado = null, VolObservado = null, Vol60 = null }
        };

        var request = ConfirmacionService.ArmarRequestConfirmacion(header, confs);
        var item = request.MT_Confirma_Carga_Request!.Detalle!.SelectMany(d => d).Single();

        Assert.Equal("", item.T_DESPACHO);
        Assert.Equal("", item.API_DESPACHO);
        Assert.Equal("", item.VOL_DESPA_OBS);
        Assert.Equal("", item.VOL_DESPA_60);
        Assert.Equal("", item.NRO_ENTREGA);
        Assert.Equal("", item.PROD_COMER);
        Assert.Equal("", item.UMVOL);
    }

    [Fact]
    public async Task ConstruirRequestAsync_DesdeDb_RetornaRequestTipado()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var header = new DespachoHeader
        {
            NroTransporte = "000DB001",
            Terminal = "T",
            Mayorista = "M",
            PlacaVeh = "P",
            FechaCarga = DateTime.Today,
            DNI = "12345678",
            Destino = "D",
            IndViaje = "1",
            BayQueuePriority = "N",
            Details = new List<DespachoDetail>
            {
                new() { NroTransporte = "000DB001", NroCompartimento = "C1", NroEntrega = "E1", Producto = "G90", UMVol = "GL" }
            }
        };
        db.DespachosHeaders.Add(header);
        db.ConfirmacionesDespacho.Add(new ConfirmacionDespacho
        {
            NroTransporte = "000DB001",
            NroCompartimento = "C1",
            Temperatura = 20m,
            APIDespachado = 60m,
            VolObservado = 100m,
            Vol60 = 99m,
            FechaCompletado = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = TestFactory.CreateConfirmacionService(db);

        var request = await service.ConstruirRequestAsync("000DB001", CancellationToken.None);

        Assert.NotNull(request);
        Assert.Equal("000DB001", request!.MT_Confirma_Carga_Request!.I_NRO_TRANSPORTE);
        var item = request.MT_Confirma_Carga_Request!.Detalle!.SelectMany(d => d).Single();
        Assert.Equal("E1", item.NRO_ENTREGA);
        Assert.Equal("G90", item.PROD_COMER);
        Assert.Equal("20.00", item.T_DESPACHO);
    }

    [Fact]
    public async Task ConstruirRequestAsync_NoExisteDespacho_RetornaNull()
    {
        await using var db = TestFactory.CreateInMemoryDb();
        var service = TestFactory.CreateConfirmacionService(db);

        var request = await service.ConstruirRequestAsync("NOEXISTE", CancellationToken.None);

        Assert.Null(request);
    }

    private static (DespachoHeader header, List<ConfirmacionDespacho> conf) BuildHeaderAndConf(string nro)
    {
        var header = new DespachoHeader
        {
            NroTransporte = nro,
            Terminal = "T",
            Mayorista = "M",
            PlacaVeh = "P",
            FechaCarga = DateTime.Today,
            DNI = "12345678",
            Destino = "D",
            IndViaje = "1",
            BayQueuePriority = "N",
            Details = new List<DespachoDetail>
            {
                new() { NroCompartimento = "C1", NroEntrega = "E1", Producto = "G90", UMVol = "GL" }
            }
        };
        var conf = new List<ConfirmacionDespacho>
        {
            new() { NroTransporte = nro, NroCompartimento = "C1", Temperatura = 20m, APIDespachado = 60m, VolObservado = 100m, Vol60 = 99m }
        };
        return (header, conf);
    }
}
