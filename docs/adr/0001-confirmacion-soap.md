# ADR 0001: Confirmación de carga a SAP vía SOAP 1.1 con proxy WCF

- **Estado**: Aceptado
- **Fecha**: 2026-06-18
- **Decisiones relacionadas**: CONTEXT.md #7, #20, #21

## Contexto

La Interfaz de Confirmación de Carga (3.2) debe enviar a SAP PI los datos reales del despacho
(volúmenes observados, temperatura, API, vol a 60°F) para cada compartimento del transporte.

Hasta este ADR, el servicio web implementaba el envío 3.2 como un POST REST con body XML crudo
y cabecera `X-API-Key`. El cliente Petroperú entregó el contrato real de SAP PI: un Web Service
**SOAP 1.1** descrito por el WSDL `SIS_Confirma_Carga` publicado en el Integration Directory de
SAP PI/QC.

El contrato SOAP define:

- **Binding**: document/literal, SOAP 1.1, soapAction `http://sap.com/xi/WebService/soap1.1`.
- **Namespace** target: `urn:petroperu.com.pe:pmerp:tas:Confirma_Carga`.
- **Operación**: `SIS_Confirma_Carga` (request/response síncrono).
- **Request**: `MT_Confirma_Carga_Request` con `I_NRO_TRANSPORTE` (string) y `Detalle/item[]`
  con los campos `NRO_TRANS, NRO_ENTREGA, COMPARTIMENTO, PROD_COMER, T_DESPACHO, API_DESPACHO,
  VOL_DESPA_OBS, UMVOL, VOL_DESPA_60` (todos string).
- **Response**: `MT_Confirma_Carga_Response` con `Return` de tipo `DT_RETURN` (BAPIRET2):
  `TYPE, ID, NUMBER, MESSAGE, LOG_NO, LOG_MSG_NO, MESSAGE_V1..V4`.
- **Endpoint QC**: `http://petpidqc.petroperu.com.pe:51200/XISOAPAdapter/MessageServlet?...`
  (también HTTPS en puerto 51201).
- **Auth**: sin WS-Security policy declarada → Basic Auth a nivel adapter con el service `BC_WS`.

El contrato previo REST + `X-API-Key` no encaja con este WSDL. Hay que decidir cómo
implementarlo en .NET 9.

## Decisiones

1. **Adoptar SOAP 1.1** como transporte para 3.2, conforme al WSDL `SIS_Confirma_Carga`.
2. **Generar un proxy WCF** con `dotnet-svcutil` desde el WSDL y mantenerlo en
   `src/Despachos.Api/SoapSap/Reference.cs`. El proxy produce los tipos tipados
   (`SIS_Confirma_CargaClient`, `SIS_Confirma_CargaRequest`, `DT_Confirma_Carga_Request`,
   `DT_Confirma_Carga_DetItem`, `DT_RETURN`, etc.) y el envelope SOAP correcto.
3. **Auth Basic** con credenciales del service `BC_WS` configuradas en `Sap:Username` /
   `Sap:Password` (env vars / user secrets, nunca en repo).
4. **Éxito/error por `DT_RETURN.TYPE`**:
   - `S` → `Confirmado`, `OutboxEstado.Enviado`.
   - `W` → `Confirmado` con log de advertencia, `OutboxEstado.Enviado`.
   - `E` → `Error` definitivo, no reintenta (es error de negocio de SAP).
   - SOAP Fault / `CommunicationException` / 5xx HTTP → reintento con backoff
     (10s/30s/60s, máx. 3 intentos).
5. **Outbox pattern se mantiene**: la tabla `outbox_confirmacion` guarda el payload
   serializado del `DT_Confirma_Carga_Request` en XML para sobrevivir restarts. El
   `OutboxWorker` deserializa y llama al proxy por cada item pendiente.
6. **`BasicHttpBinding`** configurado en código (no en `appsettings`/`system.serviceModel`)
   para mantener el modelo de Minimal API + IHostedService y poder inyectar endpoint y
   credenciales desde `IConfiguration`. `MaxReceivedMessageSize = int.MaxValue`.
7. **HTTPS opcional**: si el endpoint comienza con `https`, se usa
   `BasicHttpSecurityMode.Transport`. Para QC se parte con HTTP (puerto 51200) para evitar
   roces con certificados autofirmados durante el desarrollo.

## Consecuencias

**Positivas**:
- Tipos y envelope SOAP conformes al contrato SAP PI, sin riesgo de desajuste manual.
- `DT_RETURN.TYPE` nos da semántica de éxito/error de negocio que el HTTP status solo no
  aporta en SOAP.
- El outbox y el worker existentes se reutilizan: solo cambia el transporte.

**Negativas / trade-offs**:
- Se añaden paquetes `System.ServiceModel.Primitives` y `System.ServiceModel.Http`
  (WCF client sobre .NET 9, soportado por dotnet-svcutil 8.x).
- El proxy es código autogenerado: regenerar cuando SAP PI publique una nueva versión del
  WSDL (registrar el WSDL fuente en `wsdl/confirmacion-3.2.wsdl` para reproducibilidad).
- `Detalle` es `DT_Confirma_Carga_DetItem[][]` (jagged array) por la anidación
  `Detalle (1..n) > item (1..n)` del XSD; en la práctica siempre se envía un único
  `Detalle` con todos los `item`s.

## Pendiente

- **Credenciales `BC_WS`**: las debe entregar el cliente. Sin ellas el worker no envía
  (log de error, no crash).
- **Endpoint de producción**: cuando el cliente lo entregue, se sobreescribe
  `Sap:ConfirmacionEndpoint` por env var / config de prod.
- **WSDL de la interfaz 3.1 (inbound)**: pendiente. En el ADR siguiente se decidirá si se
  expone con SoapCore/CoreWCF o si se mantiene Minimal API + XML aceptando envelope.

## Referencias

- WSDL fuente: `wsdl/confirmacion-3.2.wsdl` (descargado de
  `http://petpidqc.petroperu.com.pe:51200/dir/wsdl?p=sa/959a2ce2849c308bac542d145bd4b143`).
- Proxy generado: `src/Despachos.Api/SoapSap/Reference.cs`.
- Implementación: `src/Despachos.Api/Services/ConfirmacionService.cs`,
  `src/Despachos.Api/Services/OutboxWorker.cs`.
