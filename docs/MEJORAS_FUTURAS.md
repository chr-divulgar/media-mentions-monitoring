# Mejoras Futuras

Este documento recoge ideas y planes de mejora identificados durante el desarrollo,
que no se implementan aún pero tienen valor claro para el sistema.

---

## [MEJORA-01] Integración con radio-browser.info para descubrimiento de emisoras

**Origen:** Análisis del repositorio `8beat` (cliente de radio de escritorio)  
**Prioridad:** Baja — mejora de UX, no bloquea funcionalidad actual  
**Esfuerzo estimado:** Medio (2-3 días)

### Problema actual

Las emisoras monitoreadas se agregan manualmente a la base de datos MongoDB
(colección `emisoras` en `config`). Si el cliente quiere ampliar la cobertura
a nuevas emisoras, alguien debe buscar la URL del stream manualmente y
registrarla a mano.

### Solución propuesta

Integrar la **API pública de radio-browser.info** en el panel web (`web-ui`)
para permitir buscar y agregar nuevas emisoras directamente desde la interfaz.

**API:** `https://de1.api.radio-browser.info` (gratuita, sin clave)

Endpoints útiles:
```
GET /json/stations/search?name=RCN&limit=10
GET /json/stations/byid/{stationId}
GET /json/stations/bycountry/colombia
GET /json/stations/topvote/20
```

Respuesta incluye: `name`, `url` (stream directo), `country`, `codec`, `favicon`, `tags`, `votes`

### Implementación sugerida

**Backend (`web-api` NestJS):**
- Nuevo endpoint `GET /stations/search?q=nombre` que actúa como proxy a radio-browser.info
- Nuevo endpoint `POST /stations/import` que recibe los datos de la emisora y la guarda en MongoDB config

**Frontend (`web-ui` React):**
- Nueva sección en el panel: "Agregar emisora"
- Buscador que llama al endpoint de búsqueda y muestra resultados con favicon y metadata
- Botón "Agregar al monitoreo" que importa la emisora seleccionada

### Beneficio

- El cliente puede autogestionar la lista de emisoras sin intervención técnica
- Acceso a ~40,000 emisoras mundiales con URLs ya validadas
- Evita el problema de URLs de streams rotas (radio-browser.info las valida periódicamente)

### Referencia

Ver repo `8beat/` — específicamente `8beat/8beat/helpers/StationRequester.py`
para ver cómo se consumen los endpoints.

---

*Agregar nuevas mejoras en este documento a medida que se identifiquen.*
