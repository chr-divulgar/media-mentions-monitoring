# Backlog Semilla Fase 2 - API .NET 10

Fecha de inicio propuesta: 2026-06-10

## Requirement IDs objetivo

- `RQ-002` Plataforma web de consulta con filtros.
- `RQ-004` Base de datos estructurada consultable.
- `RQ-005` Exportacion en formatos abiertos.
- `RQ-006` Seguridad, acceso y auditoria.

## Epicas iniciales

1. Crear host API en `src/Api/Operations.Api.Host` con wiring limpio a Application.
2. Definir contrato de consulta unificado para monitoreo y filtros avanzados.
3. Exponer endpoints de evidencias y exportaciones abiertas (CSV/JSON estructurado).
4. Incorporar capa inicial de autenticacion/autorizacion y trazabilidad.

## Historias tecnicas prioritarias

1. Bootstrapping API host con DI, logging estructurado y health endpoints.
2. Endpoint de consulta paginada por tenant, medio, plataforma y rango temporal.
3. Endpoint de exportacion de resultados filtrados en CSV/JSON.
4. Contratos de DTO versionados y pruebas de compatibilidad.
5. Pruebas de integracion para consultas y exportacion.

## Evidencias esperadas

1. `dotnet test` en `MediaOpsCore.sln` con suite API incluida.
2. Coleccion de pruebas HTTP automatizadas para endpoints criticos.
3. Mapeo de requisitos actualizado en `REQUIREMENTS_LIVE_MATRIX.md`.
4. Registro de decisiones de seguridad y auditoria en documentos de fase.

## Rollout y rollback

1. Activacion en stage con feature flags por endpoint.
2. Shadow read contra flujo legacy antes de habilitar consumo productivo.
3. Rollback por desactivacion de endpoints y reversión de version de host API.