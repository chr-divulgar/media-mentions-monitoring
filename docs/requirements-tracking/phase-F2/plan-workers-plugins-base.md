# Plan Base Fase 2 - Separacion de Workers y Plugins

Fecha de propuesta: 2026-06-03

## Estado de implementacion (2026-06-04)

1. Etapa 1 implementada en codigo:
   - Contratos `IIngestionPluginResolver`, `IPluginProfileProvider`, `PluginExecutionPlan`, `PluginProfile`, `IngestionMode`.
   - Provider inicial `JsonPluginProfileProvider`.
   - Resolver `MediaPlatformIngestionPluginResolver` con prioridad `media+platform` y fallback `media`.
   - Sin fallback legacy: fuentes sin perfil de plugin quedan fuera de ejecucion.
2. Etapa 2 base implementada en codigo:
   - `ContinuousIngestionWorker` y `DiscreteIngestionWorker` como hosted services separados.
   - `ContinuousIngestionOrchestrator` y `DiscreteIngestionOrchestrator`.
   - Registro DI actualizado para ejecutar ambos workers.
3. Etapa 3 implementada para migracion canary radio/TV:
   - Filtro configurable de worker continuo por medios (`ContinuousMediaAllowList`), valor por defecto `radio,video`.
   - Integracion del filtro de medios antes del canary por plataformas para controlar migracion por subconjunto.
   - Perfiles iniciales de plugin para `radio` y `video` + override `youtube` en `stage/plugin-profiles.json`.
4. Evidencia de validacion:
   - Unit tests de Etapa 1/2/3 (resolver, provider JSON, filtros canary/media): 9/9 OK.
   - Unit tests completos del proyecto: 12/12 OK.
   - Architecture tests: 4/4 OK.
5. Pendiente de este plan:
   - Etapa 4 (plugins discretos funcionales web/social/pdf y scheduler operativo).
6. Politica de evidencia operativa vigente:
   - Persistencia local por defecto en ruta de archivos (`stageFilesystemRootPath`).
   - Si existe al menos un adapter de DB configurado y el upsert es exitoso, la evidencia local se elimina para evitar crecimiento indefinido.
   - Si no hay DB configurada o falla la persistencia en DB, la evidencia local se conserva como respaldo operativo.

## Requirement IDs impactados

- `RQ-001` Monitoreo multicanal (prensa, radio, TV, digital, redes).
- `RQ-004` Base de datos estructurada consultable.
- `RQ-007` Disponibilidad minima de plataforma (SLA) y continuidad.
- `RQ-201` Cobertura amplia (nacional, regional, local).
- `RQ-202` Sistema de indexacion y clasificacion.
- `A-003` Universo exacto de medios y politica de ampliacion.

## Supuestos y alcance de esta propuesta

1. El catalogo base de fuentes se mantiene sin cambios estructurales:
   - `sourceId`
   - `platform`
   - `media`
   - `streamUrl`
2. Radio y TV continuan en flujo de ingesta continua para esta fase.
3. No se incluyen en esta fase casos especiales de autenticacion (por ejemplo cookies) ni reglas de proveedor especifico.
4. El objetivo de la fase es dejar la arquitectura escalable para incorporar nuevos medios sin romper flujos existentes.

## Objetivo tecnico

Separar la ejecucion operativa por modo de ingesta y desacoplar la logica especifica de cada fuente mediante plugins, manteniendo un modelo de fuente estable y una resolucion operativa por perfil.

## Arquitectura objetivo (base)

1. `ContinuousIngestionWorker`
   - Procesa fuentes de ingesta continua (`radio`, `video` live).
   - Ejecuta ciclo permanente con guardrails de continuidad.
2. `DiscreteIngestionWorker`
   - Procesa fuentes de ejecucion puntual o programada (`prensa`, `internet`, `redes`, `documentos`).
   - Ejecuta por lote/cron/evento.
3. `Plugin Registry`
   - Resuelve plugin y perfil operativo por `media` y override opcional por `platform`.
4. `Source Catalog` (archivo o BD)
   - Se mantiene como fuente de verdad de las fuentes activas.
5. `Plugin Profiles Catalog` (archivo o BD)
   - Define ejecutable, plantilla de argumentos, timeout, retries y limites por plugin.

## Regla de resolucion de plugin

1. Resolver por `media`.
2. Aplicar override por `platform` solo cuando exista regla explicita.
3. Si no existe override, usar perfil por defecto de `media`.
4. Si no existe perfil para `media`, retornar error de configuracion.

