using System.Xml.Serialization;

namespace Despachos.Api.Models;

[XmlRoot("PlanificacionCarga")]
public sealed class PlanificacionCargaXml
{
    [XmlElement("Header")]
    public PlanificacionHeaderXml Header { get; set; } = null!;

    [XmlArray("Details")]
    [XmlArrayItem("Detail")]
    public List<PlanificacionDetailXml> Details { get; set; } = new();
}

public sealed class PlanificacionHeaderXml
{
    [XmlElement("NroTransporte")]
    public string NroTransporte { get; set; } = null!;

    [XmlElement("Terminal")]
    public string Terminal { get; set; } = null!;

    [XmlElement("Mayorista")]
    public string Mayorista { get; set; } = null!;

    [XmlElement("PlacaVeh")]
    public string PlacaVeh { get; set; } = null!;

    [XmlElement("FechaCarga")]
    public string FechaCarga { get; set; } = null!;

    [XmlElement("DNI")]
    public string DNI { get; set; } = null!;

    [XmlElement("Destino")]
    public string Destino { get; set; } = null!;

    [XmlElement("IndViaje")]
    public string IndViaje { get; set; } = null!;

    [XmlElement("BayQueuePriority")]
    public string BayQueuePriority { get; set; } = null!;
}

public sealed class PlanificacionDetailXml
{
    [XmlElement("NroTransporte")]
    public string NroTransporte { get; set; } = null!;

    [XmlElement("NroEntrega")]
    public string NroEntrega { get; set; } = null!;

    [XmlElement("CustomerCode")]
    public string? CustomerCode { get; set; }

    [XmlElement("Destinatario")]
    public string? Destinatario { get; set; }

    [XmlElement("SCOP")]
    public string? SCOP { get; set; }

    [XmlElement("NroCompartimento")]
    public string NroCompartimento { get; set; } = null!;

    [XmlElement("Producto")]
    public string Producto { get; set; } = null!;

    [XmlElement("Volumen")]
    public string Volumen { get; set; } = null!;

    [XmlElement("UMVol")]
    public string UMVol { get; set; } = null!;

    [XmlElement("API")]
    public string? API { get; set; }
}
