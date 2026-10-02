# Plan Maestro — Plataforma de Monitoreo de Medios

> Fuente única de requisitos, decisiones sobre ambigüedades, fases y % de avance.
> Corte inicial: **2026-10-01**. Se actualiza al cerrar cada entregable (ver §9).

## 1. Propósito

Construir una **plataforma propia** de monitoreo de medios que cumpla, o se acerque lo más posible, a lo que exigen las entidades públicas en sus convocatorias. El objetivo es tenerla desarrollada y demostrable **antes de postularnos**.

- No hay nada contratado. Los pliegos se usan como especificación de producto.
- Pliegos de referencia:
  - `docs/3. ANEXO_TECNICO_MONITOREO_FINAL_25-05-2026 (2) Posperidad social.md`: requisitos `ANX-01..116`.
  - `docs/Ficha Tecnica Monitoreo de Medios.md` (IDRD): requisitos `FIC-01..63`.
- Regla de construcción: ante cualquier ambigüedad se implementa **la lectura más exigente**, dejando el valor **configurable por cliente**. Así el mismo producto sirve para cualquier convocatoria (§5).
- Fuera de alcance de este plan: la historia interna del desarrollo (versiones anteriores, migraciones). Para la entidad el sistema es uno solo.

Los identificadores `ANX-xx` y `FIC-xx` vienen del catálogo extraído de cada pliego. Cada uno remite a las líneas del documento fuente.

## 2. Arquitectura del producto

```
                 ┌─────────────────────── Portal ────────────────────────┐
 Usuarios ──────▶│ apps/web-ui (React)  ◀──▶  apps/web-api (NestJS)      │──▶ Correo / WhatsApp
                 └───────────────▲───────────────────────────▲───────────┘
                                 │ HTTP / socket             │
                 ┌───────────────┴──── Media Core Worker (.NET 10) ──────┐
 Fuentes ───────▶│ Captura (radio, TV, web, prensa, redes) → Transcripción│
 (streams, web,  │ → Menciones/Clasificación → Alertas → Resúmenes horarios│
  redes, PDF)    └──────┬──────────────────┬──────────────────┬──────────┘
                        ▼                  ▼                  ▼
                Firestore (catálogo,   MongoDB (alertas,   Almacén de evidencias
                clientes, config)      menciones, reportes) (MP3/MP4/PDF, histórico)
```

- **Portal** (`apps/web-api`, `apps/web-ui`): consulta, filtros, dashboards, configuración, reportes y exportes.
- **Media Core Worker** (`apps/media-core-worker`): ingesta 24/7, procesamiento y alertas. Arquitectura hexagonal (`docs/CODING_STANDARDS_DOTNET10.md`).
- La ingesta es global y compartida. Las reglas, las alertas, las vistas y los datos entregables se separan por cliente (tenant).

## 3. Requisitos habilitantes (tumban la oferta si fallan)

| Requisito | Fuente | Estado hoy | Acción |
|---|---|---|---|
| Plataforma propia o licenciada, **sin herramientas gratuitas** ni buscadores públicos | FIC-01, FIC-03, ANX-95, ANX-97 | **Incumple en dos puntos.** La transcripción usa el endpoint gratuito no oficial de Google. WhatsApp sale por Baileys/mudslide, que no es oficial. | F1: motor STT licenciado. F5: WhatsApp Business Cloud API. |
| Certificado de propiedad o licencia del software | FIC-02, ANX-98 | No existe | F10: registro del software ante la Dirección Nacional de Derecho de Autor y ficha técnica |
| Demo funcional en vivo: captura digital y de redes, búsqueda, registro, dashboards, configuración de alertas, exporte Excel/CSV, acceso de administrador | ANX-113, ANX-114, ANX-115 | Parcial: radio, alertas y portal. Faltan digital, redes y exportes. | Hito **M1** (§6) |
| Acceso temporal de validación para la entidad | ANX-98, ANX-114 | No existe | F6 (tenant demo) + F10 |

## 4. Matriz consolidada de requisitos

Estado: **N** no iniciado · **P** en progreso · **C** cumplido (solo con evidencia verificable).

### 4.1 Habilitantes y cumplimiento legal

