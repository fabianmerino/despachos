# Arquitectura y Flujo de Información - Despachos Petroperú

## 1. Vista de Componentes

```mermaid
graph TB
    subgraph "SAP ECC"
        SAP[ SAP ERP ]
    end

    subgraph "Servicio Web (.NET 8)"
        API[ Minimal API ]
        DS[ DespachoService ]
        CS[ ConfirmacionService ]
        OW[ OutboxWorker<br/>BackgroundService ]
        OPC[ OpcUaBackgroundService<br/>BackgroundService ]
        HK[ HealthChecks ]
        AK[ ApiKeyMiddleware ]
    end

    subgraph "Base de Datos"
        MYSQL[( MySQL 8+ )]
        subgraph "Tablas"
            TH[ despachos_header ]
            TD[ despachos_detail ]
            TC[ confirmacion_despacho ]
            TO[ outbox_confirmacion ]
        end
    end

    subgraph "Isla de Despacho"
        ACC[ ACCULOAD ]
        SCADA[ SCADA System ]
        OPCS[ OPC-UA Server ]
    end

    SAP -->|"3.1 Planificación Carga<br/>POST /planificacion (XML)"| API
    SAP -->|"Cancelación<br/>POST /cancelacion (XML)"| API
    API -->|"X-API-Key"| AK
    AK --> DS
    DS -->|"Guarda Orden"| MYSQL
    CS -->|"Lee/Encola"| MYSQL
    OW -->|"Procesa Outbox"| MYSQL
    OW -->|"3.2 Confirmación Carga<br/>POST HTTP (XML)"| SAP
    OPC -->|"Subscription<br/>MonitoredItem"| OPCS

    ACC -->|"Modbus"| SCADA
    SCADA -->|"Consulta parámetros"| MYSQL
    SCADA -->|"Guarda mediciones"| MYSQL
    SCADA -->|"Escribe flag completado<br/>NroTransporte|1"| OPCS
    OPCS -->|"Notificación cambio"| OPC
    OPC -->|"Channel"| OW

    style SAP fill:#e1f5fe
    style API fill:#fff3e0
    style MYSQL fill:#e8f5e9
    style SCADA fill:#fce4ec
    style OPCS fill:#fce4ec
```

---

## 2. Flujo de Planificación de Carga (Interfaz 3.1)

```mermaid
sequenceDiagram
    autonumber
    actor SAP
    participant API as Servicio Web
    participant DS as DespachoService
    participant VAL as Validador
    participant DB as MySQL

    SAP->>API: POST /planificacion<br/>XML (HEADER + DETAILs)
    Note over API: ApiKeyMiddleware valida X-API-Key
    API->>DS: ProcesarPlanificacionAsync(xml)
    DS->>DS: Deserializar XML
    alt XML mal formado
        DS-->>API: ValidationErrors(XML)
        API-->>SAP: 400 BadRequest + ErrorResponse XML
    end
    DS->>VAL: ValidarHeader(header)
    DS->>VAL: ValidarDetails(details)
    alt Validación fallida
        DS-->>API: ValidationErrors(campos)
        API-->>SAP: 400 BadRequest + ErrorResponse XML
    end
    DS->>DB: SELECT header por NroTransporte
    alt Orden existe y Estado != Pendiente
        DS-->>API: ValidationErrors(NroTransporte, 409)
        API-->>SAP: 409 Conflict + ErrorResponse XML
    else Orden existe y Estado == Pendiente
        DS->>DB: DELETE header existente (cascade)
    end
    DS->>DB: INSERT header + details
    DB-->>DS: Guardado exitoso
    DS-->>API: DespachoHeader
    API-->>SAP: 200 OK + PlanificacionResponse XML
```

---

## 3. Flujo en Isla de Despacho (SCADA)

```mermaid
sequenceDiagram
    autonumber
    actor OP as Operario
    participant ACC as ACCULOAD
    participant SCADA as SCADA
    participant DB as MySQL
    participant OPCS as OPC-UA Server

    OP->>ACC: Ingresa NroTransporte
    ACC->>SCADA: Lee NroTransporte (Modbus)
    SCADA->>DB: SELECT orden por NroTransporte
    DB-->>SCADA: Header + Details (compartimentos)
    Note over SCADA: Operario carga combustible
    SCADA->>DB: INSERT confirmacion_despacho<br/>(Temperatura, API, VolObservado, Vol60)
    SCADA->>OPCS: Write Variable<br/>"NroTransporte|1"
```

