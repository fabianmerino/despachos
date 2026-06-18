# Despachos Petroperú

Servicio de integración entre SAP y el SCADA de isla de despacho para la planificación y confirmación de carga de combustibles.

## Language

**Orden de Despacho (DispatchOrder)**:
Conjunto de uno o más compartimentos de un transporte a cargar, recibido desde SAP vía la Interfaz de Planificación de Carga (3.1).
_Avoid_: Pedido, shipment, delivery

**Transporte**:
Vehículo cisterna identificado por `NroTransporte` y `PlacaVeh`, compuesto por múltiples compartimentos.
_Avoid_: Camión, vehículo, truck

**Compartimento**:
Subdivisión del transporte que contiene un producto específico con un volumen planificado.
_Avoid_: Tanque, compartment, tank

**Interfaz de Planificación de Carga (3.1)**:
Webhook que SAP consume para enviar órdenes de despacho al servicio web (HEADER + DETAIL por compartimento).
_Avoid_: Inbound order, carga programada

**Interfaz de Confirmación de Carga (3.2)**:
Webhook en SAP que el servicio web consume para devolver los datos reales de despacho (volúmenes observados, temperatura, API, vol a 60°F).
_Avoid_: Outbound confirmation, cierre de carga

**SCADA**:
Sistema de control en isla que actúa como OPC-UA Server y Modbus master. Lee el `NroTransporte` ingresado por el operario en el ACCULOAD, consulta MySQL para obtener los parámetros de la orden, y al finalizar el despacho guarda las mediciones y activa el flag de completado en OPC-UA.
_Avoid_: HMI, panel, PLC

**ACCULOAD**:
Dispositivo en isla donde el operario ingresa manualmente el `NroTransporte`. El SCADA lee este valor vía Modbus.
_Avoid_: Terminal, keypad, panel

**OPC-UA Server**:
El SCADA expone variables OPC-UA. El servicio web se suscribe a una variable que indica que una orden finalizó.
_Avoid_: Tag server, OPC DA

**Servicio Web**:
Aplicación .NET 8 que recibe órdenes de SAP (3.1), las guarda en MySQL, y se suscribe al OPC-UA Server del SCADA para detectar despachos completados y enviar la confirmación a SAP (3.2).
_Avoid_: API, backend

## Relationships

- Un **Transporte** contiene uno o más **Compartimentos**.
- Una **Orden de Despacho** (3.1) produce exactamente una **Confirmación de Carga** (3.2).
- El **Servicio Web** recibe de SAP y notifica a SAP.
- El **SCADA** lee del **ACCULOAD** vía Modbus, consulta MySQL, y escribe a OPC-UA.
- El **Servicio Web** es OPC-UA Client del **SCADA** (OPC-UA Server).

## Example dialogue

> **Dev:** "Cuando SAP envía una Orden de Despacho, ¿el servicio web notifica al SCADA por OPC-UA?"
> **Domain expert:** "No. Solo se guarda en MySQL. El operario recibe la orden en papel, teclea el NroTransporte en el ACCULOAD, y el SCADA busca los datos en MySQL."

> **Dev:** "Y cuando el despacho termina, ¿cómo sabe el servicio web que debe enviar la confirmación a SAP?"
> **Domain expert:** "El SCADA guarda las mediciones en MySQL y escribe una variable OPC-UA con el NroTransporte y un flag de completado. El servicio web está suscrito a esa variable y al detectar el cambio arma el payload 3.2 y lo envía a SAP."

**Ciclo de vida de la Orden de Despacho**:
`Pendiente` → `EnProceso` → `Completado` → `Confirmado`. Además `Cancelado` como terminal alternativo y `Error` para fallos en confirmación a SAP.
_Avoid_: Estado 0/1/2, active, done

## Architecture decisions

