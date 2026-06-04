# Plan Fase 1A - Unificacion Operativa (media-monitor + media-monitor-helper)

## 1. Objetivo de esta fase
Unificar la funcionalidad operativa actual de `media-monitor` y `media-monitor-helper` en una nueva solucion .NET 10 dentro del monorepo `media-mentions-monitoring`, sin afectar lo existente y sin incluir API en esta fase.

Meta de Fase 1A:
- Mantener la misma funcionalidad actual (paridad funcional).
- Ejecutar en paralelo (shadow/canary) antes del corte.
- Dejar base lista para agregar API en fase posterior.

## 2. Nombre propuesto dentro de apps/
Nombre recomendado:
- `media-core-worker`

Ruta objetivo:
- `media-mentions-monitoring/apps/media-core-worker`

Razon:
1. Describe el foco operativo (captura, segmentacion, control de procesos).
2. Permite crecimiento natural para incluir `Api.Host` despues sin renombrar.
3. Evita confundirlo con `web-api` actual de NestJS.

## 3. Alcance Fase 1A (incluido / excluido)
### Incluido
1. Captura continua y ciclo de procesos externos (ffmpeg/yt-dlp) equivalente al estado actual.
2. Segmentacion incremental y registro de estados/segmentos.
3. Funciones de helper:
  - supervision de procesos externos (apagado ordenado, timeout y recuperacion automatica)
  - reconciliacion de inactivos
  - control de procesos huérfanos
4. Persistencia agnostica al proveedor de base de datos (Firebase inicial), con capacidad de cambio a otro motor sin reescribir Domain/Application.
5. Observabilidad operativa (logs estructurados, metricas base, health checks).
6. Ingestion global compartida (captura y segmentacion) para todas las fuentes comunes; diferenciacion por tenant aplicada en alertas, reglas y vistas de consumo.

### Excluido
1. API publica nueva en .NET 10.
2. Migracion de frontend.
3. Modulos avanzados de analitica contractual (semaforo, share of voice, desinformacion).
4. Implementacion funcional de informes contractuales periodicos (semanal/mensual/especial), alertas/notas de salida y entrega funcional del historico; se gestiona en el plan general del producto.

## 4. Arquitectura de Fase 1A
Patron: Modular Hexagonal (Clean Architecture), .NET 10.

Componentes:
1. `Operations.Worker` (host principal de ciclo operativo)
2. `Capture` module
3. `Segmentation` module
4. `ProcessGuardian` module (supervision y resiliencia de procesos externos)
5. `Storage` adapters (provider-agnostic): `FirebaseAdapter` inicial + adapter alterno futuro (Mongo/Postgres u otro) + filesystem/evidence path
6. `Tooling` adapters (ffmpeg/yt-dlp process runner)

Regla de dependencia:
- Domain <- Application <- Infrastructure
- Worker host solo orquesta use cases.

Separacion de contexto de datos:
- Contexto Ingestion: artefactos de captura y segmentacion sobre fuentes globales compartidas.
- Contexto Alerting/Consumption: reglas, suscripciones, filtros y entregables parametrizados por tenant.

## 5. Estructura sugerida de carpetas
```
media-mentions-monitoring/apps/media-core-worker/
  MediaOpsCore.sln
  src/
    BuildingBlocks/
      Domain/
      Application/
      Infrastructure/
    Modules/
      Capture/
        Capture.Domain/
        Capture.Application/
        Capture.Infrastructure/
      Segmentation/
        Segmentation.Domain/
        Segmentation.Application/
        Segmentation.Infrastructure/
      ProcessGuardian/
        ProcessGuardian.Domain/
        ProcessGuardian.Application/
        ProcessGuardian.Infrastructure/
    Workers/
      Operations.Worker/
  tests/
    Unit/
    Integration/
    Architecture/
```

## 6. Mapeo de funcionalidad legacy -> nuevo modulo
1. `media-monitor` (w-service)
   - StartRecording / restart -> `Capture.Application`
   - OnChanged incremental -> `Segmentation.Application`
   - Process lifecycle tracking -> `ProcessGuardian.Application`

2. `media-monitor-helper`
   - monitor_processes.py -> `ProcessGuardian.Application/ProcessMonitorUseCase`
   - reconcile_inactive.py -> `ProcessGuardian.Application/ReconcileInactiveUseCase`
   - monitor_chunk_processes.py -> `ProcessGuardian.Application/ChunkProcessMonitorUseCase`

## 7. Plan de ejecucion (8 semanas sugeridas)
Regla transversal de fase:
1. Al cierre de cada semana se actualiza la documentacion tecnica/arquitectonica comun del proyecto exigible por contrato y la matriz de requisitos.
2. Esta regla no sustituye la implementacion de entregables funcionales contractuales, que se planifican en el plan general.

### Semana 1
1. Crear `apps/media-core-worker` + solucion .NET 10.
2. Configurar estructura Clean Architecture y standards.
3. Configurar CI basica para build + tests.
4. Crear estructura documental de fase para seguimiento semanal y evidencias.

### Semana 2
1. Implementar contratos de dominio y puertos de persistencia (provider-agnostic) + process runner.
2. Implementar `FirebaseAdapter` como proveedor inicial y pruebas de contrato de repositorios.
3. Crear pruebas de arquitectura (reglas de dependencia) y pruebas de compatibilidad de adapter.

