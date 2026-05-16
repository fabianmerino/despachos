using System.Xml.Serialization;

namespace Despachos.Api.Models;

[XmlRoot("ConfirmacionCarga")]
public sealed class ConfirmacionCargaXml
{
    [XmlElement("Header")]
    public ConfirmacionHeaderXml Header { get; set; } = null!;

    [XmlArray("Details")]
    [XmlArrayItem("Detail")]
    public List<ConfirmacionDetailXml> Details { get; set; } = new();
}

public sealed class ConfirmacionHeaderXml
{
    [XmlElement("NroTransporte")]
    public string NroTransporte { get; set; } = null!;
}

public sealed class ConfirmacionDetailXml
{
    [XmlElement("NroTransporte")]
    public string NroTransporte { get; set; } = null!;

    [XmlElement("NroEntrega")]
    public string NroEntrega { get; set; } = null!;

    [XmlElement("NroCompartimento")]
    public string NroCompartimento { get; set; } = null!;

    [XmlElement("Producto")]
    public string Producto { get; set; } = null!;

    [XmlElement("Temperatura")]
    public string Temperatura { get; set; } = null!;

    [XmlElement("APIDespachado")]
    public string APIDespachado { get; set; } = null!;

    [XmlElement("VolObservado")]
    public string VolObservado { get; set; } = null!;

    [XmlElement("UMVol")]
    public string UMVol { get; set; } = null!;

    [XmlElement("Vol60")]
    public string Vol60 { get; set; } = null!;
}
