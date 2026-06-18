using System.Runtime.Serialization;
using System.ServiceModel;
using System.Xml.Serialization;

namespace Despachos.Api.SoapInbound;

[ServiceContract(Namespace = "urn:petroperu.com.pe:pmerp:tas:Planifica_Carga", ConfigurationName = "SIS_Planifica_Carga")]
public interface IPlanificaCargaService
{
    [OperationContract(Action = "http://sap.com/xi/WebService/soap1.1", ReplyAction = "*")]
    [XmlSerializerFormat(SupportFaults = true)]
    Task<SIS_Planifica_CargaResponse> SIS_Planifica_Carga(SIS_Planifica_CargaRequest request);
}

[MessageContract(IsWrapped = false)]
public sealed class SIS_Planifica_CargaRequest
{
    [MessageBodyMember(Namespace = "urn:petroperu.com.pe:pmerp:tas:Planifica_Carga", Order = 0)]
    public MT_Planifica_Carga_Request? MT_Planifica_Carga_Request;

    public SIS_Planifica_CargaRequest() { }

    public SIS_Planifica_CargaRequest(MT_Planifica_Carga_Request MT_Planifica_Carga_Request)
    {
        this.MT_Planifica_Carga_Request = MT_Planifica_Carga_Request;
    }
}

[MessageContract(IsWrapped = false)]
public sealed class SIS_Planifica_CargaResponse
{
    [MessageBodyMember(Namespace = "urn:petroperu.com.pe:pmerp:tas:Planifica_Carga", Order = 0)]
    public MT_Planifica_Carga_Response? MT_Planifica_Carga_Response;

    public SIS_Planifica_CargaResponse() { }

    public SIS_Planifica_CargaResponse(MT_Planifica_Carga_Response MT_Planifica_Carga_Response)
    {
        this.MT_Planifica_Carga_Response = MT_Planifica_Carga_Response;
    }
}

[XmlType(Namespace = "urn:petroperu.com.pe:pmerp:tas:Planifica_Carga")]
public sealed class MT_Planifica_Carga_Request
{
    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 0)]
    public string I_NRO_TRANSPORTE { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 1)]
    public string I_TERMINAL { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 2)]
    public string I_MAYORISTA { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 3)]
    public string I_PLACA_VEH { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 4)]
    public string I_FECHA_CARGA { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 5)]
    public string I_DNI { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 6)]
    public string I_DESTINO { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 7)]
    public string I_IND_VIAJE { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 8)]
    public string I_BAY_QUEUE_PRIORITY { get; set; } = "";

    [XmlArray(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 9)]
    [XmlArrayItem("item", typeof(DT_Planifica_Carga_DetItem), Form = System.Xml.Schema.XmlSchemaForm.Unqualified, IsNullable = false)]
    public DT_Planifica_Carga_DetItem[][] Detalle { get; set; } = Array.Empty<DT_Planifica_Carga_DetItem[]>();
}

[XmlType(AnonymousType = true, Namespace = "urn:petroperu.com.pe:pmerp:tas:Planifica_Carga")]
public sealed class DT_Planifica_Carga_DetItem
{
    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 0)]
    public string NRO_TRANS { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 1)]
    public string NRO_ENTREGA { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 2)]
    public string CUSTOMER_CODE { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 3)]
    public string DESTINATARIO { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 4)]
    public string SCOP { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 5)]
    public string COMPARTIMENTO { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 6)]
    public string PROD_COMER { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 7)]
    public string VOLUMEN { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 8)]
    public string UMVOL { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 9)]
    public string API { get; set; } = "";
}

[XmlType(Namespace = "urn:petroperu.com.pe:pmerp:tas:Planifica_Carga")]
public sealed class MT_Planifica_Carga_Response
{
    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 0)]
    public DT_RETURN Return { get; set; } = new();
}

[XmlType(Namespace = "urn:petroperu.com.pe:pmerp:tas:Planifica_Carga")]
public sealed class DT_RETURN
{
    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 0)]
    public string TYPE { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 1)]
    public string ID { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 2)]
    public string NUMBER { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 3)]
    public string MESSAGE { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 4)]
    public string LOG_NO { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 5)]
    public string LOG_MSG_NO { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 6)]
    public string MESSAGE_V1 { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 7)]
    public string MESSAGE_V2 { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 8)]
    public string MESSAGE_V3 { get; set; } = "";

    [XmlElement(Form = System.Xml.Schema.XmlSchemaForm.Unqualified, Order = 9)]
    public string MESSAGE_V4 { get; set; } = "";
}
