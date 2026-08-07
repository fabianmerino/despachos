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
Sistema de control en isla que actúa como Modbus master. Lee el `NroTransporte` ingresado por el operario en el ACCULOAD, consulta MySQL para obtener los parámetros de la orden, y al finalizar el despacho guarda las mediciones en MySQL. Ya no notifica al Servicio Web directamente: esa señal la produce el Servicio de Captura.
_Avoid_: HMI, panel, PLC

**ACCULOAD**:
Dispositivo en isla donde el operario ingresa manualmente el `NroTransporte`. El SCADA lee este valor vía Modbus.
_Avoid_: Terminal, keypad, panel

**Servicio de Captura**:
Sistema externo (fuera del alcance de este repo) responsable de detectar que un despacho terminó en la isla y notificarlo al Servicio Web vía el Webhook de Despacho Completado. Reemplaza la suscripción OPC-UA que el Servicio Web mantenía antes directamente con el SCADA.
_Avoid_: OPC-UA client, servicio OPC One, OPCVA

**Webhook de Despacho Completado**:
Endpoint `POST /webhooks/despacho-completado` que el Servicio Web expone con Basic Auth dedicada (`WebhookCompletado:Username`/`Password`, independiente de las credenciales de SAP). Recibe `{ "NroTransporte": "..." }` del Servicio de Captura y encola el proceso de confirmación hacia SAP a través del outbox existente.
_Avoid_: callback OPC-UA, notificación OPC

**Servicio Web**:
Aplicación .NET 8 que recibe órdenes de SAP (3.1), las guarda en MySQL, expone el Webhook de Despacho Completado para enterarse de despachos terminados, y envía la confirmación a SAP (3.2).
_Avoid_: API, backend

## Relationships

- Un **Transporte** contiene uno o más **Compartimentos**.
- Una **Orden de Despacho** (3.1) produce exactamente una **Confirmación de Carga** (3.2).
- El **Servicio Web** recibe de SAP y notifica a SAP.
- El **SCADA** lee del **ACCULOAD** vía Modbus, consulta MySQL, y guarda las mediciones en MySQL.
- El **Servicio de Captura** notifica al **Servicio Web** a través del **Webhook de Despacho Completado** cuando un despacho termina.

## Example dialogue

> **Dev:** "Cuando SAP envía una Orden de Despacho, ¿el servicio web le avisa al SCADA de alguna forma?"
> **Domain expert:** "No. Solo se guarda en MySQL. El operario recibe la orden en papel, teclea el NroTransporte en el ACCULOAD, y el SCADA busca los datos en MySQL."

> **Dev:** "Y cuando el despacho termina, ¿cómo sabe el servicio web que debe enviar la confirmación a SAP?"
> **Domain expert:** "El SCADA guarda las mediciones en MySQL. Cuando el despacho termina, el Servicio de Captura llama al webhook `/webhooks/despacho-completado` con el NroTransporte. El servicio web arma el payload 3.2 con esa señal y lo encola para SAP."

**Ciclo de vida de la Orden de Despacho**:
`Pendiente` → `EnProceso` → `Completado` → `Confirmado`. Además `Cancelado` como terminal alternativo y `Error` para fallos en confirmación a SAP.
_Avoid_: Estado 0/1/2, active, done

## Architecture decisions