| ID | Requisito (lectura más exigente) | Fuentes | Estado | % | Evidencia hoy | Fase |
|---|---|---|---|---|---|---|
| RQ-001 | Plataforma propia; ningún componente gratuito, público o de terceros sin licencia en el flujo productivo | ANX-95, ANX-96, ANX-97, FIC-01, FIC-03 | P | 30 | Código propio en el repo. Incumplen la transcripción (Google gratuito) y WhatsApp (Baileys/mudslide). | F1, F5 |
| RQ-002 | Certificado de propiedad o licencia y documentación técnica (diagramas, ficha) | ANX-98, FIC-02, ANX-92 | N | 0 | — | F10 |
| RQ-003 | Demo funcional, acceso temporal y vista de administrador | ANX-113, ANX-114, ANX-115 | P | 15 | Portal en uso con radio | F10 |
| RQ-004 | Protección de datos (Ley 1581/2012), confidencialidad, reserva; los datos son solo de la entidad | FIC-04, FIC-05, FIC-06, FIC-07, ANX-116 | N | 5 | — | F6, F10 |

### 4.2 Cobertura y captura

| ID | Requisito | Fuentes | Estado | % | Evidencia hoy | Fase |
|---|---|---|---|---|---|---|
| RQ-010 | Radio 24/7 nacional, regional y local; audio de cada fragmento en MP3 | ANX-10, ANX-11, FIC-54, FIC-61 | P | 75 | `InProcessFfmpegAudioCapturePlugin.cs`, ~67 fuentes activas, recuperación automática | F1 |
| RQ-011 | TV: noticieros, debates y franjas; **video MP4**; los 29 canales del pliego | ANX-12, FIC-53 | P | 25 | YouTube Live solo audio (`YtdlpLiveStreamUrlResolver.cs`), 4 canales | F1 |
| RQ-012 | Prensa impresa y revistas (impresa + digital); escaneo completo de la página en PDF; medida en cm/columnas | ANX-08, ANX-09, FIC-14, FIC-55, FIC-56 | N | 0 | — | F3 |
| RQ-013 | Medios digitales nativos y blogs; crawler continuo con muchas fuentes en paralelo | ANX-13, ANX-14, ANX-31, ANX-32, FIC-15, FIC-57 | N | 0 | `DiscreteIngestionWorker` sin plugins | F3 |
| RQ-014 | Copia permanente de cada nota web, aunque el medio la retire | FIC-21 | N | 0 | — | F3 |
| RQ-015 | Redes: X, Facebook, Instagram, YouTube, TikTok, Threads; distinguir medios, periodistas, líderes y público; hashtags y virales; volumen, alcance, interacción y tono | ANX-15, ANX-16, ANX-17, FIC-58 | N | 0 | — | F4 |
| RQ-016 | Plataformas audiovisuales: canales de noticias en YouTube, transmisiones en vivo y debates digitales | ANX-18 | P | 20 | YouTube Live (audio) | F1, F4 |
| RQ-017 | Universo de medios ampliable sin costo; ≥30 departamentos; base de medios actualizada; clasificación TIER1/TIER2; medios comunitarios | ANX-19, ANX-20, ANX-21, ANX-22, FIC-16, FIC-31, FIC-52, FIC-59 | P | 20 | Catálogo en Firestore con zona y ciudad (Plataformas UI) | F1, F3 |
| RQ-018 | Monitorear entidad, programas, voceros, entidades adscritas y sector; menciones directas e indirectas | ANX-01, ANX-05, ANX-42 | P | 30 | Palabras clave por cliente; origen directo/indirecto manual en notas | F2 |
| RQ-019 | Captura automática + **revisión editorial** (ortografía y calidad) antes de publicar | ANX-04, FIC-27 | P | 20 | Revisión manual en el modal de alertas | F2 |
| RQ-020 | Transcripción radio/TV con motor licenciado, español de Colombia | ANX-11, ANX-42, FIC-01 | P | 50 | `ChunkTranscriptionPipeline` (motor gratuito, por reemplazar) | F1 |

### 4.3 Base de datos de menciones y clasificación

