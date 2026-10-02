# Matriz Viva de Requisitos

## 1. Objetivo

Controlar el avance al 100% de cumplimiento de requisitos contractuales de:

- 3. ANEXO_TECNICO_MONITOREO_FINAL_25-05-2026 (2) Posperidad social.md
- Ficha Tecnica Monitoreo de Medios.md

Esta matriz es el artefacto principal de la Fase 0 del plan de migracion/unificacion a .NET 10.

## 2. Leyenda

- Estado:
  - N: No iniciado
  - P: En progreso
  - C: Cumplido
  - R: Riesgo/Bloqueo
- Prioridad:
  - Critica
  - Alta
  - Media

## 3. Estructura de Seguimiento

| ID  | Requisito | Fuente | Prioridad | Componente Objetivo (.NET 10) | Estado | Brecha Actual | Cambio Requerido | Evidencia de Prueba | Responsable | Fecha Objetivo | Riesgo |
| --- | --------- | ------ | --------- | ----------------------------- | ------ | ------------- | ---------------- | ------------------- | ----------- | -------------- | ------ |

## 4. Requisitos CORE (Prellenado Inicial)

| ID     | Requisito                                                | Fuente | Prioridad | Componente Objetivo (.NET 10)         | Estado | Brecha Actual                                                            | Cambio Requerido                                                 | Evidencia de Prueba                                                       | Responsable | Fecha Objetivo | Riesgo |
| ------ | -------------------------------------------------------- | ------ | --------- | ------------------------------------- | ------ | ------------------------------------------------------------------------ | ---------------------------------------------------------------- | ------------------------------------------------------------------------- | ----------- | -------------- | ------ |
| RQ-001 | Monitoreo multicanal (prensa, radio, TV, digital, redes) | Ambos  | Critica   | Ingestion Workers + Source Connectors | N      | Cobertura desigual por fuente y cliente                                  | Unificar conectores y reglas por tenant                          | Pruebas E2E por canal y fuente                                            | Pendiente   | YYYY-MM-DD     | Medio  |
| RQ-002 | Plataforma web de consulta con filtros                   | Ambos  | Critica   | Unified API + Web UI                  | P      | Filtros y UX no homologados                                              | Definir `Operations.Api.Host` y contrato unico de API/filtros    | Backlog Fase 2 versionado + casos UAT de busqueda/filtro                  | Pendiente   | 2026-07-15     | Medio  |
| RQ-003 | Alertas automaticas operativas                           | Ambos  | Critica   | Alert Engine + Notifier               | N      | Reglas dispersas y SLA ambiguo                                           | Motor unico de reglas y trazabilidad                             | Logs SLA + reporte de entrega                                             | Pendiente   | YYYY-MM-DD     | Alto   |
| RQ-004 | Base de datos estructurada consultable                   | Ambos  | Critica   | Data Model Unificado + Repositorios   | P      | Persistencia unificada en avance, falta consolidar consultas de producto | Consolidar consultas y exportes sobre modelo canonico            | Pruebas de paridad por coleccion + validacion de esquema/consultas        | Pendiente   | 2026-06-28     | Medio  |
| RQ-005 | Exportacion (Excel/CSV/PDF y soportes)                   | Ambos  | Critica   | Reporting Engine + Export Module      | N      | Exportes no estandarizados                                               | Flujo de exportacion contractual unico                           | Archivos de salida validados                                              | Pendiente   | YYYY-MM-DD     | Medio  |
| RQ-006 | Seguridad, acceso y auditoria                            | Ambos  | Critica   | Auth/RBAC/Audit                       | N      | Auditoria parcial y roles no unificados                                  | Implementar RBAC y auditoria transversal                         | Evidencia de trazas y permisos                                            | Pendiente   | YYYY-MM-DD     | Medio  |
| RQ-007 | Disponibilidad minima de plataforma (SLA) y continuidad  | ANEXO  | Alta      | Observability + SRE Controls          | P      | SLO contractual formal y dashboard consolidado aun pendientes            | Completar panel SLI/SLO y automatizar evidencias de contingencia | Pruebas automáticas de canary 20-50-100 + evidencia de rollback y paridad | Pendiente   | 2026-06-21     | Alto   |

