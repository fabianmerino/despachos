namespace Despachos.Api.Models;

public sealed class DespachoHeader
{
    public string NroTransporte { get; set; } = null!;
    public string Terminal { get; set; } = null!;
    public string Mayorista { get; set; } = null!;
    public string PlacaVeh { get; set; } = null!;
    public DateTime FechaCarga { get; set; }
    public string DNI { get; set; } = null!;
    public string Destino { get; set; } = null!;
    public string IndViaje { get; set; } = null!;
    public string BayQueuePriority { get; set; } = null!;
    public string Estado { get; set; } = EstadoDespacho.Pendiente;
    public DateTime CreadoEn { get; set; } = DateTime.UtcNow;

    public ICollection<DespachoDetail> Details { get; set; } = new List<DespachoDetail>();
}

public static class EstadoDespacho
{
    public const string Pendiente = "Pendiente";
    public const string EnProceso = "EnProceso";
    public const string Completado = "Completado";
    public const string Confirmado = "Confirmado";
    public const string Cancelado = "Cancelado";
    public const string Error = "Error";
}
