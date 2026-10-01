# Requirements-First Implementation Standard (AI-Agnostic)

## 1) Objetivo

Definir un estándar de implementación orientado a requisitos contractuales, reusable con cualquier IA o equipo humano, para asegurar que cada cambio de código tenga trazabilidad hacia los documentos fuente y evidencia verificable.

Documentos fuente obligatorios:

- `3. ANEXO_TECNICO_MONITOREO_FINAL_25-05-2026 (2) Posperidad social.md`
- `Ficha Tecnica Monitoreo de Medios.md`
- `REQUIREMENTS_LIVE_MATRIX.md`
- `PROPOSAL_CONTEXT_BASE.md`
- `CODING_STANDARDS_DOTNET10.md`

## 2) Principio Rector

Requirements-first:

1. El requerimiento define el cambio.
2. El diseño y el código se adaptan al requerimiento.
3. Si existe conflicto entre implementación actual y requisito, prevalece el requisito (salvo aclaración contractual formal).

## 3) Protocolo Mínimo Antes de Implementar

Todo cambio debe iniciar con:

1. Identificar `Requirement ID` de la matriz (`RQ-xxx` o `A-xxx`).
2. Citar fuente (`ANEXO`, `Ficha` o ambos).
3. Registrar tipo de cambio: funcional, no funcional, cumplimiento, deuda técnica.
4. Definir evidencia de aceptación (pruebas, exportes, reportes, trazas).

Si no existe `Requirement ID`, se debe crear/actualizar en `REQUIREMENTS_LIVE_MATRIX.md` antes de codificar.

## 4) Regla de Trazabilidad Obligatoria

Cada PR, commit o tarea técnica debe incluir:

- Requirement IDs impactados.
- Módulos/componentes afectados.
- Pruebas agregadas/actualizadas.
- Evidencia de cumplimiento esperada.

Formato recomendado:

`REQ: RQ-004, RQ-005 | MOD: Capture, Reporting | TEST: unit+integration | EVIDENCE: export CSV + validacion de esquema`

## 5) Estándar de Datos y Portabilidad (No Lock-in)

Desde día 1 se requiere:

1. Modelo canónico de datos de monitoreo (independiente del motor).
2. Capa de repositorios/puertos para desacoplar dominio del proveedor de datos.
3. Exportación periódica en formato abierto (CSV/Excel/JSON estructurado).
4. Scripts versionados de migración (ejemplo: Firebase <-> Mongo o equivalente).
5. Prueba de restauración/reimportación de datos en ambiente de stage.

Esto aplica aunque el motor inicial sea Firebase, Mongo u otro.

## 6) Definición de Cumplimiento por Requisito

Un requisito pasa a `C` (Cumplido) solo si:

1. Tiene implementación productiva verificable.
2. Tiene pruebas automáticas relevantes (unit/integration/contract según aplique).
3. Tiene evidencia funcional de salida (consulta, exportación, log, reporte).
4. Tiene actualización de matriz (`Estado`, `Brecha`, `Cambio`, `Evidencia`, `Fecha`).

## 7) Reglas para Uso de IA (Copilot, GPT, Claude, etc.)

Toda IA usada para analizar o generar código debe:

1. Leer primero documentos fuente y matriz viva.
2. Proponer cambios mapeados a `Requirement IDs`.
3. Evitar generar código que no tenga trazabilidad contractual.
4. Incluir pruebas y criterios de aceptación en su propuesta.
5. Declarar supuestos cuando el requisito sea ambiguo (`A-xxx`).

## 8) Salida Mínima Esperada de una IA para cada cambio

1. Requisitos impactados (`RQ/A IDs`).
2. Diseño propuesto (módulos, puertos/adapters, datos).
3. Lista de archivos a crear/modificar.
4. Pruebas a agregar y evidencia esperada.
5. Riesgos, rollout y rollback.

## 9) Gobernanza Operativa

1. Revisión semanal de `REQUIREMENTS_LIVE_MATRIX.md`.
2. Cierre prioritario de ambigüedades contractuales `A-001..A-004`.
3. No aprobar cambios críticos sin `Requirement ID` y evidencia asociada.
4. Mantener consistencia con `CODING_STANDARDS_DOTNET10.md`.
5. Cada semana de cada fase debe cerrar con actualización documental técnica-contractual común del proyecto (matriz, arquitectura, cumplimiento y trazabilidad).
6. Ninguna fase puede avanzar de semana sin corte documental versionado y trazable por Requirement IDs en documentación común del proyecto.

## 10) Regla de Actualización Semanal por Fase (Mandatory)

Para cada fase (F0, F1A, F1B, etc.), al cierre de cada semana se debe actualizar la documentación técnica común del proyecto exigible por contrato.

Obligatorio por semana:

1. Actualizar `REQUIREMENTS_LIVE_MATRIX.md`:
   - Estado (`N/P/C/R`)
   - Brecha actual
   - Cambio requerido
   - Evidencia de prueba
   - Fecha objetivo/real
2. Actualizar documentación de arquitectura/implementación común del proyecto (módulos, puertos/adapters, decisiones técnicas vigentes, impactos de cumplimiento).
3. Actualizar estado de cumplimiento y riesgos de la fase (bloqueos, supuestos, decisiones y plan de mitigación).
4. Mantener trazabilidad documental por Requirement ID en el índice o documento común que aplique.

Alcance de esta regla:

1. Aplica a documentación técnica y arquitectónica común del proyecto.
2. No implica documentar semanalmente cada cambio técnico por archivo.
3. No implica generar como parte de esta regla reportes funcionales de salida (alertas/notas/estadísticas), salvo obligación contractual explícita independiente.
4. Los entregables funcionales contractuales (informes semanales, mensuales, especiales; alertas/notas; entrega de histórico) se planifican y ejecutan en el plan general de implementación del producto, no en esta gobernanza documental semanal del proyecto.
5. La documentación semanal del proyecto debe incluir explícitamente estado de continuidad del servicio y contingencia técnica (SLA, riesgos, mitigaciones y evidencia disponible).

## 11) Checklist Rápido (Pre-merge)

1. ¿Qué requisito exacto se cumple?
2. ¿Dónde está la evidencia verificable?
3. ¿Qué prueba evita regresión?
4. ¿La solución mantiene portabilidad de datos?
5. ¿La matriz viva fue actualizada?
6. ¿Se actualizó la documentación técnica común de la fase exigible por contrato?
