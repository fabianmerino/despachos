namespace Despachos.Api.Models;

public sealed class OutboxConfirmacion
{
    public int Id { get; set; }
    public string NroTransporte { get; set; } = null!;
    public string Payload { get; set; } = null!;
    public int Reintentos { get; set; }
    public int MaxReintentos { get; set; } = 3;
    public string Estado { get; set; } = OutboxEstado.Pendiente;
    public DateTime CreadoEn { get; set; } = DateTime.UtcNow;
    public DateTime? UltimoIntentoEn { get; set; }
}

public static class OutboxEstado
{
    public const string Pendiente = "Pendiente";
    public const string Enviado = "Enviado";
    public const string Error = "Error";
}
