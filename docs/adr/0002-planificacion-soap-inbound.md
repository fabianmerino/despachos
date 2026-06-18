# ADR 0002: Planificación de carga desde SAP vía SOAP 1.1 con SoapCore

- **Estado**: Aceptado
- **Fecha**: 2026-06-18
- **Decisiones relacionadas**: CONTEXT.md #4, #11, #14, #15, #16; ADR 0001

## Contexto

La Interfaz de Planificación de Carga (3.1) es el camino por el cual SAP envía las órdenes
de despacho al servicio web. Hasta este ADR, el inbound se implementaba como un endpoint
REST `POST /planificacion` (`src/Despachos.Api/Endpoints/DespachoEndpoints.cs:12`) que
recibía XML crudo, lo desserializaba con `XmlSerializer` a `PlanificacionCargaXml`
(`src/Despachos.Api/Models/PlanificacionCargaXml.cs`) y respondía con un XML ad-hoc
`<PlanificacionResponse>`. La autenticación era `X-API-Key` vía middleware global
(`src/Despachos.Api/Middleware/ApiKeyMiddleware.cs`). La cancelación se exponía en un
endpoint separado `POST /cancelacion` (`DespachoEndpoints.cs:48`).

El cliente Petroperú confirmó que la integración con SAP PI es **SOAP/WSDL en ambos
sentidos** (ver ADR 0001 para el sentido outbound 3.2). Para el sentido inbound 3.1, el
cliente delega en nosotros la autoría del contrato: **nosotros publicamos el WSDL** y SAP
PI genera/configura su sender channel a partir de él. No hay XSD impuesto por SAP PI para
el request inbound —diseñamos el contrato desde cero.

El contrato actual usa nombres "amigables" en PascalCase (`NroTransporte`, `NroEntrega`,
`Producto`, ...) que **no** coinciden con el estilo SAP del WSDL 3.2 ya analizado
(`NRO_TRANS`, `NRO_ENTREGA`, `PROD_COMER`, `COMPARTIMENTO`, `UMVOL`, ...). Para mantener
consistencia contractual entre las dos interfaces y facilitar el mapeo en SAP PI, el nuevo
WSDL 3.1 usará el mismo estilo de nombres que el 3.2.

## Decisiones

1. **Exponer 3.1 como servicio SOAP 1.1** con SoapCore encima de Kestrel/ASP.NET Core.
   SoapCore es el estándar de facto para publicar endpoints SOAP en .NET 9 minimal-stack:
   integra con el pipeline de middlewares existente, genera WSDL en `?wsdl` automáticamente
   y soporta `document/literal` con `MessageContract`.

2. **Autoría del contrato: code-first con `MessageContract`**. Definir en C# los tipos del
   contrato con atributos `[ServiceContract]`, `[OperationContract]`,
   `[MessageContract]`, `[DataContract]`. SoapCore publica el WSDL resultante en
   `<endpoint>?wsdl` para que SAP PI lo importe. No se mantiene un WSDL escrito a mano;
   la fuente de verdad es el código C#.

3. **Simetría con el contrato 3.2**:

   - **Nombre del servicio**: `SIS_Planifica_Carga` (espejo de `SIS_Confirma_Carga`).
   - **Namespace target**: `urn:petroperu.com.pe:pmerp:tas:Planifica_Carga`.
   - **Binding**: SOAP 1.1, `document/literal`, `soapAction="http://sap.com/xi/WebService/soap1.1"`.
   - **Operación única**: `SIS_Planifica_Carga` (request/response síncrono).
   - **Response**: `MT_Planifica_Carga_Response` con `Return` de tipo `DT_RETURN`
     (BAPIRET2: `TYPE, ID, NUMBER, MESSAGE, LOG_NO, LOG_MSG_NO, MESSAGE_V1..V4`), idéntico
     al response del 3.2. Semántica:
     - `TYPE=S` → orden guardada en estado `Pendiente`.
     - `TYPE=E` → error de validación o de negocio, `MESSAGE` con el detalle.
     - No hay `W` ni reintento: el inbound es síncrono y SAP PI decide en base al
       `DT_RETURN` recibido.

