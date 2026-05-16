namespace Despachos.Api.Models;

public sealed class ConfirmacionDespacho
{
    public string NroTransporte { get; set; } = null!;
    public string NroCompartimento { get; set; } = null!;
    public decimal? Temperatura { get; set; }
    public decimal? APIDespachado { get; set; }
    public decimal? VolObservado { get; set; }
    public decimal? Vol60 { get; set; }
    public DateTime FechaCompletado { get; set; }
}