---

## 4. Flujo de Confirmación de Carga (Interfaz 3.2)

```mermaid
sequenceDiagram
    autonumber
    participant OPCS as OPC-UA Server
    participant OPC as OpcUaBackgroundService
    participant CH as Channel&lt;string&gt;
    participant OW as OutboxWorker
    participant CS as ConfirmacionService
    participant DB as MySQL
    participant SAP as SAP

    Note over OW: Startup Scan
    OW->>CS: ObtenerCompletadosPendientesAsync()
    CS->>DB: SELECT headers Estado=Completado<br/>sin outbox Estado=Enviado
    DB-->>CS: Lista NroTransportes
    loop Por cada pendiente
        OW->>CS: ProcesarDespachoCompletadoAsync(nro)
        CS->>DB: SELECT header + details + confirmaciones
        CS->>DB: INSERT outbox_confirmacion (payload XML)
        CS->>DB: UPDATE header Estado = Completado
    end

    Note over OPCS,CH: En caliente (subscription)
    OPCS->>OPC: Notification: "NroTransporte|1"
    OPC->>OPC: ParseNroTransporte()
    OPC->>CH: TryWrite(nroTransporte)
    CH->>OW: ReadAsync()
    OW->>CS: ProcesarDespachoCompletadoAsync(nro)
    CS->>DB: INSERT outbox_confirmacion
    CS->>DB: UPDATE header Estado = Completado
    OW->>DB: SELECT outbox Estado=Pendiente
    loop Por cada outbox pendiente
        OW->>SAP: POST Confirmación XML<br/>X-API-Key
        alt 2xx Success
            SAP-->>OW: 200 OK
            OW->>DB: UPDATE outbox Estado=Enviado
            OW->>DB: UPDATE header Estado=Confirmado
        else 5xx / 429
            OW->>OW: Backoff (10s/30s/60s)<br/>Reintentos++
            alt Reintentos < 3
                OW->>SAP: Reintento
            else Agotado
                OW->>DB: UPDATE outbox Estado=Error
            end
        else 4xx Client Error
            OW->>DB: UPDATE outbox Estado=Error
        end
    end
```

---

## 5. Ciclo de Vida de la Orden de Despacho

```mermaid
stateDiagram-v2
    [*] --> Pendiente : SAP envía 3.1
    Pendiente --> EnProceso : SCADA inicia carga
    EnProceso --> Completado : SCADA guarda mediciones
    Completado --> Confirmado : OutboxWorker envía 3.2 a SAP (2xx)
    Completado --> Error : OutboxWorker agota reintentos o 4xx
    Pendiente --> Cancelado : POST /cancelacion
    Pendiente --> Pendiente : SAP reenvía 3.1 (update)
    Confirmado --> [*]
    Cancelado --> [*]
    Error --> [*]
```

---

## 6. Modelo de Datos (ERD)

```mermaid
erDiagram
    DESPACHOS_HEADER ||--o{ DESPACHOS_DETAIL : contiene
    DESPACHOS_HEADER ||--o| CONFIRMACION_DESPACHO : confirma
    DESPACHOS_HEADER ||--o| OUTBOX_CONFIRMACION : encola

    DESPACHOS_HEADER {
        string NroTransporte PK "Max 10"
        string Terminal "Max 20, Req"
        string Mayorista "Max 20, Req"
        string PlacaVeh "Max 13, Req"
        date FechaCarga "Req"
        string DNI "Max 8, Req"
        string Destino "Max 20, Req"
        string IndViaje "Max 1, Req"
        string BayQueuePriority "Max 16, Req"
        string Estado "Default: Pendiente"
        datetime CreadoEn "Req"
    }

    DESPACHOS_DETAIL {
        int Id PK
        string NroTransporte FK
        string NroEntrega "Max 10, Req"
        string CustomerCode "Max 16"
        string Destinatario "Max 16"
        string SCOP "Max 20"
        string NroCompartimento "Max 3, Req"
        string Producto "Max 18, Req"
        decimal Volumen "16,2, Req"
        string UMVol "Max 4, Req"
        decimal API "8,4"
        string Estado "Default: Pendiente"
    }

    CONFIRMACION_DESPACHO {
        string NroTransporte PK,FK
        string NroCompartimento PK,FK "Max 3"
        decimal Temperatura "6,2"
        decimal APIDespachado "8,4"
        decimal VolObservado "16,2"
        decimal Vol60 "16,2"
        datetime FechaCompletado "Req"
    }

    OUTBOX_CONFIRMACION {
        int Id PK
        string NroTransporte FK "Max 10, Req"
        text Payload "longtext, Req"
        int Reintentos "Default: 0"
        int MaxReintentos "Default: 3"
        string Estado "Default: Pendiente"
        datetime CreadoEn "Req"
        datetime UltimoIntentoEn
    }
```