| ID | Requisito | Fuentes | Estado | % | Evidencia hoy | Fase |
|---|---|---|---|---|---|---|
| RQ-021 | Registro único por mención con los 13 campos: medio, tipo (8 valores), fecha y hora, titular, resumen, autor, vocero, tema, favorabilidad, región, enlace o archivo, alcance, free press. Campos por tipo de medio | ANX-48, ANX-49, ANX-104, FIC-13, FIC-14, FIC-15 | P | 25 | Notas con muchos campos, captura manual (`NoteEditModal.tsx`) | F2 |
| RQ-022 | Clasificación automática: tipo, tema, programa, vocero, región, dependencia y favorabilidad, con método validable | ANX-06, ANX-33, ANX-43 | P | 10 | Todo manual | F2 |
| RQ-023 | Resumen y titular automáticos por registro | ANX-49 | P | 50 | OpenAI bajo demanda (`alerts.service.ts#getChatResponse`) | F2 |
| RQ-024 | Búsqueda por palabras e índice; consulta en tiempo real o casi real; grandes volúmenes | ANX-02, ANX-26, ANX-34, ANX-46, ANX-47 | P | 15 | Transcripciones solo en JSON en disco; búsqueda en el navegador | F2, F6 |
| RQ-025 | Alcance estimado por registro (audiencia, tráfico, seguidores, vistas) | ANX-49 | P | 15 | Audiencia por franja (`SlotDto.audience`) | F2 |
| RQ-026 | Free press por registro (6 variables), por publicación, medio y periodo; según origen e impacto; fórmula versionada | ANX-51, ANX-52, ANX-53, FIC-43 | P | 20 | Tarifa ×30 s calculada en el navegador | F2 |
| RQ-027 | Matriz de valoración mediática por registro, por periodo, tipo y tema; indicador de impacto institucional | ANX-74, ANX-75, ANX-76, ANX-77, ANX-78 | N | 0 | — | F8 |

### 4.4 Alertas

| ID | Requisito | Fuentes | Estado | % | Evidencia hoy | Fase |
|---|---|---|---|---|---|---|
| RQ-030 | Alertas automáticas inmediatas por palabra clave, vocero, tema, riesgo reputacional y evento; frecuencia ajustable | ANX-38, ANX-79, ANX-83, FIC-23, FIC-30 | P | 45 | `DetectAlertsUseCase.cs` (palabras, anuncios, duplicados) | F5 |
| RQ-031 | SLA **≤ 5 min** desde la captura, medido y auditado por alerta | ANX-80, ANX-88 | N | 0 | Sin medición | F5 |
| RQ-032 | Canales: correo + WhatsApp oficial; destinatarios ilimitados; canal de crisis | FIC-24, ANX-71 | P | 40 | WhatsApp no oficial, sin correo | F5 |
| RQ-033 | Contenido: titular, audio/video, fecha, medio, bloque, duración, valor estimado, enlace; en riesgo, además alcance, tono, evolución y actores | FIC-25, FIC-26, ANX-82 | P | 30 | Texto con contexto de la palabra clave | F5 |
| RQ-034 | Niveles N1 informativa, N2 seguimiento, N3 riesgo, N4 crisis, enlazados al semáforo | ANX-81, ANX-71 | N | 0 | — | F5, F8 |
| RQ-035 | Alerta diaria de titulares de prensa y "top mediático del día" | FIC-28, FIC-29 | N | 0 | — | F5 |
| RQ-036 | Histórico de alertas: fecha y hora, tema, medio, nivel, alcance y clasificación reputacional | ANX-72, ANX-107 | P | 50 | Mongo `workerAlert` con seguimiento por destinatario | F5 |

### 4.5 Portal, seguridad y exportes

