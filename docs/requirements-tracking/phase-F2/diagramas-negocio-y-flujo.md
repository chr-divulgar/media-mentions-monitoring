# Diagramas de Negocio y Flujo - Workers y Plugins

Fecha: 2026-06-04

## 1. Vista de negocio (no tecnica)

Este diagrama muestra como se separa la operacion para escalar por tipo de medio sin cambiar el catalogo base de fuentes.

```mermaid
flowchart LR
    A[Catalogo de fuentes\nradio, tv, prensa, internet, redes, pdf] --> B[Reglas de enrutamiento\npor medio y plataforma]

    B --> C[Operacion continua\nradio y tv 24/7]
    B --> D[Operacion discreta\nprensa, internet, redes, pdf]

    C --> E[Alertas y consumo por cliente]
    D --> E

    E --> F[Reportes y evidencia\noperacion y cumplimiento]
```

## 2. Flujo operativo (end to end)

Este flujo resume como se procesa una fuente desde su entrada hasta la salida de evidencia.

```mermaid
flowchart TD
    A[Fuente activa] --> B[Validar medio permitido]
    B --> C[Resolver perfil de plugin\nmedia primero, plataforma despues]
    C --> D{Modo de ingesta}

    D -->|continuous| E[Worker continuo\nloop y canary por plataforma]
    D -->|discrete| F[Worker discreto\ncron o evento]

    E --> G[Ejecutar plugin]
    F --> G

    G --> H[Guardar evidencia local temporal]
    H --> I{Hay DB configurada?}
    I -->|no| J[Conservar evidencia local]
    I -->|si| K[Enviar artefacto a DB]
    K --> L{Persistencia exitosa?}
    L -->|no| J
    L -->|si| M[Eliminar evidencia local]
    J --> N[Metricas y trazabilidad]
    M --> N
    N --> O[Consumo de alertas\ny reporteria]
```

## 3. Flujo canary para migracion radio y tv

```mermaid
flowchart LR
    A[Fuentes radio y tv] --> B[Filtro por medio\nradio,video]
    B --> C[Canary por plataforma\n20 -> 50 -> 100]
    C --> D[Validar indicadores\nexito, lag, error]
    D --> E{Cumple umbral?}
    E -->|si| F[Subir porcentaje]
    E -->|no| G[Rollback por configuracion]
```

## 4. Archivos de configuracion de referencia

1. Sources de entrada: apps/media-core-worker/stage/capture-sources.example.json
2. Perfiles de plugins: apps/media-core-worker/stage/plugin-profiles.example.json
3. Opciones de workers: apps/media-core-worker/stage/worker-options.example.json

## 5. Regla de negocio clave

1. El catalogo base de fuentes no se altera.
2. La logica de ejecucion vive en perfiles de plugin.
3. El crecimiento de canales se resuelve agregando perfiles, no reescribiendo el core.
4. La evidencia local no es almacenamiento permanente: se limpia cuando la persistencia en DB fue exitosa.