## 5. Requisitos Operativos IDRD (Prellenado Inicial)

| ID     | Requisito                                 | Fuente | Prioridad | Componente Objetivo (.NET 10)         | Estado | Brecha Actual                             | Cambio Requerido                            | Evidencia de Prueba             | Responsable | Fecha Objetivo | Riesgo |
| ------ | ----------------------------------------- | ------ | --------- | ------------------------------------- | ------ | ----------------------------------------- | ------------------------------------------- | ------------------------------- | ----------- | -------------- | ------ |
| RQ-101 | Portal con acceso de usuarios autorizados | Ficha  | Critica   | Auth/RBAC + Web Portal                | N      | Control de usuarios no consolidado        | Provisionamiento y perfiles por rol         | Prueba acceso/roles             | Pendiente   | YYYY-MM-DD     | Bajo   |
| RQ-102 | Alertas por correo y WhatsApp             | Ficha  | Critica   | Alert Engine + Multi-channel Notifier | N      | Integracion y gobernanza no centralizadas | Motor de plantillas/canales y tracking      | Evidencia de envio y entrega    | Pendiente   | YYYY-MM-DD     | Medio  |
| RQ-103 | Reportes diarios de medios y redes        | Ficha  | Critica   | Reporting Scheduler + Templates       | N      | Cadencia/reportes no unificados           | Orquestar calendario y formatos por cliente | Entrega automatizada y validada | Pendiente   | YYYY-MM-DD     | Alto   |
| RQ-104 | Informe mensual con analisis y free press | Ficha  | Alta      | Reporting Engine + Free Press Module  | N      | Metodo de free press no estandarizado     | Formula versionada y auditable              | Informe mensual firmado QA      | Pendiente   | YYYY-MM-DD     | Medio  |
| RQ-105 | Palabras clave dinamicas por supervisor   | Ficha  | Alta      | Tenant Config + Rule Store            | N      | Cambios manuales no gobernados            | Panel de configuracion + versionado         | Historial de cambios aplicado   | Pendiente   | YYYY-MM-DD     | Bajo   |

## 6. Requisitos Estrategicos ANEXO (Prellenado Inicial)

| ID     | Requisito                                    | Fuente | Prioridad | Componente Objetivo (.NET 10)          | Estado | Brecha Actual                             | Cambio Requerido                             | Evidencia de Prueba                       | Responsable | Fecha Objetivo | Riesgo |
| ------ | -------------------------------------------- | ------ | --------- | -------------------------------------- | ------ | ----------------------------------------- | -------------------------------------------- | ----------------------------------------- | ----------- | -------------- | ------ |
| RQ-201 | Cobertura amplia (nacional, regional, local) | ANEXO  | Critica   | Source Coverage Manager                | N      | Cobertura y catalogo sin gobernanza unica | Modelo de cobertura por tenant y region      | Mapa de cobertura y muestreo              | Pendiente   | YYYY-MM-DD     | Alto   |
| RQ-202 | Sistema de indexacion y clasificacion        | ANEXO  | Critica   | Processing Workers + Indexing Pipeline | N      | Indexacion heterogenea por flujo          | Pipeline unico de indexado y tags            | Pruebas de recuperacion por filtros       | Pendiente   | YYYY-MM-DD     | Medio  |
| RQ-203 | Almacenamiento historico contractual         | ANEXO  | Critica   | Evidence Manager + Retention Policy    | N      | Politica de historico no unificada        | Implementar retencion y entrega formal       | Simulacro de entrega contractual          | Pendiente   | YYYY-MM-DD     | Alto   |
| RQ-204 | Registro historico de alertas                | ANEXO  | Critica   | Alert Audit Store                      | N      | Trazabilidad incompleta                   | Ledger de alertas con metadatos obligatorios | Consulta y export del historial           | Pendiente   | YYYY-MM-DD     | Medio  |
| RQ-205 | Semaforo reputacional                        | ANEXO  | Alta      | Reputation Module                      | N      | Sin modulo transversal productizado       | Implementar reglas y umbrales versionados    | Reporte con clasificacion y justificacion | Pendiente   | YYYY-MM-DD     | Alto   |
| RQ-206 | Indicadores de inteligencia mediatica        | ANEXO  | Alta      | Metrics Engine                         | N      | Indicadores dispersos/no normalizados     | Motor unico de indicadores y series          | Dashboard + export de indicadores         | Pendiente   | YYYY-MM-DD     | Medio  |
| RQ-207 | Share of Voice institucional                 | ANEXO  | Media     | Advanced Analytics Module              | N      | Comparativos no formalizados              | Modelo comparativo por universo definido     | Reporte comparativo validado              | Pendiente   | YYYY-MM-DD     | Medio  |
| RQ-208 | Monitoreo de desinformacion                  | ANEXO  | Alta      | Disinformation Module                  | N      | Capacidad no implementada end-to-end      | Reglas de deteccion + flujo de validacion    | Casos de prueba de deteccion              | Pendiente   | YYYY-MM-DD     | Alto   |
| RQ-209 | Matriz de valoracion mediatica               | ANEXO  | Alta      | Scoring Module                         | N      | Metodo de ponderacion no centralizado     | Matriz parametrizable y versionada           | Tabla de calculo + evidencia              | Pendiente   | YYYY-MM-DD     | Medio  |