| ID | Requisito | Fuentes | Estado | % | Evidencia hoy | Fase |
|---|---|---|---|---|---|---|
| RQ-040 | Portal web 24/7, responsive, usuarios simultáneos ilimitados | ANX-23, ANX-24, ANX-25, FIC-08, FIC-11 | P | 50 | `apps/web-ui` | F6 |
| RQ-041 | Login, roles por perfil, **auditoría de actividad**, consola de administración de usuarios; credenciales en ≤2 días hábiles | ANX-93, ANX-94, FIC-09, FIC-10 | P | 25 | Firebase Auth; roles solo en la UI; API sin guards | F6 |
| RQ-042 | Filtros en servidor: palabra, medio, tipo, fecha, región, programa, vocero, tema, favorabilidad; paginado | ANX-27, ANX-47, FIC-17 | P | 25 | Fecha, cliente y tipo | F6 |
| RQ-043 | Vista de registro con reproductor (audio, video, PDF); titulares del día por medio | FIC-18, FIC-20 | P | 30 | Modal de alertas con forma de onda | F6 |
| RQ-044 | Dashboards automáticos: volumen por periodo, tipo, favorabilidad, tendencias, vocero, región (mapa de calor); contador de menciones | ANX-28, ANX-36, ANX-37, FIC-19 | P | 35 | Dashboard sobre notas manuales; exporte PPTX | F6, F8 |
| RQ-045 | Exportación Excel **y** CSV de registros completos y filtrados, para integración | ANX-29, ANX-39, ANX-48 | N | 0 | Solo importación Excel | F6 |
| RQ-046 | Descarga de soportes PDF, MP3 y MP4; compresión sin pérdida visible | ANX-30, FIC-17, FIC-18, FIC-22 | P | 20 | Descarga de clip MP3 | F6 |

### 4.6 Reportes

| ID | Requisito | Fuentes | Estado | % | Evidencia hoy | Fase |
|---|---|---|---|---|---|---|
| RQ-050 | Programador de reportes configurable por cliente (horas, ventanas, destinatarios, canal) | FIC-35, FIC-38 | N | 0 | — | F7 |
| RQ-051 | Reporte diario de medios a las **6:00** (17:00–5:59) y **17:00** (6:00–16:59), todos los días; medio, titular, fecha, soporte, medida y valor | FIC-32, FIC-33, FIC-34, FIC-36 | N | 0 | — | F7 |
| RQ-052 | 2 reportes diarios de redes con alcance, interacciones, influenciadores, favorabilidad, reproducciones, pico en vivo y enlace | FIC-37, FIC-38, FIC-39 | N | 0 | — | F7 |
| RQ-053 | Informe semanal con semáforo, indicadores, índice, matriz y free press | ANX-84, ANX-85 | N | 0 | — | F7 |
| RQ-054 | Informes mensuales de medios y de redes (≤5 días hábiles): presencia por palabra y canal, tono, cobertura, gráficos mensuales y acumulados, free press, conclusiones; redes con nube de términos, ranking de autores, hashtags, seguidores y bajas | FIC-40, FIC-41, FIC-42, FIC-44, FIC-45, FIC-46, FIC-47, ANX-84 | N | 10 | Exporte PPTX manual del dashboard | F7 |
| RQ-055 | Informes especiales y bajo demanda; PDF + formato editable | ANX-50, ANX-86, ANX-87 | N | 0 | — | F7 |
| RQ-056 | Base de datos actualizada cada día antes de las 9:00, con evidencia | ANX-90 | P | 30 | Ingesta continua, sin evidencia | F7 |

### 4.7 Analítica e inteligencia

| ID | Requisito | Fuentes | Estado | % | Evidencia hoy | Fase |
|---|---|---|---|---|---|---|
| RQ-060 | Indicadores de favorabilidad, volumen, alcance, participación regional (mapa de calor) y tendencias por tema; cada uno en número, gráfico y análisis; método disponible | ANX-03, ANX-07, ANX-54, ANX-55, ANX-56, ANX-57, ANX-58, ANX-60, ANX-61, ANX-62 | P | 15 | Agregados del dashboard | F8 |
| RQ-061 | Share of Voice frente a comparadores definidos por la entidad (que también se monitorean) | ANX-59 | N | 0 | — | F8 |
| RQ-062 | Índice de posicionamiento mediático 0-100 (volumen, favorabilidad, alcance, SoV), trazable y versionado | ANX-63, ANX-64 | N | 0 | — | F8 |
| RQ-063 | Semáforo Verde/Amarillo/Rojo/Rojo crítico con ≥8 variables configurables, al inicio del resumen ejecutivo, con actualizaciones extraordinarias | ANX-65, ANX-66, ANX-67, ANX-68, ANX-69, ANX-70 | N | 0 | — | F8 |
| RQ-064 | Desinformación: contenido falso o fuera de contexto, rumores virales, alerta temprana y validación | ANX-73 | N | 0 | — | F8 |
| RQ-065 | Narrativas dominantes, actores influyentes, influenciadores, nube de términos, ranking de autores | ANX-03, ANX-07, FIC-45 | N | 0 | — | F8 |
| RQ-066 | Palabras clave dinámicas por supervisor: ≥25, sin límite, 3 listas (permanente, temporada, deportistas o voceros), eventos y hashtags | FIC-48, FIC-49, FIC-50, FIC-51 | P | 60 | Palabras clave con contexto de anuncio por cliente (Settings) | F5 |

