# Backlog Semilla Fase 2 - API .NET 10

Fecha de inicio propuesta: 2026-06-10

## Requirement IDs objetivo

- `RQ-002` Plataforma web de consulta con filtros.
- `RQ-004` Base de datos estructurada consultable.
- `RQ-005` Exportacion en formatos abiertos.
- `RQ-006` Seguridad, acceso y auditoria.
- `RQ-001` Monitoreo multicanal (prensa, radio, TV, digital, redes).
- `RQ-007` Disponibilidad minima de plataforma (SLA) y continuidad.
- `RQ-201` Cobertura amplia (nacional, regional, local).
- `RQ-202` Sistema de indexacion y clasificacion.
- `A-003` Universo exacto de medios y politica de ampliacion.

## Epicas iniciales

1. Crear host API en `src/Api/Operations.Api.Host` con wiring limpio a Application.
2. Definir contrato de consulta unificado sobre dataset global de monitoreo y filtros avanzados.
3. Exponer endpoints de evidencias y exportaciones abiertas (CSV/JSON estructurado).
4. Incorporar capa inicial de autenticacion/autorizacion y trazabilidad.
5. Incorporar modulo de alerting por tenant (reglas, suscripciones y canales) desacoplado de la ingesta.
6. Separar la ejecucion operativa en dos workers base (`ContinuousIngestionWorker` y `DiscreteIngestionWorker`).
7. Introducir `Plugin Registry` y `Plugin Profiles` para resolver procesamiento por `media` y override por `platform`.

## Historias tecnicas prioritarias

1. Bootstrapping API host con DI, logging estructurado y health endpoints.
2. Endpoint de consulta paginada sobre dataset global (medio, plataforma, rango temporal) con proyeccion por tenant en capa de permisos y alertas.
3. Endpoint de exportacion de resultados filtrados en CSV/JSON.
4. Contratos de DTO versionados y pruebas de compatibilidad.
5. Pruebas de integracion para consultas y exportacion.
6. CRUD de reglas de alerta por tenant y evaluacion sobre eventos globales de ingesta.
7. Contrato `IIngestionPluginResolver` para resolver plugin por `media` con reglas de override por `platform`.
8. Contrato `IPluginProfileProvider` para soportar catalogo de perfiles desde archivo y/o BD.
9. Implementacion de host `ContinuousIngestion.Worker` con orquestacion continua y metricas de continuidad.
10. Implementacion de host `DiscreteIngestion.Worker` con ejecucion puntual/programada y controles de concurrencia.
11. Pruebas de contrato para proveedores de perfiles de plugin (JSON inicial y proveedor alterno).
12. Pruebas de no regresion para radio/TV en worker continuo durante canary de separacion.

## Evidencias esperadas

1. `dotnet test` en `MediaOpsCore.sln` con suite API incluida.
2. Coleccion de pruebas HTTP automatizadas para endpoints criticos.
3. Mapeo de requisitos actualizado en `REQUIREMENTS_LIVE_MATRIX.md`.
4. Registro de decisiones de seguridad y auditoria en documentos de fase.
5. Evidencia de resolucion deterministica `media -> plugin profile` y `media+platform -> override`.
6. Evidencia de ejecucion simultanea de workers continuo/discreto en la misma maquina de stage.

## Rollout y rollback

1. Activacion en stage con feature flags por endpoint.
2. Validacion operativa en stage antes de habilitar consumo productivo.
3. Rollback por desactivacion de endpoints y reversion de version de host API.