## 7. Requisitos Con Ambiguedad Contractual (Resolver Antes de Cotizar Cierre)

| ID    | Tema                                                             | Riesgo | Pregunta de aclaracion                                                 | Estado    |
| ----- | ---------------------------------------------------------------- | ------ | ---------------------------------------------------------------------- | --------- |
| A-001 | SLA de alertas (5 min vs 10/15 min en apartados distintos)       | Alto   | Cual es el SLA oficial y como se mide?                                 | Pendiente |
| A-002 | Alcance de historico (mencion relacionada vs grabacion completa) | Alto   | Se exige conservar solo evidencias relacionadas o señal completa 24/7? | Pendiente |
| A-003 | Universo exacto de medios y politica de ampliacion               | Medio  | Lista cerrada o ampliable sin costo?                                   | Pendiente |
| A-004 | Formula oficial de free press                                    | Medio  | Metodo y fuente de tarifas obligatorias?                               | Pendiente |

## 8. Reglas de Uso de la Matriz

1. No mover un requisito a C (Cumplido) sin evidencia verificable.
2. Cada requisito debe tener al menos una prueba funcional y una evidencia de salida.
3. Todo requisito en R (Riesgo) debe tener plan de mitigacion y fecha de resolucion.
4. Revisar esta matriz semanalmente en comite tecnico-funcional.

## 9. Corte Semanal (Plantilla)

- Semana:
- Requisitos N -> P:
- Requisitos P -> C:
- Requisitos con bloqueo (R):
- Riesgo mayor de la semana:
- Decision tomada:
- Proxima meta:

## 11. Corte Semanal - Semana 8 (Fase 1A)

- Semana: 8
- Requisitos N -> P: `RQ-002`, `RQ-004`, `RQ-007`
- Requisitos P -> C: Ninguno
- Requisitos con bloqueo (R): Ninguno
- Riesgo mayor de la semana: Falta de dashboard SLI/SLO contractual consolidado para cierre formal de `RQ-007`
- Decision tomada: Completar cierre técnico F1A con escalamiento canary 20-50-100 y preparar backlog de Fase 2 para API .NET 10
- Proxima meta: Iniciar implementación de `Operations.Api.Host` y cerrar evidencias funcionales de consulta/exportación

## 10. Proximo Paso Recomendado

1. Completar responsables y fechas objetivo por requisito.
2. Vincular cada requisito a una historia/epica en backlog.
3. Iniciar cierre de ambiguedades contractuales A-001 a A-004 en paralelo con Fase 0.
