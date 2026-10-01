# Contexto Maestro Para Propuestas

## 1. Proposito

Este documento consolida el contexto tecnico y funcional para generar propuestas comerciales y tecnicas de monitoreo de medios, reutilizando:

- Requisitos contractuales actuales.
- Arquitectura y capacidades existentes en el repositorio.
- Criterios de modularidad para adaptar la oferta por cliente.

Principio rector de este documento:

- Requisitos primero. La arquitectura actual se usa solo como punto de partida (baseline) para estimar esfuerzo, riesgos y tiempos.
- El producto objetivo debe adaptarse para cumplir los requisitos contractuales, no al reves.

Este documento NO ejecuta el plan de migracion/unificacion tecnica. Solo sirve como base para propuestas.

## 2. Fuentes Base Obligatorias

- 3. ANEXO_TECNICO_MONITOREO_FINAL_25-05-2026 (2) Posperidad social.md
- Ficha Tecnica Monitoreo de Medios.md
- graphify-out/GRAPH_REPORT.md
- media-monitor/docs/apps/w-service/overview.md
- media-mentions-monitoring/docs/ARQUITECTURA.md
- media-monitor-helper/docs/flows.md

## 3. Dos Frentes Separados

### Frente A: Migracion y unificacion tecnica (pendiente)

Se mantiene como roadmap. No se ejecuta salvo solicitud explicita.

### Frente B: Generacion de propuestas (activo)

Usa este documento para crear una propuesta real basada en requisitos y arquitectura actual.

## 4. Requisitos Comunes (Nucleo CORE)

Aplican transversalmente a los dos documentos y a cualquier cliente similar:

1. Monitoreo multicanal: prensa, radio, TV, digital y redes.
2. Plataforma web con acceso seguro y filtros.
3. Alertas automaticas por criterios configurables.
4. Base de datos estructurada y consultable.
5. Exportacion de datos y evidencias en formatos abiertos.
6. Analitica base: volumen, favorabilidad, tendencias.
7. Seguridad, control de acceso y trazabilidad.
8. Reporteria periodica con entregables verificables.

## 5. Diferencias Clave Entre Los Dos Pliegos

1. Cobertura geografica: enfoque nacional amplio vs enfoque mas operativo/local.
2. Frecuencia operativa: exigencia mas transaccional en reportes diarios en uno de los casos.
3. Profundidad analitica: semaforo reputacional, indice compuesto, desinformacion, share of voice.
4. Nivel de detalle del catalogo de medios: abierto por categorias vs listado especifico.
5. Interpretacion de SLA de alertas: existen apartados con potencial ambiguedad de tiempos.

## 6. Modelo De Producto Reutilizable

### 6.1 CORE (siempre incluido)

- Ingesta y captura multifuente.
- Indexacion y clasificacion base.
- Motor de alertas configurable.
- Portal web, busqueda y filtros.
- Exportacion (CSV/Excel/PDF y soportes asociados).
- Seguridad y auditoria.

### 6.2 CONFIGURABLE (por cliente)

- Cobertura territorial y universo de medios.
- Palabras clave, voceros, temas y reglas de alertas.
- Frecuencia y formato de reportes.
- Canales de notificacion (correo, WhatsApp, otros).
- Politicas de retencion y ventana de consulta.
- Formula de free press e indicadores.

### 6.3 ADD-ON (opcionales)

- Semaforo reputacional avanzado.
- Deteccion de desinformacion.
- Share of Voice frente a competidores.
- Indice de posicionamiento mediatico compuesto.
- Analitica predictiva o avanzada.

## 7. Mapeo A Arquitectura Actual (Estado Del Repositorio)

Nota de interpretacion:

- Esta seccion describe la capacidad existente hoy para identificar reutilizacion y brechas.
- No implica que el estado actual cumpla por si solo el contrato final.

### 7.1 Capacidades utiles de media-monitor

- Captura continua de streams y procesamiento operativo.
- Segmentacion y manejo de procesos externos (ffmpeg/yt-dlp).
- Flujo orientado a operacion 24/7.

### 7.2 Capacidades utiles de media-mentions-monitoring

- API modular y UI web para gestion y consulta.
- Flujo de transcripcion/resumen/notas.
- Base apta para evolucion a producto configurable por cliente.

### 7.3 Capacidades utiles de media-monitor-helper

- Utilidades de soporte operativo de procesos y limpieza.
- Mantenimiento de consistencia en procesos auxiliares.

### 7.4 Brechas a considerar en propuestas

- Multi-tenant nativo por cliente.
- Homologacion de esquema de datos y contratos de salida.
- Gobernanza unica de SLA y metodologia de indicadores.

### 7.5 Regla de evolucion tecnica

- Si un requisito contractual no esta cubierto por el estado actual, se define ajuste de arquitectura, esfuerzo y costo para cerrarlo.
- No se descartan requisitos por limitaciones temporales del codigo existente.

### 7.6 Matriz accionable de brechas (base inicial)

Usar esta matriz para convertir requisitos en plan de implementacion.