---

## 7. Arquitectura Interna del Servicio Web

```mermaid
graph LR
    subgraph "HTTP Layer"
        REQ[ HTTP Request ]
        AK[ ApiKeyMiddleware ]
        EP[ Minimal API Endpoints ]
    end

    subgraph "Application Layer"
        DS[ DespachoService ]
        CS[ ConfirmacionService ]
    end

    subgraph "Infrastructure Layer"
        DB[ EF Core + Pomelo ]
        HTTP[ HttpClientFactory ]
        OPC[ OPC-UA Client ]
    end

    subgraph "Background Workers"
        OPCS[ OpcUaBackgroundService ]
        OW[ OutboxWorker ]
    end

    subgraph "External"
        MYSQL[( MySQL )]
        SAP[ SAP ]
        OPCSRV[ OPC-UA Server ]
    end

    REQ --> AK
    AK --> EP
    EP --> DS
    EP --> CS
    DS --> DB
    CS --> DB
    DB --> MYSQL
    HTTP --> SAP
    OPC --> OPCSRV
    OPCS --> OPC
    OW --> CS
    OW --> HTTP
    OW --> DB
    OPCS -->|Channel| OW
```

---

## 8. Decisiones Clave de Arquitectura

| # | Decisión | Descripción |
|---|----------|-------------|
| 1 | **MySQL única** | Compartida entre Servicio Web y SCADA. Ambos leen/escriben la misma BD. |
| 2 | **OPC-UA solo para confirmación** | Sentido SCADA → Servicio Web únicamente. No se notifican nuevas órdenes vía OPC-UA. |
| 3 | **Subscription OPC-UA** | `MonitoredItem` con `PublishingInterval=1000ms`, no polling. |
| 4 | **Síncrono SAP inbound** | SAP espera respuesta HTTP al enviar 3.1. |
| 5 | **Outbox pattern** | Tabla `outbox_confirmacion` con reintentos (3 intentos, backoff 10s/30s/60s). |
| 6 | **Autenticación API Key** | SAP inbound/outbound usan `X-API-Key`. OPC-UA usa user/password. |
| 7 | **Idempotencia** | Duplicado de SAP en estado `Pendiente` hace update; en otros estados devuelve 409. |
| 8 | **Startup degradado** | Si OPC-UA no está disponible, el servicio acepta órdenes e intenta reconectar cada 30s. |
| 9 | **Graceful shutdown** | Drain del worker outbox (timeout 30s). Pendientes en memoria se recuperan vía startup scan. |
| 10 | **Confirmación idempotente** | HTTP 2xx = `Confirmado`, 4xx = `Error`, 5xx = reintento. |

---

## 9. Formato Variable OPC-UA

```
ns=2;s=Despachos.Completados
Valor: "{NroTransporte}|1"
```

Ejemplo: `"T001234567|1"`

El servicio parsea el valor, extrae el `NroTransporte` y lo encola en un `Channel<string>` para procesamiento por el `OutboxWorker`.

---

## 10. Endpoints REST

| Método | Ruta | Descripción | Auth |
|--------|------|-------------|------|
| POST | `/planificacion` | Recibe Interfaz 3.1 de SAP | X-API-Key |
| POST | `/cancelacion` | Cancela orden en estado Pendiente | X-API-Key |
| GET | `/health` | Health checks (MySQL, OPC-UA, Outbox) | - |
| GET | `/health/ready` | Readiness probe | - |
| GET | `/health/live` | Liveness probe | - |