4. **Estructura del request `MT_Planifica_Carga_Request`** con nombres estilo SAP
   alineados al 3.2. Mapeo desde el `PlanificacionCargaXml` actual:

   | Campo actual (PascalCase)  | Campo SOAP (UPPER_SNAKE) | Origen 3.2 |
   |----------------------------|--------------------------|------------|
   | `Header.NroTransporte`     | `I_NRO_TRANSPORTE`       | ✓ idéntico |
   | `Header.Terminal`          | `TERMINAL`               | –          |
   | `Header.Mayorista`         | `MAYORISTA`              | –          |
   | `Header.PlacaVeh`          | `PLACA_VEH`              | –          |
   | `Header.FechaCarga`        | `FECHA_CARGA` (yyyyMMdd) | –          |
   | `Header.DNI`               | `DNI`                    | –          |
   | `Header.Destino`           | `DESTINO`                | –          |
   | `Header.IndViaje`          | `IND_VIAJE`              | –          |
   | `Header.BayQueuePriority`  | `BAY_QUEUE_PRIORITY`     | –          |
   | `Detail.NroTransporte`     | `NRO_TRANS`              | ✓ idéntico |
   | `Detail.NroEntrega`        | `NRO_ENTREGA`            | ✓ idéntico |
   | `Detail.CustomerCode`      | `CUSTOMER_CODE`          | –          |
   | `Detail.Destinatario`      | `DESTINATARIO`           | –          |
   | `Detail.SCOP`              | `SCOP`                   | –          |
   | `Detail.NroCompartimento`  | `COMPARTIMENTO`          | ✓ idéntico |
   | `Detail.Producto`          | `PROD_COMER`             | ✓ idéntico |
   | `Detail.Volumen`           | `VOLUMEN`                | –          |
   | `Detail.UMVol`             | `UMVOL`                  | ✓ idéntico |
   | `Detail.API`               | `API`                    | –          |

   Estructura anidada simétrica al 3.2: `I_NRO_TRANSPORTE` + `Detalle/item[]` donde cada
   `item` lleva los campos del compartimento. El header va "plano" en el request (sin
   envoltorio `Header` adicional) para mantener el estilo `I_<campo>` del 3.2.

5. **Autenticación Basic Auth** (simétrica al outbound 3.2 del ADR 0001). SAP PI envía
   `Authorization: Basic <base64(user:pass)>` en la petición SOAP. Se elimina el
   `ApiKeyMiddleware` para el path del servicio SOAP; las credenciales se configuran en
   `SapInbound:Username` / `SapInbound:Password` (env vars / user secrets, nunca en repo).
   El healthcheck sigue sin auth.

6. **Stack servidor: SoapCore**. Paquete `SoapCore` (versión compatible con .NET 9).
   Se registra vía `app.UseSoapEndpoint<TServiceContract>(...)` sobre el pipeline de
   ASP.NET Core, en un path dedicado (propuesto `/soap/planificacion-carga`). SoapCore
   publica el WSDL en `?wsdl` y atiende las peticiones SOAP 1.1 con `BasicHttpBinding`.

7. **Sin cancelación**. Se elimina la operación `POST /cancelacion` y el método
   `DespachoService.CancelarOrdenAsync` deja de exponerse (se puede conservar el método
   interno para uso futuro, pero no hay endpoint público). El operario gestiona
   cancelaciones por otro canal (no por SAP). Esto simplifica el contrato SOAP a una sola
   operación y alinea con el uso real reportado por el cliente.

8. **Eliminar el endpoint REST `/planificacion`** una vez que SAP migre al SOAP. No se
   mantienen ambos en paralelo: el contrato inbound queda único y claro. Durante el
   desarrollo local se puede probar el handler SOAP directamente con un cliente SOAP
   (Postman, SOAPUI, `curl` con envelope).

9. **Reutilización del dominio**. El operation handler de SoapCore debe llamar al
   `DespachoService` existente. Se refactoriza `DespachoService.ProcesarPlanificacionAsync`
   para que acepte el `DT_Planifica_Carga_Request` ya desserializado (en vez de un string
   XML), de modo que la validación, el mapeo a `DespachoHeader`/`DespachoDetail`, la
   idempotencia por `NroTransporte` y el guardado en MySQL se reutilicen tal cual. El
   `ValidationErrors` se mapea a `DT_RETURN` con `TYPE=E` y `MESSAGE` concatenando los
   errores.

10. **Idempotencia y estados sin cambio**. Se mantienen las reglas actuales
    (`DespachoService.cs:53`): duplicado en estado `Pendiente` → update (remove + add);
    duplicado en otro estado → `TYPE=E` con mensaje "orden en estado X, no se puede
    modificar". La orden se guarda en `Pendiente`.

## Consecuencias

**Positivas**:
- Contrato inbound y outbound simétricos en nombres, namespace y mecanismo de auth.
- SAP PI consume un único WSDL publicado por nosotros, sin ambigüedad contractual.
- El WSDL se genera desde el código (code-first): un solo origen de verdad, sin
  riesgo de divergencia entre WSDL y tipos C#.
- La lógica de negocio (`DespachoService`) se reutiliza sin reescribir validaciones.
- `DT_RETURN` como respuesta de negocio permite semántica clara éxito/error sin
  depender solo de HTTP status o SOAP Fault.

**Negativas / trade-offs**:
- Nuevo paquete `SoapCore` en el proyecto (dependencia adicional, mantenida por la
  comunidad pero ampliamente usada en .NET).
