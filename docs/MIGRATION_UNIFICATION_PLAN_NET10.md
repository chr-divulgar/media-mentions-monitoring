# Plan General de Migracion y Unificacion a .NET 10

## 1. Objetivo

Migrar y unificar progresivamente la plataforma actual (media-monitor, media-mentions-monitoring y media-monitor-helper) hacia una arquitectura basada en .NET 10 capaz de cumplir al 100% los requisitos ya establecidos en:

- 3. ANEXO_TECNICO_MONITOREO_FINAL_25-05-2026 (2) Posperidad social.md
- Ficha Tecnica Monitoreo de Medios.md

## 2. Principios de Ejecucion

1. Requisitos primero: el codigo se adapta al contrato.
2. Cero perdida de operacion: migracion con convivencia temporal (strangler pattern).
3. Entregables verificables por fase: cada fase cierra con criterios de aceptacion.
4. Trazabilidad completa: cada requisito debe mapearse a componente, prueba y evidencia.
5. Riesgo controlado: despliegue progresivo, rollback plan y observabilidad desde el inicio.

## 3. Alcance de Unificacion (Target)

1. Core backend y workers en .NET 10.
2. Modelo de datos unificado (multi-tenant y auditable).
3. Motor unico de alertas, reportes e historico contractual.
4. API unificada para UI y consumo externo.
5. Pipeline operativo para captura, indexacion, clasificacion, evidencia y entrega.

## 4. Arquitectura Objetivo (Alto Nivel)

1. Ingestion Workers (.NET 10 Worker Services): radio, TV, digital, social, crawlers.
2. Processing Workers (.NET 10): clasificacion, deduplicacion, enrichments, reglas.
3. Alert Engine (.NET 10): SLA, canales, priorizacion, trazabilidad.
4. Reporting Engine (.NET 10): diarios/semanales/mensuales/especiales.
5. Evidence Manager: PDF/MP3/MP4, versionado, inventario, entrega contractual.
6. Unified API (.NET 10): consulta, filtros, exportacion, administracion.
7. Auth/RBAC/Audit: control de acceso, actividad y evidencia de cumplimiento.
8. UI actual se mantiene inicialmente y se adapta por contrato de API.

## 5. Roadmap por Fases

### Fase 0 - Preparacion y Control (2 semanas)

Objetivo:

- Congelar alcance de migracion y definir baseline tecnico/funcional.

Actividades:

1. Crear matriz maestra de requisitos (ambos documentos) con estado actual.
2. Definir RACI, backlog y governance de cambios.
3. Definir NFRs objetivo (SLA alertas, disponibilidad, seguridad, auditoria).
4. Preparar ambientes Dev/QA/Stage/Prod.

Entregables:

1. Requisitos normalizados y priorizados.
2. Plan de riesgos + plan de rollback.
3. KPIs de migracion.

Criterio de cierre:

- Requisitos y criterios de aceptacion firmados.

---

### Fase 1 - Fundacion .NET 10 (3 semanas)

Objetivo:

- Establecer la base tecnica unificada.

Actividades:

1. Crear solution .NET 10 con arquitectura modular (Clean/Vertical Slice).
2. Definir contratos de dominio comunes: Mention, Alert, Evidence, Report, Tenant.
3. Definir capa de infraestructura (Mongo/Storage/Queues/Observability).
4. Establecer CI/CD, quality gates y convenciones de versionado.

Entregables:

1. Repositorio unificado .NET 10 inicial.
2. Pipeline CI/CD funcional.
3. Contratos de datos versionados.

Criterio de cierre:

- Build/release automatizado y contratos estables para integracion.

---

### Fase 2 - Migracion de Captura y Procesamiento (4 a 6 semanas)

Objetivo:

- Portar el core operativo de grabacion y segmentacion a workers .NET 10.

Actividades:

1. Migrar logica de captura continua (ffmpeg/yt-dlp/process lifecycle).
2. Migrar segmentacion y deteccion incremental.
3. Migrar control de procesos, health-check y autorecovery.
4. Implementar observabilidad operativa (CPU, lag, errores, colas).

Entregables:

1. Worker de captura .NET 10 en paralelo al actual.
2. Worker de segmentacion .NET 10.
3. Dashboard operativo de estabilidad.

Criterio de cierre:

- Captura estable en paralelo con paridad funcional >= 95%.

---

### Fase 3 - Migracion de Alertas y Reglas (3 a 4 semanas)

Objetivo:

- Unificar motor de alertas para ambos tipos de cliente.

Actividades:

1. Implementar reglas parametrizables por tenant (palabras, voceros, temas, riesgo).
2. Unificar severidades y trazabilidad de alertas.
3. Implementar SLA de alertas con metricas auditables.
4. Integrar canales de salida (email/WhatsApp y otros necesarios).

Entregables:

1. Alert Engine .NET 10 multi-tenant.
2. Registro historico de alertas auditable.

Criterio de cierre:

- Alertas generadas y entregadas con evidencia de cumplimiento de SLA.

---

### Fase 4 - Migracion de API y Frontend (4 semanas)

Objetivo:

- Exponer API unificada y adaptar UI sin romper operacion.

Actividades:

