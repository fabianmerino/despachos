namespace Despachos.Api.Models;

public sealed class DespachoDetail
{
    public int Id { get; set; }
    public string NroTransporte { get; set; } = null!;
    public string NroEntrega { get; set; } = null!;
    public string? CustomerCode { get; set; }
    public string? Destinatario { get; set; }
    public string? SCOP { get; set; }
    public string NroCompartimento { get; set; } = null!;
    public string Producto { get; set; } = null!;
    public decimal Volumen { get; set; }
    public string UMVol { get; set; } = null!;
    public decimal? API { get; set; }
    public string Estado { get; set; } = EstadoDespacho.Pendiente;

    public DespachoHeader Header { get; set; } = null!;
}
