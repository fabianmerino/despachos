# Instalación en Windows Server

Guía para instalar `Despachos.Api` como servicio de Windows en el servidor de planta, con MySQL y el OPC-UA Server del SCADA como dependencias externas.

## 1. Arquitectura de despliegue

El servicio corre como **Windows Service nativo** (no bajo IIS). Esto es intencional: el servicio mantiene una `Subscription` OPC-UA de larga duración con el SCADA, y el reciclado periódico de app pools de IIS la mataría, obligando a reconectar constantemente. Kestrel escucha directamente en el puerto configurado.

```
SAP PI  --SOAP/HTTP(S)--> [Despachos.Api Windows Service] --OPC-UA--> SCADA (OPC-UA Server)
                                    |
                                    v
                                 MySQL (compartida con el SCADA)
```

## 2. Prerrequisitos

- Windows Server 2019+ (o Windows 10/11 para pruebas).
- **.NET 8 Hosting Bundle** (incluye el runtime ASP.NET Core 8): https://dotnet.microsoft.com/download/dotnet/8.0 → "Hosting Bundle". Aunque no se usa IIS, el Hosting Bundle instala el runtime compartido; alternativamente se puede publicar el servicio como *self-contained* y saltarse este paso (ver §4).
- **MySQL Server 8.0.x** accesible desde el servidor (puede ser el mismo que usa el SCADA — así está diseñado, ver ADR).
- Acceso de red al **OPC-UA Server del SCADA** (típicamente puerto `4840/tcp`) y al endpoint SOAP de **SAP PI** (saliente).
- Una cuenta de servicio de Windows (o `NT AUTHORITY\NetworkService`) con permisos de escritura sobre `C:\ProgramData\Despachos\`.

## 3. Base de datos (MySQL)

El esquema ya no se crea con `EnsureCreated` (peligroso en una BD compartida con el SCADA): se versiona con **migraciones de EF Core**.

1. Crear la base de datos y el usuario de aplicación:

```sql
CREATE DATABASE Despachos CHARACTER SET utf8mb4;
CREATE USER 'despachos_app'@'%' IDENTIFIED BY '<password-fuerte>';
GRANT SELECT, INSERT, UPDATE, DELETE ON Despachos.* TO 'despachos_app'@'%';
```

   No se otorgan permisos DDL al usuario de aplicación: las migraciones se aplican una vez, con un usuario con más privilegios (ver paso 2), y el servicio corre después con `despachos_app`, que solo necesita DML.

2. Aplicar las migraciones (desde una máquina con el SDK de .NET y el código fuente, apuntando a la cadena de conexión real; requiere un usuario con permisos DDL, p. ej. `root` o uno dedicado):

```bash
dotnet tool install --global dotnet-ef
cd src/Despachos.Api
dotnet ef database update --connection "Server=<host>;Database=Despachos;User=<usuario-ddl>;Password=<password>;"
```

   Alternativa: dejar que el propio servicio las aplique en su primer arranque (`Program.cs` llama a `MigrateAsync()` al iniciar) — en ese caso la cuenta configurada en `ConnectionStrings:DefaultConnection` sí necesita permisos DDL la primera vez, o se ejecuta el `dotnet ef database update` del paso anterior por separado y luego el servicio ya solo necesita DML.

3. Verificar la versión real del servidor MySQL (`SELECT VERSION();`) y ajustar `Database:MySqlServerVersion` en `appsettings.json` si no es `8.0.36`. Este valor **no** dispara una conexión en el arranque (a diferencia del `AutoDetect` que traía el código antes) — es solo el dialecto SQL que usa Pomelo.

## 4. Publicar y copiar el servicio

Desde la máquina de build:

```bash
dotnet publish src/Despachos.Api/Despachos.Api.csproj -c Release -o C:\Apps\Despachos
```

Para no depender de que el servidor tenga el runtime instalado, agregar `--self-contained -r win-x64`.

Copiar el contenido de `C:\Apps\Despachos` al servidor, en la misma ruta (o ajustar el `binPath` del paso 6).

## 5. Configuración (`appsettings.Production.json` + variables de entorno)

**No** se editan `appsettings.json`/`appsettings.Development.json` en el servidor — son plantillas versionadas en git. En el servidor se usa `appsettings.Production.json` (no versionado) o variables de entorno del servicio (recomendado para secretos, con el separador `__`):

| Variable de entorno | Propósito |
|---|---|
| `ConnectionStrings__DefaultConnection` | Cadena de conexión a MySQL con el usuario `despachos_app` |
| `SapInbound__Username` / `SapInbound__Password` | Credenciales que SAP PI usa contra este servicio (Basic Auth). **Obligatorias**: el servicio no arranca si `SapInbound__Username` está vacío. |
| `Sap__ConfirmacionEndpoint` | URL del Web Service SOAP de SAP PI (3.2) |
| `Sap__Username` / `Sap__Password` | Credenciales del service `BC_WS` en SAP PI |
| `OpcUa__EndpointUrl` | `opc.tcp://<host-scada>:4840` |
| `OpcUa__UserName` / `OpcUa__Password` | Si el OPC-UA Server del SCADA exige autenticación |
| `OpcUa__UseSecurity` | `true` para exigir canal seguro OPC-UA (ver §7); `false` para `SecurityPolicy=None` |
| `Kestrel__Endpoints__Http__Url` | Por defecto `http://0.0.0.0:8080`; ajustar el puerto si hace falta |