1. **MySQL única** – compartida entre Servicio Web y SCADA. Ambos leen/escriben la misma BD.
2. **OPC-UA solo para confirmación** – no se usa para notificar nuevas órdenes. El sentido es SCADA → Servicio Web únicamente.
3. **Subscription OPC-UA** – el servicio web usa MonitoredItem, no polling.
4. **Síncrono SAP inbound SOAP** – SAP PI consume un servicio SOAP 1.1 expuesto por el servicio web (`SIS_Planifica_Carga`, path `/soap/planificacion-carga`, binding document/literal, namespace `urn:petroperu.com.pe:pmerp:tas:Planifica_Carga`, soapAction `http://sap.com/xi/WebService/soap1.1`). SoapCore code-first publica el WSDL en `?wsdl`. Response con `DT_RETURN.TYPE` (`S` = guardada en `Pendiente`, `E` = error de validación/negocio).
5. **Stack .NET 8** – Minimal API, OPC Foundation, Pomelo EF Core MySQL, IHostedService para subscription OPC-UA.
6. **Merge de datos en confirmación** – el payload 3.2 combina mediciones del SCADA (temperatura, API despachado, vol observado, vol a 60°F) con datos fijos de la orden original (NroEntrega, NroCompartimento, Producto, UMVol).
7. **Confirmación a SAP vía SOAP 1.1** – el servicio web consume un Web Service SOAP expuesto por SAP PI (WSDL `SIS_Confirma_Carga`, binding document/literal, namespace `urn:petroperu.com.pe:pmerp:tas:Confirma_Carga`, soapAction `http://sap.com/xi/WebService/soap1.1`). Proxy WCF generado con `dotnet-svcutil` en `src/Despachos.Api/SoapSap/`. Auth Basic sobre HTTP (usuario/clave del service `BC_WS`). Éxito/error se decide por `DT_RETURN.TYPE` (`S`/`W` = Confirmado, `E` = Error, sin reintento). SOAP Fault y 5xx → reintento con backoff.
8. **SCADA consulta MySQL directo** – el SCADA lee la BD sin intermediación del servicio web, para no depender de su uptime.
9. **Detección de completados con doble mecanismo** – en caliente el servicio web recibe subscription OPC-UA; al startup escanea MySQL por órdenes completadas sin confirmación enviada a SAP.
10. **Outbox pattern para confirmación a SAP** – los envíos se encolan en tabla `outbox_confirmacion` con reintentos (3 intentos, backoff 10s/30s/60s); un `BackgroundService` procesa la cola.
11. **Autenticación mixta** – SAP inbound (3.1 SOAP) usa HTTP Basic Auth con credenciales dedicadas (`SapInbound:Username`/`Password`). SAP outbound (3.2 SOAP) usa HTTP Basic Auth con las credenciales del service `BC_WS` de SAP PI. OPC-UA usa user/password.
12. **Idempotencia por NroTransporte** – duplicado de SAP en estado Pendiente hace update; en otros estados devuelve `DT_RETURN.TYPE=E` (conflicto de estado).
13. **Variable OPC-UA de completado** – string con formato `"NroTransporte|1"`. El servicio web parsea para obtener el NroTransporte.
14. **Sin cancelación vía SOAP** – no se expone operación de cancelación al SAP. El operario gestiona cancelaciones por otro canal. `DespachoService.CancelarOrdenAsync` se conserva como capacidad interna sin endpoint público.
15. **Validación inbound** – estructural (XML bien formado, campos obligatorios) + `Volumen > 0`, `COMPARTIMENTO` no duplicado. Sin validación de catálogos. Se ejecuta sobre el `MT_Planifica_Carga_Request` ya desserializado por SoapCore.
16. **Respuestas de error a SAP** – SOAP 1.1 con `MT_Planifica_Carga_Response.Return` (`DT_RETURN`): `TYPE=E` + `MESSAGE` con los errores de validación concatenados. Éxito con `TYPE=S`.
17. **Startup degradado** – si OPC-UA no está disponible al iniciar, el servicio acepta órdenes igual e intenta reconectar en background cada 30s.
18. **Configuración** – `appsettings.json` + variables de entorno. Secretos (API keys, OPC-UA creds) en variables de entorno o user secrets.
19. **Graceful shutdown** – drain del worker outbox (timeout 30s). Pendientes en memoria se recuperan vía startup scan.
20. **SAP outbound 3.2 es SOAP** – documento XML envuelto en SOAP envelope, binding document/literal, soapAction `http://sap.com/xi/WebService/soap1.1`. Payload generado por el proxy WCF en `src/Despachos.Api/SoapSap/`.
21. **Confirmación a SAP idempotente por `DT_RETURN.TYPE`** – `S`/`W` = `Confirmado`, `E` = `Error` (no reintenta), SOAP Fault / 5xx / `CommunicationException` = reintento con backoff.
