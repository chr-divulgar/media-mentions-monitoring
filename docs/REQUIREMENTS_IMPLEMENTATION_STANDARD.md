# Requirements-First Implementation Standard (AI-Agnostic)

## 1) Objetivo

Definir un estándar de implementación orientado a requisitos contractuales, reusable con cualquier IA o equipo humano, para asegurar que cada cambio de código tenga trazabilidad hacia los documentos fuente y evidencia verificable.

Documentos fuente obligatorios:

- `PLAN_MAESTRO.md` (raíz del repo)
- `docs/3. ANEXO_TECNICO_MONITOREO_FINAL_25-05-2026 (2) Posperidad social.md`
- `docs/Ficha Tecnica Monitoreo de Medios.md`
- `docs/CODING_STANDARDS_DOTNET10.md`

## 2) Principio Rector

Requirements-first:

1. El requerimiento define el cambio.
2. El diseño y el código se adaptan al requerimiento.
3. Si existe conflicto entre implementación actual y requisito, prevalece el requisito.
4. Ante ambigüedad del pliego se construye la lectura más exigente, configurable por cliente.

## 3) Protocolo Mínimo Antes de Implementar

Todo cambio debe iniciar con:

1. Identificar `Requirement ID` en `PLAN_MAESTRO.md` (`RQ-xxx` o `AMB-xxx`).
2. Citar fuente (`ANEXO`, `Ficha` o ambos).
3. Registrar tipo de cambio: funcional, no funcional, cumplimiento, deuda técnica.
4. Definir evidencia de aceptación (pruebas, exportes, reportes, trazas).

Si no existe `Requirement ID`, se debe crear/actualizar en `PLAN_MAESTRO.md` antes de codificar.

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

## 9) Gobernanza

1. `PLAN_MAESTRO.md` (raíz del repo) es la fuente única de requisitos (`RQ-xxx`), ambigüedades (`AMB-xxx`), fases y % de avance.
2. Al cerrar un entregable, actualizar en `PLAN_MAESTRO.md` el estado (`N/P/C`), el % y la evidencia del `RQ-xxx` afectado.
3. Un `RQ-xxx` no pasa a `C` sin evidencia verificable.
4. Mantener consistencia con `docs/CODING_STANDARDS_DOTNET10.md`.

## 10) Checklist Rápido (Pre-merge)

1. ¿Qué requisito exacto se cumple?
2. ¿Dónde está la evidencia verificable?
3. ¿Qué prueba evita regresión?
4. ¿La solución mantiene portabilidad de datos?
5. ¿Se actualizó el estado y el % del `RQ-xxx` en `PLAN_MAESTRO.md`?