Estas variables se configuran a nivel de **máquina** (no de usuario), porque el servicio corre bajo una cuenta de servicio, no la sesión interactiva:

```powershell
[Environment]::SetEnvironmentVariable("SapInbound__Username", "sap_pi_user", "Machine")
[Environment]::SetEnvironmentVariable("SapInbound__Password", "<password>", "Machine")
[Environment]::SetEnvironmentVariable("ConnectionStrings__DefaultConnection", "Server=<host>;Database=Despachos;User=despachos_app;Password=<password>;", "Machine")
# ... resto de variables
```

Requiere reiniciar la sesión/servicio para que tome las variables nuevas.

## 6. Instalar como Windows Service

```powershell
New-Service -Name "DespachosPetroperu" `
  -BinaryPathName "C:\Apps\Despachos\Despachos.Api.exe" `
  -DisplayName "Despachos Petroperu - Integracion SAP/SCADA" `
  -StartupType Automatic

sc.exe failure "DespachosPetroperu" reset= 86400 actions= restart/60000/restart/60000/restart/60000

Start-Service DespachosPetroperu
```

- `sc.exe failure ... actions= restart/...` hace que Windows reinicie el servicio si crashea (p. ej. si arrancó sin `SapInbound__Username` configurado).
- Si se usa una cuenta de servicio dedicada en vez de `LocalSystem`, otorgarle antes permisos de escritura sobre `C:\ProgramData\Despachos\` (logs y certificados OPC-UA, ver §7-8) y sobre `C:\Apps\Despachos` si el certificado o algo más necesita escribir ahí.

Verificar:

```powershell
Get-Service DespachosPetroperu
Get-Content C:\ProgramData\Despachos\logs\despachos-*.log -Tail 50
```

## 7. OPC-UA: certificados y conectividad con el SCADA

El cliente OPC-UA necesita un certificado de aplicación propio (se autogenera la primera vez, ya no falla el arranque como antes). Las carpetas del store viven en:

```
C:\ProgramData\Despachos\pki\own       (certificado propio del servicio, autogenerado)
C:\ProgramData\Despachos\pki\trusted   (certificados del SCADA que este servicio confía)
C:\ProgramData\Despachos\pki\issuers   (CAs intermedias, si aplica)
C:\ProgramData\Despachos\pki\rejected  (certificados vistos pero no confiados)
```

Dos escenarios:

**A. Canal sin seguridad (`OpcUa__UseSecurity=false`, por defecto)** — igual se genera el certificado propio (la librería OPC-UA lo exige aunque el canal sea `SecurityPolicy=None`), pero no hay que intercambiar confianza con el SCADA. Es el modo más simple para arrancar; el tráfico OPC-UA va sin cifrar en la red interna de planta.

**B. Canal seguro (`OpcUa__UseSecurity=true`)** — requiere confianza mutua:
1. Arrancar el servicio una vez para que genere su certificado en `pki\own\certs\*.der`.
2. Copiar ese `.der` al store de certificados confiables del OPC-UA Server del SCADA (el mecanismo depende del SCADA: consola de administración, carpeta de confianza, etc.).
3. Copiar el certificado del OPC-UA Server del SCADA a `C:\ProgramData\Despachos\pki\trusted\certs\`.
4. Reiniciar el servicio.

`OpcUa__AutoAcceptUntrustedCertificates=true` salta el paso 2-3 aceptando cualquier certificado — **solo para pruebas**, nunca en producción (el `appsettings.json` base ya lo trae en `false`; `appsettings.Development.json` lo trae en `true` para desarrollo local).

Conectividad: confirmar que el servidor donde corre `Despachos.Api` puede alcanzar `opc.tcp://<host-scada>:4840` (firewall de planta/Windows Firewall en el SCADA suele bloquear el puerto por defecto).