1. **MySQL única** – compartida entre Servicio Web y SCADA. Ambos leen/escriben la misma BD.
2. **Webhook solo para confirmación** – no se usa para notificar nuevas órdenes. El sentido es Servicio de Captura → Servicio Web únicamente.
3. **Contrato del Webhook de Despacho Completado** – `POST /webhooks/despacho-completado`, body JSON `{ "NroTransporte": "..." }`, respuesta `202 Accepted` si se encoló, `400` si `NroTransporte` falta o excede 10 caracteres. La llamada en sí es la señal de completado (no hay flag separado). Reemplaza la suscripción OPC-UA (`MonitoredItem`) que el servicio web mantenía antes con el SCADA.
4. **Síncrono SAP inbound SOAP** – SAP PI consume un servicio SOAP 1.1 expuesto por el servicio web (`SIS_Planifica_Carga`, path `/soap/planificacion-carga`, binding document/literal, namespace `urn:petroperu.com.pe:pmerp:tas:Planifica_Carga`, soapAction `http://sap.com/xi/WebService/soap1.1`). SoapCore code-first publica el WSDL en `?wsdl`. Response con `DT_RETURN.TYPE` (`S` = guardada en `Pendiente`, `E` = error de validación/negocio).
5. **Stack .NET 8** – Minimal API, Pomelo EF Core MySQL, IHostedService para el outbox worker.
6. **Merge de datos en confirmación** – el payload 3.2 combina mediciones del SCADA (temperatura, API despachado, vol observado, vol a 60°F) con datos fijos de la orden original (NroEntrega, NroCompartimento, Producto, UMVol).
7. **Confirmación a SAP vía SOAP 1.1** – el servicio web consume un Web Service SOAP expuesto por SAP PI (WSDL `SIS_Confirma_Carga`, binding document/literal, namespace `urn:petroperu.com.pe:pmerp:tas:Confirma_Carga`, soapAction `http://sap.com/xi/WebService/soap1.1`). Proxy WCF generado con `dotnet-svcutil` en `src/Despachos.Api/SoapSap/`. Auth Basic sobre HTTP (usuario/clave del service `BC_WS`). Éxito/error se decide por `DT_RETURN.TYPE` (`S`/`W` = Confirmado, `E` = Error, sin reintento). SOAP Fault y 5xx → reintento con backoff.
8. **SCADA consulta MySQL directo** – el SCADA lee la BD sin intermediación del servicio web, para no depender de su uptime.
9. **Detección de completados con doble mecanismo** – en caliente el servicio web recibe la notificación por el Webhook de Despacho Completado; al startup escanea MySQL por órdenes completadas sin confirmación enviada a SAP (red de seguridad si una llamada al webhook se pierde).
10. **Outbox pattern para confirmación a SAP** – los envíos se encolan en tabla `outbox_confirmacion` con reintentos (3 intentos, backoff 10s/30s/60s); un `BackgroundService` procesa la cola.
11. **Autenticación mixta** – SAP inbound (3.1 SOAP) usa HTTP Basic Auth con credenciales dedicadas (`SapInbound:Username`/`Password`). SAP outbound (3.2 SOAP) usa HTTP Basic Auth con las credenciales del service `BC_WS` de SAP PI. El Webhook de Despacho Completado usa HTTP Basic Auth con credenciales propias (`WebhookCompletado:Username`/`Password`), independientes de las de SAP.
12. **Idempotencia por NroTransporte** – duplicado de SAP en estado Pendiente hace update; en otros estados devuelve `DT_RETURN.TYPE=E` (conflicto de estado). El webhook comparte la misma idempotencia: si el `NroTransporte` ya está encolado en el outbox, una notificación repetida se ignora.
13. **Endpoint del webhook desacoplado del envío a SAP** – `WebhookCompletadoService` solo valida y encola el `NroTransporte` en un `Channel<string>` (`DespachoCompletadoNotifier`); `OutboxWorker` sigue siendo el único que arma el payload 3.2 (vía `ConfirmacionService`) y lo envía a SAP.
14. **Sin cancelación vía SOAP** – no se expone operación de cancelación al SAP. El operario gestiona cancelaciones por otro canal. `DespachoService.CancelarOrdenAsync` se conserva como capacidad interna sin endpoint público.
15. **Validación inbound** – estructural (XML bien formado, campos obligatorios) + `Volumen > 0`, `COMPARTIMENTO` no duplicado. Sin validación de catálogos. Se ejecuta sobre el `MT_Planifica_Carga_Request` ya desserializado por SoapCore.
16. **Respuestas de error a SAP** – SOAP 1.1 con `MT_Planifica_Carga_Response.Return` (`DT_RETURN`): `TYPE=E` + `MESSAGE` con los errores de validación concatenados. Éxito con `TYPE=S`.
17. **Startup degradado** – el servicio arranca (Kestrel levanta) aunque MySQL no esté disponible al iniciar; las migraciones fallan en silencio con warning y se reintenta la conexión en las siguientes operaciones. Ya no depende de una sesión OPC-UA persistente al arranque: el webhook no requiere conexión previa a nada externo.
18. **Configuración** – `appsettings.json` + variables de entorno. Secretos (credenciales SAP, credenciales del webhook) en variables de entorno o user secrets.
19. **Graceful shutdown** – drain del worker outbox (timeout 30s). Pendientes en memoria se recuperan vía startup scan.
20. **SAP outbound 3.2 es SOAP** – documento XML envuelto en SOAP envelope, binding document/literal, soapAction `http://sap.com/xi/WebService/soap1.1`. Payload generado por el proxy WCF en `src/Despachos.Api/SoapSap/`.
21. **Confirmación a SAP idempotente por `DT_RETURN.TYPE`** – `S`/`W` = `Confirmado`, `E` = `Error` (no reintenta), SOAP Fault / 5xx / `CommunicationException` = reintento con backoff.