## Modulos, puertos y adapters afectados

### Application / Domain (nuevos contratos)

1. `IIngestionPluginResolver`
2. `IPluginProfileProvider`
3. `IContinuousIngestionOrchestrator`
4. `IDiscreteIngestionOrchestrator`
5. `PluginExecutionPlan` (modelo canonico)
6. `IngestionMode` (`continuous`, `discrete`)

### Infrastructure (nuevos adapters)

1. `JsonPluginProfileProvider` (fase inicial)
2. `DbPluginProfileProvider` (fase posterior)
3. `ContinuousPluginRunner` (adapter runner continuo)
4. `DiscretePluginRunner` (adapter runner discreto)

### Worker hosts

1. `Workers/ContinuousIngestion.Worker`
2. `Workers/DiscreteIngestion.Worker`

## Plan de implementacion incremental

## Etapa 1 - Contratos y resolucion (sin romper flujo actual)

1. Introducir contratos de resolucion de plugin y perfiles.
2. Crear catalogo de perfiles en archivo (`plugin-profiles.json`).
3. Implementar resolucion por `media` con override opcional por `platform`.
4. Mantener worker actual operativo como compatibilidad temporal.

Evidencia minima:

1. Unit tests de resolucion de perfil (`media` y `platform`).
2. Validacion de error para medias no mapeados.

## Etapa 2 - Separacion de workers base

1. Crear `ContinuousIngestionWorker`.
2. Crear `DiscreteIngestionWorker`.
3. Mover orquestacion por modo de ingesta sin duplicar reglas de negocio.
4. Mantener infraestructura compartida de evidencia y metricas.

Evidencia minima:

1. Unit tests de orquestacion por worker.
2. Pruebas de humo en stage para ambos workers en la misma maquina.
3. Verificacion de ciclo de evidencia: local solo como buffer y limpieza tras confirmacion de persistencia en DB.

## Etapa 3 - Migracion de radio y TV al worker continuo

1. Asignar perfiles de plugin para `radio` y `video`.
2. Ejecutar canary de separacion por subconjunto de fuentes.
3. Validar no regresion de continuidad y latencia operativa.

Evidencia minima:

1. `capture_success_rate` estable.
2. `pipeline_lag_seconds` dentro de umbral.
3. Registro de rollback probado.

## Etapa 4 - Preparacion de medios discretos

1. Definir stubs de plugins discretos (`web`, `social`, `pdf`) sin activar procesamiento masivo.
2. Activar planificador de ejecucion puntual para worker discreto.
3. Preparar contratos de salida para indexacion y clasificacion.

Evidencia minima:

1. Ejecuciones discretas de prueba con artefactos canonicamente persistidos.
2. Traza por `sourceId` y `pluginId` para auditoria operativa.

## Configuracion objetivo (separada)

1. Catalogo de fuentes (no se modifica): archivo o BD de `sources`.
2. Catalogo de perfiles de plugin: archivo o BD de `pluginProfiles`.
3. Mapeo `media -> pluginProfile` y override `media + platform -> pluginProfile`.

## Riesgos y mitigacion

1. Riesgo: acoplar la seleccion de plugin a hardcode en worker.
   - Mitigacion: resolver siempre via `IIngestionPluginResolver`.
2. Riesgo: duplicar logica entre worker continuo y discreto.
   - Mitigacion: orquestadores de Application reutilizables y hosts delgados.
3. Riesgo: deriva de configuracion entre archivo y BD.
   - Mitigacion: contrato unico de `IPluginProfileProvider` con tests de contrato.

## Rollout y rollback

1. Rollout por feature flag de worker discreto y por subconjunto de fuentes.
2. Rollback por desactivar worker nuevo y retornar al worker anterior temporal.
3. Criterio de rollback: degradacion sostenida de continuidad o aumento de error critico.

## Criterios de aceptacion

1. Dos workers operando en la misma maquina sin interferencia critica.
2. Seleccion de plugin resuelta por `media` con soporte de override por `platform`.
3. Fuentes base no modificadas en su estructura contractual.
4. Evidencia de pruebas automatizadas y de stage para cada etapa.

## Nota de evolucion

Esta base define la primera separacion por modo operativo. La separacion adicional (por ejemplo audio vs video) se decide despues con metricas de carga, confiabilidad e impacto cruzado.