## 8. Logs

Ruta por defecto: `C:\ProgramData\Despachos\logs\despachos-YYYYMMDD.log` (rotación diaria), configurable con `Logging__FilePath`. Ya no es una ruta relativa `logs/...` — como Windows Service, una ruta relativa resolvía contra `C:\Windows\System32` y quedaba sin permisos de escritura ni visibilidad.

## 9. Firewall

- **Entrante**: puerto de Kestrel (`8080` por defecto, o el que se configure) desde la red donde está SAP PI.
- **Saliente**: hacia MySQL (`3306`), hacia el OPC-UA Server del SCADA (`4840`), hacia el endpoint de SAP PI (puerto según `Sap:ConfirmacionEndpoint`, típicamente `51200` según el `appsettings.json` de ejemplo).

## 10. HTTPS en el inbound SOAP

Por defecto el servicio expone HTTP plano (Basic Auth sobre HTTP = credenciales en base64 legibles en la red). Para producción se recomienda habilitar HTTPS agregando un endpoint Kestrel vía variables de entorno (no se versiona ningún certificado ni password en el repo):

```powershell
[Environment]::SetEnvironmentVariable("Kestrel__Endpoints__Https__Url", "https://0.0.0.0:8443", "Machine")
[Environment]::SetEnvironmentVariable("Kestrel__Endpoints__Https__Certificate__Path", "C:\ProgramData\Despachos\certs\despachos.pfx", "Machine")
[Environment]::SetEnvironmentVariable("Kestrel__Endpoints__Https__Certificate__Password", "<password-del-pfx>", "Machine")
```

El `.pfx` puede ser un certificado interno de la CA de Petroperú, o para pruebas uno autofirmado:

```powershell
$cert = New-SelfSignedCertificate -DnsName "despachos.petroperu.local" -CertStoreLocation "cert:\LocalMachine\My"
Export-PfxCertificate -Cert $cert -FilePath "C:\ProgramData\Despachos\certs\despachos.pfx" -Password (ConvertTo-SecureString -String "<password>" -Force -AsPlainText)
```

Una vez confirmado que HTTPS funciona, se puede quitar el endpoint HTTP (`Kestrel__Endpoints__Http__Url`) para forzar TLS.

## 11. Verificación post-instalación

```powershell
Invoke-RestMethod http://localhost:8080/health | ConvertTo-Json -Depth 5
```

Debe devolver `status: "Healthy"` (o `"Unhealthy"` en `opcua` si el SCADA todavía no está accesible — es información real, ya no un health check que miente). Probar también la autenticación del inbound SOAP:

```powershell
Invoke-WebRequest http://localhost:8080/soap/planificacion-carga?wsdl -Headers @{Authorization = "Basic " + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("sap_pi_user:<password>"))}
```

Un `401` sin el header `Authorization` confirma que el Basic Auth está activo (y que el servicio, si esas credenciales no estuvieran configuradas, directamente no habría arrancado).