| Requisito                                                     | Cumple hoy | Gap identificado                                               | Cambio requerido                                            | Esfuerzo estimado |
| ------------------------------------------------------------- | ---------- | -------------------------------------------------------------- | ----------------------------------------------------------- | ----------------- |
| Monitoreo multicanal continuo (prensa/radio/tv/digital/redes) | Parcial    | Cobertura desigual por fuente y cliente                        | Ingesta global unificada + politicas de consumo por tenant  | Alto              |
| Alertas inmediatas con SLA contractual                        | Parcial    | Ambiguedad de SLA y reglas heterogeneas                        | Motor unico de alertas + politica SLA versionada            | Medio-Alto        |
| Portal web con filtros avanzados y exportacion                | Parcial    | Filtros/exportes no homogeneos entre flujos                    | Capa unica de consulta y exportacion                        | Medio             |
| Registro historico y entrega contractual de evidencias        | Parcial    | Politica de retencion y empaquetado final no estandarizado     | Pipeline de archivado, inventario y entrega verificable     | Alto              |
| Indicadores base (volumen, favorabilidad, tendencias)         | Parcial    | Metodologia no unificada                                       | Motor de metricas con formulas versionadas                  | Medio             |
| Free press (metodologia y calculo)                            | Parcial    | Criterios/fuentes no normalizados                              | Modulo parametrizable por cliente con trazabilidad          | Medio-Alto        |
| Semaforo reputacional e indicadores avanzados                 | No         | Logica avanzada no productizada como modulo transversal        | Modulo analitico avanzado (add-on)                          | Alto              |
| Deteccion de desinformacion                                   | No         | Sin flujo formal end-to-end                                    | Modulo especializado + reglas y validacion                  | Alto              |
| Multi-tenant nativo por cliente                               | Parcial    | Riesgo de mezclar configuracion de clientes en alertas/consumo | Modelo hibrido: ingesta global + reglas/permisos por tenant | Alto              |
| Seguridad, auditoria y trazabilidad de usuario                | Parcial    | Auditoria incompleta por flujo                                 | Estandar unico de auth, roles y auditoria                   | Medio             |

Escala sugerida para esfuerzo:

- Bajo: ajuste menor y de bajo riesgo.
- Medio: cambio moderado con impacto controlado.
- Alto: cambio estructural con impacto en arquitectura, datos o operacion.

Regla de uso:

1. No enviar propuesta final sin completar esta matriz para el cliente objetivo.
2. Cada fila debe mapearse a costo, plazo y riesgo en la propuesta.

## 8. Supuestos De Costeo (Plantilla)

Completar para cada propuesta:

1. Cobertura: local, regional o nacional.
2. Volumen: medios, palabras clave, ventanas de monitoreo.
3. Entregables: diarios, semanales, mensuales, especiales.
4. SLA: alertas, disponibilidad y tiempos de respuesta.
5. Retencion: operativa diaria, contractual, historico post-contrato.
6. Canales de distribucion de alertas y reportes.
7. Nivel analitico requerido (base o avanzado).

## 9. Aclaraciones Contractuales Minimas Antes De Cotizar

1. SLA exacto de alertas y criterio de medicion.
2. Alcance exacto de historico (menciones relacionadas vs grabacion total continua).
3. Universo de medios obligatorio y politica de cambios.
4. Formula oficial de free press y validacion.
5. Definicion de indicadores avanzados y umbrales de semaforo.
6. Requisitos de seguridad, auditoria y evidencia de cumplimiento.

## 10. Plantilla Rapida Para Nuevo Cliente

Usar este bloque para ingresar nuevo contexto.

### 10.1 Entrada minima

- Nombre del cliente:
- Objetivo del servicio:
- Cobertura geografica:
- Medios obligatorios:
- Palabras clave iniciales:
- SLA alertas:
- Disponibilidad esperada:
- Entregables:
- Retencion/historico:
- Presupuesto orientativo:
- Duracion contractual:

### 10.2 Salida esperada (propuesta)

1. Resumen ejecutivo.
2. Matriz de requisitos (CORE/CONFIGURABLE/ADD-ON).
3. Arquitectura recomendada.
4. Costeo por escenarios.
5. Riesgos y dependencias.
6. Plan de implementacion por fases.

## 11. Checklist De Calidad Antes De Entregar Propuesta

1. Todos los requisitos trazados a una capacidad concreta.
2. Supuestos de costo declarados y consistentes.
3. Ambiguedades contractuales identificadas y preguntadas.
4. Entregables y formatos alineados con el pliego.
5. Riesgos y exclusiones explicitados.
6. Hoja de ruta de escalamiento definida.

## 12. Regla De Uso De Este Documento

Cuando llegue un nuevo requerimiento, no partir de cero.

1. Completar la seccion 10.1.
2. Mapear a secciones 4, 5 y 6.
3. Ajustar arquitectura con seccion 7.
4. Ejecutar checklist de seccion 11.
5. Emitir propuesta final con estructura de seccion 10.2.