### Semana 3-4
1. Portar flujo de captura continua (equivalente w-service).
2. Portar flujo de segmentacion incremental.
3. Instrumentar metricas base (errores, lag, segmentos generados).

### Semana 5
1. Portar supervision de procesos (helper) con politicas de timeout/restart.
2. Portar reconciliacion de inactivos.
3. Portar monitoreo de chunk process.

### Semana 6
1. Integracion end-to-end con BD y filesystem de stage.
2. Ejecutar modo shadow en paralelo con legacy.
3. Comparar paridad funcional (resultados por coleccion).

### Semana 7
1. Canary por subconjunto de plataformas (10-20%).
2. Ajustes por diferencias y tuning.
3. Documentar runbook de operacion y rollback.

### Semana 8
1. Escalar canary a 50% y luego 100% si KPIs ok.
2. Cierre de Fase 1A y acta de paridad funcional.
3. Preparar backlog de Fase 2 (API .NET 10).

## 8. Criterios de aceptacion Fase 1A
1. Paridad funcional >= 95% vs sistema actual durante ventana acordada.
2. No regresiones criticas en captura y segmentacion.
3. Procesos helper cubiertos por casos de uso .NET 10.
4. Observabilidad activa con dashboards operativos minimos.
5. Rollback validado.
6. Cambio de proveedor de datos validado en stage sin cambios en Domain/Application.
7. Suite de pruebas de contrato de repositorios ejecutando al menos 2 adapters (Firebase + mock/alterno).
8. Actualizacion semanal de documentacion tecnica contractual de fase y matriz viva de requisitos.
9. Esta actualizacion no implica documentar cada cambio tecnico por archivo ni generar reportes funcionales de salida (alertas/notas/estadisticas).
10. La documentacion semanal registra estado de SLA y continuidad del servicio, incluyendo contingencia tecnica, riesgos y mitigaciones.

## 9. KPIs de control
1. `capture_success_rate`
2. `segment_generation_rate`
3. `process_orphan_count`
4. `reconciliation_actions`
5. `pipeline_lag_seconds`
6. `critical_error_rate`

## 9.1 Recomendaciones de portabilidad y cumplimiento (desde dia 1)
1. Definir modelo canonico de datos de monitoreo, independiente del motor.
2. Implementar puertos/repositorios para desacoplar dominio del proveedor de datos.
3. Programar exportacion periodica en formatos abiertos (CSV/Excel/JSON estructurado).
4. Versionar scripts de migracion de datos (ejemplo: Firebase <-> Mongo).
5. Ejecutar simulacro mensual de restauracion/reimportacion en stage.
6. Mantener trazabilidad de cambios contra `REQUIREMENTS_LIVE_MATRIX.md`.
7. Mantener separacion explicita entre datos globales de ingesta y datos por tenant para alertas/consumo.

## 10. Estrategia de no impacto sobre lo existente
1. No modificar `apps/web-api` ni `apps/web-ui`.
2. Legacy sigue operando durante shadow/canary.
3. Cambios sobre BD con compatibilidad hacia atras.
4. Cutover gradual por plataformas.

## 11. Riesgos principales y mitigacion
1. Diferencias de comportamiento en timeouts de procesos externos.
   - Mitigacion: pruebas de caracterizacion + adapter de process runner.
2. Divergencia de datos entre legacy y nuevo flujo.
   - Mitigacion: reconciliacion automatica y reportes diarios de delta.
3. Sobrecarga por doble procesamiento en shadow.
   - Mitigacion: windowing por plataformas y limites de concurrencia.

## 12. Entregables de Fase 1A
1. Nueva solucion .NET 10 en `apps/media-core-worker`.
2. Runbook operativo + rollback.
3. Reporte de paridad funcional.
4. Checklist de salida hacia Fase 2 (API).
5. Modelo canonico de datos + paquete de exportacion abierta.
6. Scripts de migracion de datos y evidencia de prueba de restauracion.
7. Contratos de persistencia (ports) versionados y documentados.
8. Matriz de compatibilidad de proveedores de datos (Firebase inicial + candidato alterno).
9. Prueba de conmutacion de proveedor en stage (evidencia de ejecucion y resultados).
10. Paquete documental tecnico-contractual de fase con cortes semanales y trazabilidad por Requirement ID.
11. Registro formal de traspaso al plan general para funcionalidades contractuales de reporting y entrega funcional de historico.

## 12.1 Estructura documental recomendada (Fase 1A)
1. `docs/requirements-tracking/index.md` (indice contractual de fase y trazabilidad por Requirement ID).
2. `docs/requirements-tracking/phase-F1A/architecture-status.md` (estado arquitectonico y decisiones vigentes).
3. `docs/requirements-tracking/phase-F1A/compliance-status.md` (estado de cumplimiento, riesgos y supuestos).
4. La actualizacion semanal cubre documentacion comun de proyecto; no exige evidencia semanal por cada archivo tecnico.

## 13. Puente hacia Fase 2 (API)
Para fase posterior, agregar dentro del mismo app:
- `src/Api/Operations.Api.Host` (o `src/Api/Host.Api`)

Esto permite exponer endpoints sin rehacer dominio/aplicacion ya migrados en Fase 1A.

Para el plan general de implementacion funcional:
1. Informes automaticos periodicos en Ops-Core mediante worker dedicado de reporting.
2. Informes y consultas bajo demanda sobre el mismo backend por peticiones desde front.
