# Checklist de variables de entorno — antes de instalar en producción

Checklist de verificación previa a instalar `Despachos.Api` como Windows Service en el servidor de planta. Complementa [instalacion-windows.md](instalacion-windows.md) (§5) con foco en qué falta definir y por qué, según la revisión del `appsettings.json` versionado.

Alternativa a las variables de entorno: [`appsettings.Production.json.example`](../src/Despachos.Api/appsettings.Production.json.example) trae los mismos valores como plantilla de archivo. Copiarlo junto al `.exe` como `appsettings.Production.json` (no se commitea — ya está en `.gitignore`) y completar los `REEMPLAZAR_*`. Para los passwords, se prefiere variable de entorno de todas formas: un archivo de config es más fácil de dejar tirado con secretos en texto plano.

Todas las variables se configuran a nivel de **máquina** (la cuenta de servicio, no la sesión interactiva, es la que arranca el proceso):

```powershell
[Environment]::SetEnvironmentVariable("<Nombre>", "<Valor>", "Machine")
```

Requieren reiniciar el servicio (o la sesión) para tomar efecto.

## 🔴 Obligatorias — el servicio no arranca sin esto

- [ ] `SapInbound__Username`
- [ ] `SapInbound__Password`

  Credenciales que SAP PI usa contra el Basic Auth del inbound SOAP. [Program.cs](../src/Despachos.Api/Program.cs) corta el arranque (`Log.Fatal` + excepción) si `SapInbound__Username` está vacío, a propósito: sin esto el endpoint quedaría sin autenticación. En el `appsettings.json` versionado viene vacío adrede.

- [ ] `WebhookCompletado__Username`
- [ ] `WebhookCompletado__Password`

  Credenciales que el Servicio de Captura (reemplaza a OPC-UA/OPC One, ver [ADR 0003](adr/0003-webhook-despacho-completado.md)) usa contra el Basic Auth del webhook `/webhooks/despacho-completado`. Mismo fail-closed que `SapInbound__Username`: sin esto el servicio no arranca. Son credenciales **distintas** a las de SAP — no reutilizar `SapInbound__Username`/`Password` aquí.

## 🟠 Alta prioridad — el servicio arranca, pero mal configurado en producción

- [ ] `ConnectionStrings__DefaultConnection`

  El valor versionado es `Server=localhost;Database=Despachos;User=root;Password=;` — desarrollo local. En producción: host real de MySQL, usuario `despachos_app` (no `root`), password fuerte. Ver §3 de `instalacion-windows.md` para el `GRANT` correcto (solo DML, sin DDL).

- [ ] `Sap__ConfirmacionEndpoint`

  **Verificar con el equipo de SAP PI antes de salir a vivo.** El valor versionado apunta a `petpidqc.petroperu.com.pe` — el sufijo `qc` sugiere ambiente de *Quality/Testing*, no producción. Si se instala tal cual, las confirmaciones de despacho (3.2) se enviarían al ambiente equivocado sin ningún error visible.

- [ ] `Sap__Username`
- [ ] `Sap__Password`

  Credenciales salientes hacia el servicio `BC_WS` de SAP PI. Vienen vacías en el `appsettings.json` base; si el endpoint de producción exige Basic Auth (probable, tratándose de un WS externo), las llamadas saldrán con `401` si se dejan así.

## 🟡 Recomendadas — según el entorno de red

- [ ] `Kestrel__Endpoints__Https__Url` + `Kestrel__Endpoints__Https__Certificate__Path` + `...Certificate__Password`

  El servicio expone HTTP plano por defecto (`http://0.0.0.0:8080`) — Basic Auth sobre HTTP viaja en base64, legible en la red. Si SAP PI o el Servicio de Captura no llegan por una red ya cifrada/segmentada, habilitar HTTPS (procedimiento completo en §9 de `instalacion-windows.md`).

- [ ] `Logging__FilePath`

  Solo si se quiere una ruta de logs distinta a `C:\ProgramData\Despachos\logs\despachos-*.log`.

## Script de referencia

Plantilla para pegar en PowerShell **como administrador**, completando los valores reales:

```powershell
[Environment]::SetEnvironmentVariable("SapInbound__Username", "<usuario-sap-pi>", "Machine")
[Environment]::SetEnvironmentVariable("SapInbound__Password", "<password>", "Machine")
[Environment]::SetEnvironmentVariable("WebhookCompletado__Username", "<usuario-servicio-captura>", "Machine")
[Environment]::SetEnvironmentVariable("WebhookCompletado__Password", "<password>", "Machine")
[Environment]::SetEnvironmentVariable("ConnectionStrings__DefaultConnection", "Server=<host-mysql>;Database=Despachos;User=despachos_app;Password=<password>;", "Machine")
[Environment]::SetEnvironmentVariable("Sap__ConfirmacionEndpoint", "<url-produccion-confirmada-con-sap-pi>", "Machine")
[Environment]::SetEnvironmentVariable("Sap__Username", "<usuario-bc-ws>", "Machine")
[Environment]::SetEnvironmentVariable("Sap__Password", "<password>", "Machine")
```

## No es una variable de entorno, pero bloquea la validación end-to-end

- [ ] Confirmar con el equipo SCADA si la tabla real de despachos completados es `confirmacion_despacho` (la que asume el código) o `despachos_completos` (mencionada en su informe del 2026-05-01). Si no coincide, `ConfirmacionService` nunca va a encontrar filas que confirmar hacia SAP, en silencio, aunque el resto del sistema funcione. Ver memoria `schema-confirmacion-mismatch`.

## Verificación post-instalación

Ya cubierta en §10 de `instalacion-windows.md`: `GET /health` debe devolver `Healthy`, y una request al WSDL o al webhook sin `Authorization` debe devolver `401`.