### 4.8 Histórico, continuidad y operación

| ID | Requisito | Fuentes | Estado | % | Evidencia hoy | Fase |
|---|---|---|---|---|---|---|
| RQ-070 | Histórico de todo el contrato en línea (archivo documental), soportes adjuntos por registro, trazabilidad | ANX-35, ANX-40, ANX-44, FIC-12 | P | 40 | Audio horario en `D:\` + JSON; sin retención | F9 |
| RQ-071 | Backups periódicos, recuperación ante incidentes y plan de contingencia | ANX-44, ANX-45, ANX-92 | N | 5 | — | F9 |
| RQ-072 | Disponibilidad ≥ 99% mensual medida; infraestructura escalable | ANX-41, ANX-89 | P | 20 | Servicio de Windows con autorrecuperación en un solo PC | F9 |
| RQ-073 | Entrega final del histórico completo en formatos abiertos: BD con 13 campos, PDF, MP3, MP4, estructura de carpetas, inventario, consistencia BD↔archivo, sin duplicados; cortes parciales | ANX-102, ANX-103, ANX-104, ANX-105, ANX-106, ANX-108, ANX-109, ANX-110, ANX-111, ANX-112 | N | 0 | — | F9 |
| RQ-074 | Respuesta a requerimientos ≤ 1 h (mesa de ayuda con registro) | ANX-91 | N | 0 | — | F10 |
| RQ-075 | Soporte al equipo operativo (coordinador, analistas, administrador): jornada, roles y experiencia | ANX-99, ANX-100, ANX-101 | — | — | Requisito operativo, no de software; el software lo apoya con roles y flujo editorial (RQ-019, RQ-041) | F10 |
| RQ-076 | Cronograma y metodología aprobables; material adicional; informe de actividades para el pago | FIC-47, FIC-60, FIC-62, FIC-63 | N | 0 | — | F10 |

## 5. Ambigüedades resueltas

La técnica habitual al postularse tiene dos partes:
1. En la etapa de **observaciones al pliego** se pide la aclaración.
2. En la **oferta** se declara el supuesto con la lectura más exigente, para no quedar por debajo.

El producto se construye para esa lectura, con el parámetro configurable.

| ID | Tema | Lecturas posibles | Decisión de construcción | Texto para la oferta / observación |
|---|---|---|---|---|
| AMB-001 | SLA de alertas | 5 min desde la publicación (ANX L2230) vs "10 y 15 minutos" (ANX L2258) vs "inmediata" | Objetivo **≤5 min desde la captura** del sistema, configurable por nivel (N1-N4). Se mide y audita cada alerta. | "Alertas en máximo 5 minutos desde que el contenido es capturado por la plataforma; se entrega reporte de cumplimiento del SLA." Observación: precisar desde qué evento se mide y si 10/15 min aplica a N1/N2. |
| AMB-002 | Tiempo real vs actualización diaria | "tiempo real" (L27) vs "cercano" (L2304) vs "antes de 9:00" (L2260) | Ingesta continua y menciones disponibles en minutos; la "actualización diaria" se cumple de sobra y se evidencia con un reporte | "Actualización continua; corte diario certificado antes de las 9:00." |
| AMB-003 | Semáforo vs alertas | El semáforo es por periodo pero dispara alertas en minutos | Semáforo calculado por periodo **y** en ventana móvil (24 h); un solo evento de alto riesgo puede subir el nivel y disparar N3/N4 | Declarar el modelo y el mapeo Verde→N1, Amarillo→N2, Rojo→N3, Rojo crítico→N4 |
| AMB-004 | Canales de alerta | No nombrados (ANX) vs correo + WhatsApp (FIC) | Correo + WhatsApp oficial; arquitectura de notificadores para agregar SMS o Teams | "Correo y WhatsApp Business; otros canales a solicitud sin costo." |
| AMB-005 | Campos del registro de alertas | Con alcance (L1919) vs con clasificación reputacional (L2853) | Guardar **ambos** | — |
| AMB-006 | Universo de medios | "Como mínimo" vs "todos los medios de"; 30 departamentos; local vs regional; sin lista | Catálogo ampliable sin costo; meta ≥30 departamentos + Bogotá; nacional, regional y local; tiers TIER1/TIER2 | "Universo base de N medios con cobertura en 30+ departamentos, ampliable sin costo." Observación: pedir la lista TIER1 oficial. |
| AMB-007 | Grupos radiales "con todas sus emisoras" | Solo Bogotá vs todo el país | Diseñar para todas las emisoras del grupo (capacidad escalable); activar por cliente | Declarar cobertura de las emisoras del grupo en las ciudades del alcance, ampliable |
| AMB-008 | Entidades adscritas y sector | Monitoreo propio vs solo comparadores | Cada adscrita como sub-entidad con sus propias palabras clave; utilizables como comparadores de SoV | — |
| AMB-009 | Multimedia: opcional u obligatoria | "Cuando la tecnología lo permita" vs MP3/MP4 obligatorios | **Siempre** MP3 (radio), MP4 (TV) y PDF (prensa y web) | — |
| AMB-010 | Formatos | "PDF, mp3, mp4 respectivamente" para 4 tipos; Excel **o** CSV; "abierto y editable" | Prensa/web→PDF, radio→MP3, TV→MP4, redes→captura + enlace; exportes en Excel **y** CSV **y** JSON; reportes en PDF + DOCX/XLSX | — |
| AMB-011 | Free press | 4 vs 6 variables; tarifas sin fuente; ¿neutra o negativa? | Fórmula **versionada y parametrizable** por cliente (6 variables), con tarifario cargable por medio y franja. Las notas negativas se reportan aparte, sin restar. | "Metodología de valoración presentada al inicio para validación de la supervisión." Observación: fuente de tarifas. |
| AMB-012 | Índice, matriz y pesos | 0-100 "por ejemplo"; pesos cualitativos | Motor de fórmulas con pesos configurables y versión guardada en cada cálculo | Igual que AMB-011 |
| AMB-013 | Favorabilidad: automática o editorial | Criterios del contratista validados + revisión editorial | Clasificación automática (LLM) + **cola de validación editorial**; queda registrado quién validó | "Clasificación asistida por IA con validación de analista." |
| AMB-014 | Filtros distintos según la sección | Programa, vocero/actor, dependencia, tipo, tema | Implementar la **unión** de todos los filtros | — |
| AMB-015 | Disponibilidad 99% y respuesta en 1 h | Sin método, ventanas ni horario | Medir uptime con monitoreo externo; reporte mensual; respuesta 24/7 con registro de tickets | Declarar método de medición y ventanas de mantenimiento notificadas |
| AMB-016 | Jornada de monitoreo | No definida; "permanente" | Captura 24/7; flujo editorial con turnos configurables | — |
| AMB-017 | Reportes: días, horas y contenido | Semanal y mensual sin día; diarios con 1-60 min para preparar | Programador configurable; reportes diarios generados **automáticamente** al cierre de la ventana; mensual el día 1 hábil (el pliego da 5) | — |
| AMB-018 | Usuarios del portal | 3 usuarios (FIC) vs "múltiples" (ANX); alertas sin restricción | Usuarios **ilimitados** con roles; destinatarios ilimitados | "Usuarios ilimitados." |
| AMB-019 | Redes cubiertas y "cuenta oficial" | 4 redes (FIC) vs 6 (ANX) vs "todas"; ¿escucha o cuentas propias? | Las 6 redes en escucha pública + métricas de cuentas propias (seguidores y bajas) con acceso que otorgue la entidad | "Escucha en X, Facebook, Instagram, YouTube, TikTok y Threads; métricas de cuentas institucionales con autorización." |
| AMB-020 | Alertas de impacto neutro; "alto impacto"; TIER1 sin lista | Ambiguo | Puntaje de impacto (matriz RQ-027) con umbral configurable; las neutras alertan si superan el umbral | — |
| AMB-021 | "Transcripción" de notas web | Texto literal vs copia permanente | Copia permanente: texto + PDF de la página + captura | — |
| AMB-022 | Titulares de diarios y top del día | Solo periódicos vs revistas; sin hora ni método | Periódicos + revistas, envío configurable (por defecto 7:00); top por puntaje de impacto | — |
| AMB-023 | Histórico: alcance y retención | Menciones vs grabación completa; post-contrato | Grabación completa de radio/TV durante el contrato + soportes por mención; ventana de consulta posterior configurable | "Entrega completa en formatos abiertos sin dependencia de la plataforma." |
| AMB-024 | Software "de carácter pago" | ¿Vale software propio? | Software propio registrado (FIC L276 admite "propiedad") | Adjuntar certificado de registro |
| AMB-025 | Errores de numeración y listas | Secciones inexistentes; medios duplicados (Futbolred, AS, HSB, Laud) o extranjeros (Radio Panamericana) | El catálogo deduplica por URL; se reporta en observaciones | Observación: corregir referencias y listado |

## 6. Fases de desarrollo

Capacidad: **1 desarrollador + Claude Code**. Días = días hábiles de trabajo restante. La fecha de fin incluye **15% de contingencia** y festivos de Colombia, contando desde 2026-10-01.

| # | Fase | % hoy | Trabajo restante | Días | Fin estimado |
|---|---|---|---|---|---|
| F1 | **Núcleo de captura radio/TV licenciable** | 80 | Motor STT licenciado (Google Cloud STT o Whisper local); captura de video **MP4** de TV; ampliar TV a los canales del pliego (HLS oficial además de YouTube); logs a archivo y alertas de disco y caídas; retención y purga; deduplicar el catálogo (una sola fuente de verdad en Firestore) | 30 | 2026-11-23 |
| F2 | **Modelo único de menciones + clasificación** | 15 | Registro canónico de 13 campos para todos los canales; transcripción → mención automática; resumen y titular automáticos; favorabilidad, tema, vocero, región y tier por LLM; **cola de validación editorial**; menciones directas e indirectas; índice full-text; motor de fórmulas (free press, valoración) versionado | 32 | 2027-01-19 |
| F3 | **Prensa y medios digitales** | 0 | Plugins discretos en `DiscreteIngestionWorker`: crawler RSS, sitemap y HTML; onboarding de los portales del pliego + regionales de 30 departamentos; copia permanente (texto + PDF + captura); ediciones impresas y revistas en PDF con OCR, recorte de página y medida | 31 | 2027-03-10 |
| F4 | **Redes sociales y audiovisual** | 0 | Conectores para X, Facebook, Instagram, YouTube, TikTok y Threads (APIs oficiales; proveedor de datos licenciado como adapter intercambiable); métricas de alcance, interacciones, reproducciones, pico en vivo, hashtags, influenciadores; seguidores y bajas de cuentas propias | 25 | 2027-04-23 |
| F5 | **Alertas multicanal con SLA** | 55 | Alertas del worker en producción (salir de modo sombra); **correo**; WhatsApp Business Cloud API; contenido completo con clip y enlace; niveles N1-N4; reglas por palabra, vocero, tema, riesgo y evento; medición del SLA; titulares diarios y top del día; listas de palabras clave; corregir el envío de notas a un número fijo | 18 | 2027-05-24 |
| F6 | **Portal: seguridad, búsqueda, dashboards, exportes** | 35 | Guards en todo el API, RBAC, **auditoría**, consola admin, quitar el fallback a admin; aislamiento por cliente; filtros completos en servidor y paginados; vista de registro con reproductor; dashboards automáticos y mapa de calor; exportes Excel/CSV/JSON; descargas MP3/MP4/PDF; tenant demo | 28 | **2027-07-13** |
| — | **Hito M1 — demo aprobable** (§3, ANX-113) | — | F1 a F6 completas | 164 | **2027-07-13** |
| F7 | **Reportes automáticos** | 5 | Programador; diarios 6:00 y 17:00 + 2 de redes; semanal, mensual, especial y bajo demanda; plantillas por cliente; PDF + editable; envío por correo; evidencia del corte diario de las 9:00 | 18 | 2027-08-11 |
| F8 | **Analítica avanzada** | 0 | Indicadores; SoV con comparadores; índice 0-100; semáforo de 4 niveles con 8 variables ligado a N1-N4; matriz de valoración e impacto; desinformación con validación; narrativas y actores influyentes | 25 | 2027-09-22 |
| F9 | **Histórico, entrega y continuidad** | 10 | Retención por cliente; backups y restauración probada; paquete de entrega en formatos abiertos con inventario y verificación de consistencia; cortes parciales; despliegue en servidor o nube escalable (sin depender de un PC Windows); monitoreo de uptime del 99% con reporte; plan de contingencia | 18 | 2027-10-22 |
| F10 | **Preparación para convocatorias** | 0 | Registro del software; ficha técnica y diagramas; política de datos personales; metodologías (favorabilidad, free press, índice, semáforo, matriz); guion de demo y acceso temporal; mesa de ayuda con registro; plantilla de cronograma e informe de actividades | 10 | **2027-11-09** |

**Totales**

| Concepto | Valor |
|---|---|
| Trabajo restante | **235 días hábiles**; ~270 con 15% de contingencia |
| Demo aprobable (M1) | **2027-07-13**, a **285 días** de hoy |
| Desarrollo completo | **2027-11-09**, a **404 días** de hoy |
| Avance global ponderado por esfuerzo | **≈ 41%**: la captura, ya avanzada, es la parte más costosa |
| Promedio del % de los requisitos | **≈ 17%**: muchos requisitos de reportes y analítica están en 0 |

El orden prioriza llegar a M1. Con M1 ya se puede presentar una oferta con demo funcional, aunque todavía falten reportes avanzados y analítica, declarando esos módulos en implementación.

## 7. Riesgos

| Riesgo | Impacto | Mitigación |
|---|---|---|
| STT gratuito no oficial (incumple FIC-01 y tiene límites de tasa) | Habilitante y SLA | F1: motor licenciado; costo por hora de audio en la oferta |
| WhatsApp no oficial (Baileys/mudslide) | Habilitante y bloqueo de la cuenta | F5: WhatsApp Business Cloud API |
| Un solo PC Windows y disco local (ya hubo un incidente de disco lleno) | Disponibilidad del 99% | F1: alertas de disco y retención; F9: servidor o nube |
| Costo y acceso a las APIs de redes (X y TikTok son restrictivas) | Cobertura de redes | Adapter intercambiable con un proveedor de datos licenciado |
| Acceso a ediciones impresas (suscripciones) | Prensa en PDF | Suscripciones digitales como costo operativo de la oferta |
| Carga editorial humana (revisión y validación) | Calidad y SLA | Clasificación asistida por IA + cola editorial priorizada |
| Volumen de 29 canales de TV + ~37 radios + crawler | Rendimiento | Captura escalable por worker y medición de recursos en F1 y F9 |

## 8. Fuera de alcance del software

Equipo humano (perfiles y hojas de vida), seguridad social, facturación y garantías. El plan solo asegura que la plataforma **soporte** esa operación: roles, flujo editorial, registro de actividad e informes de actividades.

## 9. Cómo se usa este plan

1. Cada cambio de código cita uno o más `RQ-xxx` (o `AMB-xxx`).
2. Al cerrar un entregable se actualizan aquí el estado, el %, la evidencia (rutas, tests, reportes) y, si cambia, la estimación.
3. Un `RQ-xxx` pasa a **C** solo con evidencia verificable.
4. Si aparece un requisito nuevo, por ejemplo de otra convocatoria, se agrega como `RQ-xxx`, se le asigna una fase y se recalculan los días.