- Refactor de `DespachoService.ProcesarPlanificacionAsync`: cambia la firma de
  `string xmlBody` a `DT_Planifica_Carga_Request`. El endpoint REST actual (que se
  elimina según decisión #8) deja de ser el punto de entrada.
- Renombrado de campos en `PlanificacionCargaXml`/`PlanificacionHeaderXml`/
  `PlanificacionDetailXml` (o reemplazo por los data contracts nuevos). El
  `DespachoHeader`/`DespachoDetail` del EF Core **no** cambia (es modelo interno);
  solo cambia el mapeo XML ↔ entidad.
- Hay que verificar que SAP PI pueda importar el WSDL generado por SoapCore sin
  fricción (SoapCore genera WSDL 1.1 estándar, pero conviene una prueba de
  importación real en el Integration Directory del cliente).
- Sin cancelación vía SOAP: si en el futuro el cliente requiere cancelar órdenes
  desde SAP, habrá que añadir una segunda operación al contrato (cambio versionado
  del WSDL).

## Pendiente

- **Credenciales Basic Auth inbound**: las definimos nosotros (recomendado, creds
  dedicadas para SAP PI) o las pide el cliente. Sin ellas, el servicio SOAP rechaza
  401 todas las peticiones.
- **URL pública del endpoint SOAP**: la necesita SAP PI para configurar el receiver
  communication channel. Definir path final (propuesto `/soap/planificacion-carga`)
  y host/puerto donde el servicio web sea alcanzable desde SAP PI.
- **Prueba de importación del WSDL** en SAP PI Integration Directory (QC).
- **Confirmación final del cliente** de que no requieren operación de cancelación
  vía SOAP.
- **Definir el response de éxito** con detalle: ¿`DT_RETURN` con `TYPE=S` alcanza, o
  SAP PI espera además el `NroTransporte`/`Estado` en el response? En el ADR se
  asume `DT_RETURN` únicamente por simetría con el 3.2; si SAP PI requiere más
  campos en el response, se añaden al `MT_Planifica_Carga_Response`.

## Implementación (referencia)

Cuando se ejecute, los cambios esperados son:

- `src/Despachos.Api/SoapSap/` (o nueva carpeta `SoapInbound/`): data contracts
  `SIS_Planifica_CargaRequest`, `SIS_Planifica_CargaResponse`,
  `MT_Planifica_Carga_Request`, `DT_Planifica_Carga_DetItem`, reutilizando `DT_RETURN`
  del proxy 3.2 si procede (o definiéndolo local con los mismos campos).
- `src/Despachos.Api/SoapInbound/IPlanificaCargaService.cs`: `[ServiceContract]` con
  `[OperationContract] SIS_Planifica_Carga`.
- `src/Despachos.Api/SoapInbound/PlanificaCargaService.cs`: implementación que llama
  a `DespachoService.ProcesarPlanificacionAsync` y mapea `ValidationErrors` →
  `DT_RETURN(TYPE=E)`.
- `src/Despachos.Api/Services/DespachoService.cs`: cambio de firma del método
  `ProcesarPlanificacionAsync` para recibir `MT_Planifica_Carga_Request` en vez de
  `string xmlBody`. Eliminar `CancelarOrdenAsync` o dejarlo sin endpoint.
- `src/Despachos.Api/Program.cs`: `app.UseSoapEndpoint<IPlanificaCargaService>(...)`
  con path `/soap/planificacion-carga`, SOAP 1.1, y Basic Auth sobre ese path.
- `src/Despachos.Api/Middleware/ApiKeyMiddleware.cs`: reemplazar por Basic Auth
  middleware (o usar ASP.NET Core Authentication con `BasicHttpBinding` security).
- `src/Despachos.Api/Endpoints/DespachoEndpoints.cs`: eliminar `POST /planificacion`
  y `POST /cancelacion`.
- `src/Despachos.Api/Models/PlanificacionCargaXml.cs`: reemplazar por los data
  contracts SOAP.
- `appsettings.json`: añadir `SapInbound:Username` / `SapInbound:Password`.
- `CONTEXT.md`: actualizar decisiones #4 (síncrono SAP inbound SOAP), #11 (auth
  mixta: 3.1 Basic Auth inbound, 3.2 Basic Auth outbound), #14 (sin cancelación),
  #15 (validación estructural sobre el request SOAP desserializado), #16 (errores
  vía `DT_RETURN.TYPE=E`, no HTTP status + XML plano).
- `Despachos.Api.csproj`: añadir `PackageReference` a `SoapCore`.

## Referencias

- ADR 0001: Confirmación de carga a SAP vía SOAP 1.1 con proxy WCF (sentido outbound).
- WSDL 3.2 analizado: `wsdl/confirmacion-3.2.wsdl` (referencia de estilo para el
  contrato 3.1).
- Código inbound actual: `src/Despachos.Api/Endpoints/DespachoEndpoints.cs`,
  `src/Despachos.Api/Models/PlanificacionCargaXml.cs`,
  `src/Despachos.Api/Services/DespachoService.cs`,
  `src/Despachos.Api/Middleware/ApiKeyMiddleware.cs`.
- SoapCore: https://github.com/DigDes/SoapCore