1. Implementar API unificada para consulta, filtros, exportacion y admin.
2. Adaptar frontend actual por contratos API versionados.
3. Implementar RBAC, auditoria y control de actividad.
4. Activar exportaciones en formatos contractuales.

Entregables:

1. API v1 estable.
2. UI conectada a backend .NET 10.

Criterio de cierre:

- Operacion funcional end-to-end en Stage con usuarios de prueba.

---

### Fase 5 - Historico Contractual y Entregables (3 semanas)

Objetivo:

- Cumplir exigencia de historico, evidencia y entrega formal.

Actividades:

1. Definir politica de retencion por contrato/tenant.
2. Implementar inventory + empaquetado de historico (base + soportes).
3. Implementar flujo de entrega (Excel/CSV/PDF/MP3/MP4) verificable.
4. Validar integridad y trazabilidad (registro a soporte).

Entregables:

1. Evidence Manager operativo.
2. Proceso de cierre contractual reproducible.

Criterio de cierre:

- Simulacro de entrega final aprobado por QA funcional.

---

### Fase 6 - Analitica Avanzada y Modulos Especiales (4 a 6 semanas)

Objetivo:

- Cerrar requisitos avanzados del anexo (semaforo, indices, desinformacion, free press formalizado).

Actividades:

1. Implementar semaforo reputacional con metodologia versionada.
2. Implementar indice de posicionamiento y share of voice.
3. Implementar matriz de valoracion mediatica y free press.
4. Implementar monitoreo de desinformacion (reglas + validacion).

Entregables:

1. Modulos avanzados habilitables por tenant.
2. Documentacion metodologica y evidencia de calculo.

Criterio de cierre:

- Indicadores y reportes avanzados validados con data real.

---

### Fase 7 - Prueba Integral de Cumplimiento (2 a 3 semanas)

Objetivo:

- Verificar cumplimiento 100% requisitos funcionales y no funcionales.

Actividades:

1. Ejecutar matriz de cumplimiento requisito por requisito.
2. Pruebas de carga, resiliencia, seguridad y continuidad.
3. UAT con escenarios de ambos perfiles de cliente.
4. Cierre de hallazgos y hardening final.

Entregables:

1. Acta tecnica de cumplimiento.
2. Lista de evidencias por requisito.

Criterio de cierre:

- Cumplimiento total validado y sin bloqueadores de salida.

---

### Fase 8 - Cutover y Decomisionamiento (1 a 2 semanas)

Objetivo:

- Pasar a operacion plena en .NET 10 y retirar componentes legacy.

Actividades:

1. Cutover progresivo por tenant.
2. Monitoreo reforzado post-go-live.
3. Decomisionamiento controlado de piezas legacy.
4. Transferencia operativa y runbook final.

Entregables:

1. Operacion productiva estable.
2. Runbook operativo y plan de mejora continua.

Criterio de cierre:

- Operacion estable en ventana acordada y soporte normalizado.

## 6. Matriz de Cumplimiento (Plantilla)

Completar para cada requisito contractual:

| Requisito                   | Fuente        | Componente objetivo .NET 10 | Estado (N/P/C) | Evidencia de prueba       | Fecha objetivo |
| --------------------------- | ------------- | --------------------------- | -------------- | ------------------------- | -------------- |
| Ejemplo: Alertas inmediatas | ANEXO / Ficha | Alert Engine                | N              | Test SLA + logs + reporte | YYYY-MM-DD     |

Leyenda:

- N: No iniciado
- P: En progreso
- C: Cumplido

## 7. Cronograma Macro Sugerido

1. F0-F1: 5 semanas
2. F2-F4: 11 a 14 semanas
3. F5-F6: 7 a 9 semanas
4. F7-F8: 3 a 5 semanas

Total estimado: 26 a 33 semanas (dependiendo de complejidad y recursos).

## 8. Equipo Minimo Recomendado

1. Arquitecto de solucion (1)
2. Backend .NET 10 (2 a 4)
3. Data/Integraciones/Workers (1 a 2)
4. Frontend (1 a 2)
5. QA funcional y automatizacion (1 a 2)
6. DevOps/SRE (1)
7. Analista funcional de requisitos contractuales (1)

## 9. Riesgos Principales y Mitigaciones

1. Ambiguedad contractual de SLA alertas.

- Mitigacion: aclaracion formal previa y SLA unico firmado.

2. Diferencias entre perfiles de cliente (operativo vs estrategico).

- Mitigacion: modularidad por tenant y feature flags.

3. Riesgo de degradacion durante migracion.

- Mitigacion: convivencia temporal, canary release y rollback.

4. Inconsistencia de datos historicos.

- Mitigacion: migracion controlada con reconciliacion automatica.

## 10. Definicion de Exito

Se considera exito cuando:

1. Todos los requisitos de ambos documentos estan en estado Cumplido.
2. La operacion corre en .NET 10 sin dependencia critica legacy.
3. Existe evidencia auditable por requisito.
4. El sistema soporta configuracion por cliente sin rediseño.

## 11. Proximos Pasos Inmediatos

1. Aprobar este plan marco.
2. Crear matriz de requisitos detallada (hoja viva).
3. Arrancar Fase 0 con backlog tecnico y funcional firmado.
