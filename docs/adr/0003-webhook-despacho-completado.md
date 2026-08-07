# ADR 0003: Notificación de despacho completado vía webhook (reemplaza OPC-UA)

- **Estado**: Aceptado
- **Fecha**: 2026-08-07
- **Decisiones relacionadas**: CONTEXT.md #2, #3, #5, #9, #11, #12, #13, #17, #18

## Contexto

Hasta este ADR, el servicio web detectaba que un despacho había terminado en la isla
suscribiéndose por OPC-UA (`OpcUaBackgroundService`, `src/Despachos.Api/Services/OpcUaBackgroundService.cs`)
a una variable expuesta por el OPC-UA Server del SCADA (`ns=2;s=Despachos.Completados`, valor
`"{NroTransporte}|1"`). Al recibir la notificación, el valor se parseaba y se encolaba en un
`Channel<string>` que `OutboxWorker` consumía para armar y enviar la confirmación 3.2 a SAP.

Esta suscripción OPC-UA traía costos operativos: certificados de aplicación cliente (con
carpetas `pki/own|trusted|issuers|rejected`), lógica de reconexión con `SessionReconnectHandler`,
un healthcheck dedicado (`OpcUaHealthCheck`), y la necesidad de que el servicio corra como
Windows Service nativo (no IIS) para no perder la sesión de larga duración en cada reciclado
de app pool. El cliente decidió reemplazar este mecanismo: un servicio aparte (el "Servicio de
Captura", fuera del alcance de este repo) se encargará de detectar el despacho completado y
notificarlo directamente al servicio web por HTTP. El servicio OPC-UA/OPC One deja de usarse.

El resto del flujo de confirmación (armar el payload 3.2 combinando `confirmacion_despacho` +
`despachos_detail`, encolarlo en `outbox_confirmacion`, y que `OutboxWorker` lo envíe a SAP con
reintento/backoff) ya estaba implementado y no depende de OPC-UA: solo depende de recibir un
`NroTransporte`. El cambio se limita entonces a reemplazar la fuente de esa señal.

## Decisiones

1. **Nuevo endpoint HTTP `POST /webhooks/despacho-completado`**, expuesto por el mismo Minimal
   API que ya sirve `/health` y el SOAP inbound. Body JSON:

   ```json
   { "NroTransporte": "0001234567" }
   ```

   Respuestas: `202 Accepted` con `{ "nroTransporte": "..." }` si se validó y encoló; `400 Bad
   Request` con el detalle del campo si `NroTransporte` falta o excede 10 caracteres (el `Max
   Length` de `despachos_header.NroTransporte`); `401 Unauthorized` si falla el Basic Auth.

2. **Basic Auth dedicada** (`WebhookCompletado:Username`/`Password`), independiente de
   `SapInbound:Username`/`Password`. El Servicio de Captura no es SAP PI: comparte el mecanismo
   de autenticación (Basic Auth sobre HTTP, simétrico al resto del contrato) pero no el secreto.
   `BasicAuthMiddleware` resuelve qué par de credenciales exigir según el path de la request.
   Arranque fail-closed: si `WebhookCompletado:Username` no está configurado, el servicio no
   levanta (mismo patrón que ya existía para `SapInbound:Username`).

3. **`DespachoCompletadoNotifier` reemplaza el canal de `OpcUaBackgroundService`**. Es una clase
   singleton simple (no `BackgroundService`) que solo expone un `Channel<string>` — `Writer`
   para el endpoint, `Reader` para `OutboxWorker`. Mantiene la misma forma de comunicación
   interna (productor/consumidor desacoplado) que ya existía, para minimizar el cambio en
   `OutboxWorker`.

4. **`WebhookCompletadoService` como capa fina de validación**, simétrica a
   `PlanificaCargaService` (el handler del SOAP inbound 3.1): valida el `NroTransporte`
   (requerido, ≤10 caracteres), escribe en el `Channel` y devuelve `Either<ValidationErrors,
   string>`. El endpoint minimal API solo mapea ese resultado a HTTP status codes. Queda
   unit-testeable igual que el resto de servicios del dominio (sin `WebApplicationFactory`).

5. **Eliminación completa de OPC-UA**: se borra `OpcUaBackgroundService.cs` y `OpcUaHealthCheck`,
   se quitan los tres `PackageReference` de `OPCFoundation.NetStandard.Opc.Ua*` del `.csproj`, y
   se elimina la sección `OpcUa` de `appsettings*.json`. No queda health check reemplazante para
   ese slot: a diferencia de OPC-UA, un webhook no mantiene una conexión persistente cuyo estado
   valga la pena reportar en `/health` — la señal de salud relevante (¿llegan notificaciones?) ya
   la cubre indirectamente `OutboxHealthCheck` (pendientes acumulándose en la tabla outbox).

6. **Sin cambios en `ConfirmacionService` ni en `OutboxWorker`** más allá de la fuente del
   `NroTransporte`: `ConfirmacionService.ProcesarDespachoCompletadoAsync` sigue leyendo
   `confirmacion_despacho` (que el SCADA sigue escribiendo directo en MySQL, decisión #8) y
   `OutboxWorker` sigue siendo el único punto que habla con SAP. La idempotencia por
   `NroTransporte` ya existente (`existeEnOutbox`) cubre reintentos/duplicados del webhook sin
   trabajo adicional.

7. **El Servicio de Captura queda fuera de este repo**. No se define aquí cómo detecta el
   despacho completado (podría seguir usando OPC-UA contra el SCADA, o Modbus, u otro
   mecanismo) — ese es su problema interno. El contrato que este ADR fija es únicamente la
   interfaz HTTP que expone el servicio web.

## Consecuencias

**Positivas**:
- Se elimina la dependencia de certificados/PKI OPC-UA, reconexión con backoff propio, y la
  justificación de "Windows Service por sesión de larga duración" pierde peso (aunque el
  servicio se sigue instalando así por el outbox worker en background).
- Contrato de entrada simple, testeable sin infraestructura externa (no requiere un SCADA/OPC-UA
  Server simulado para pruebas locales, alcanza con `curl`/Postman).
- Simetría de autenticación con el resto de la API (Basic Auth por path), sin introducir un
  mecanismo nuevo (API key, HMAC, etc.).

**Negativas / trade-offs**:
- El servicio web pasa a depender de que el Servicio de Captura llame confiablemente al webhook;
  si esa llamada se pierde, la única red de seguridad es el startup scan (decisión #9), que solo
  corre al iniciar el proceso — un despacho completado mientras el servicio está arriba y la
  notificación se pierde no se recupera hasta el próximo reinicio. Si esto resulta insuficiente,
  una futura iteración podría agregar un scan periódico (no solo en startup).
- Pierde el chequeo activo de salud que sí daba OPC-UA (`IsConnected`); ahora un problema en el
  Servicio de Captura no es visible en `/health` hasta que se note la ausencia de confirmaciones.

## Pendiente

- **Contrato exacto con el Servicio de Captura**: este ADR fija el lado del servicio web
  (`POST /webhooks/despacho-completado`, body `{NroTransporte}`, Basic Auth). Falta coordinar
  con el equipo dueño de ese servicio la URL pública, y confirmar que el body/verbo propuestos
  les sirven tal cual o requieren ajuste.
- **Credenciales `WebhookCompletado`**: definirlas y distribuirlas de forma segura al Servicio
  de Captura (no en el repo, igual que `SapInbound`/`Sap`).
- **Reintentos del lado del Servicio de Captura**: si el webhook responde `5xx` o timeout, ese
  servicio debería reintentar — no está definido aquí porque vive en su propio código.

## Implementación (referencia)

- `src/Despachos.Api/Services/DespachoCompletadoNotifier.cs`: nuevo, reemplaza el rol de canal
  de `OpcUaBackgroundService`.
- `src/Despachos.Api/Services/WebhookCompletadoService.cs`: nuevo, valida y encola.
- `src/Despachos.Api/Endpoints/WebhookEndpoints.cs`: nuevo, `POST /webhooks/despacho-completado`.
- `src/Despachos.Api/Services/OpcUaBackgroundService.cs`: eliminado.
- `src/Despachos.Api/Services/HealthChecks.cs`: `OpcUaHealthCheck` eliminado.
- `src/Despachos.Api/Services/OutboxWorker.cs`: constructor recibe `DespachoCompletadoNotifier`
  en vez de `OpcUaBackgroundService`.
- `src/Despachos.Api/Middleware/BasicAuthMiddleware.cs`: credenciales resueltas por path
  (`/webhooks/despacho-completado` → `WebhookCompletado:*`; resto → `SapInbound:*`).
- `src/Despachos.Api/Program.cs`: registro de `DespachoCompletadoNotifier`/
  `WebhookCompletadoService`, `MapWebhookEndpoints()`, chequeo fail-closed de
  `WebhookCompletado:Username`, se quita el registro de `OpcUaBackgroundService` y del
  healthcheck `opcua`.
- `src/Despachos.Api/Despachos.Api.csproj`: se quitan los `PackageReference` de
  `OPCFoundation.NetStandard.Opc.Ua*`.
- `appsettings.json` / `appsettings.Development.json` / `appsettings.Production.json.example`:
  sección `OpcUa` reemplazada por `WebhookCompletado`.
- `CONTEXT.md`: términos `Servicio de Captura` y `Webhook de Despacho Completado` nuevos;
  `OPC-UA Server` eliminado; decisiones #2, #3, #5, #9, #11, #12, #13, #17, #18 actualizadas.
- `docs/instalacion-windows.md` y `docs/checklist-variables-entorno-produccion.md`: variables
  `OpcUa__*` reemplazadas por `WebhookCompletado__*`, se quita la sección de certificados OPC-UA.

## Referencias

- ADR 0001: Confirmación de carga a SAP vía SOAP 1.1 (sentido outbound, sin cambios).
- ADR 0002: Planificación de carga vía SOAP inbound (patrón de Basic Auth por path que este ADR
  extiende a un segundo consumidor).
- Código eliminado: `src/Despachos.Api/Services/OpcUaBackgroundService.cs` (ver historial git).
