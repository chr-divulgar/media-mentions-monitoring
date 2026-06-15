# Plan Fase 2 — Television Discovery via yt-dlp (YouTube Live)

**Fecha:** 2026-06-12
**Fase:** F2 — Media Core Worker
**Módulo afectado:** `apps/media-core-worker`

---

## 1. Objetivo

Habilitar la captura continua de señales de televisión que se transmiten como streams en vivo en YouTube (ej. `https://www.youtube.com/@noticiascaracol/live`), integrándose en el pipeline existente de captura de radio **sin modificar ninguna capa downstream** (FLAC windowing, OPUS segmentation, ProcessGuardian, Firebase).

La única diferencia frente a radio es **cómo se obtiene el `streamUrl`**: para fuentes de televisión con plataforma YouTube, el URL de la señal HLS es efímero y debe resolverse en tiempo de ejecución vía yt-dlp. Una vez resuelto, FFmpeg lo consume de forma idéntica a cualquier stream de radio.

Meta de esta feature:
- Soportar fuentes `media="television"` + `platform="youtube"` en `capture-sources.json`.
- Resolver el URL HLS real desde la página del canal de YouTube usando yt-dlp durante el startup.
- Refrescar automáticamente el URL cuando expira (TTL típico: 6–12 h), sin intervención manual.
- Manejar cookies de autenticación con alerta operacional cuando expiran.
- No impactar el rendimiento ni la estabilidad de las fuentes de radio existentes.

---

## 2. Requirement IDs impactados

| ID | Descripción |
|---|---|
| `RQ-001` | Captura continua de señales de medios colombianos |
| `RQ-004` | Soporte multi-plataforma (radio, televisión, digital) |
| `RQ-007` | Resiliencia y recuperación automática de fuentes |
| `A-003` | Integración con herramientas externas de extracción de audio/video |

---

## 3. Alcance

### Incluido
1. Nuevo media type `"television"` en `capture-sources.json` y `plugin-profiles.json`.
2. Puerto de salida `ILiveStreamUrlResolver` + resultado tipado `LiveStreamResolutionResult` en `Capture.Application`.
3. Adaptador `YtdlpLiveStreamUrlResolver` con `YtdlpBinaryProvider` (auto-descarga si no existe).
4. `YouTubeCookiesAlertService` — crea/lee/borra `stage/cookies/youtube-auth-required.flag`.
5. Pre-paso de resolución en `StartupSourceInitializationService` para fuentes TV.
6. Rama de re-resolución en `SourceAvailabilityReconciliationService` (hot-recovery + scheduled).
7. Manejo diferenciado: `Unavailable` → retry normal; `AuthRequired` → suspende hot-recovery + escribe flag.
8. Tests unitarios TDD (18 tests, mock de `IProcessRunner`, sin yt-dlp real).
9. Configuración de ejemplo con `noticias-caracol-live`.

### Excluido
1. Soporte para otras plataformas de TV (Twitch, Facebook Live, etc.) — extensión futura.
2. Descarga de video (solo audio, `bestaudio/best`).
3. UI / API endpoints para gestionar fuentes TV.
4. Soporte para streams de televisión con DRM.

---

## 4. Arquitectura objetivo

### 4.1 Flujo de startup (fuente TV)

```
capture-sources.json
  { media="television", platform="youtube",
    streamUrl="https://www.youtube.com/@noticiascaracol/live" }
          │
          ▼
StartupSourceInitializationService.InitializeAsync()
  [pre-paso TV] ILiveStreamUrlResolver.CanResolve(source) == true
          │
          ▼
YtdlpBinaryProvider.GetCommandAsync()     ← resuelve/descarga yt-dlp
YtdlpLiveStreamUrlResolver
  [opcional] --cookies stage/cookies/youtube.txt
  yt-dlp --get-url --format bestaudio/best --no-playlist --quiet <channelUrl>
          │
          ├── LiveStreamResolutionResult.Succeeded == true
          │       → source.WithStreamUrl(hlsUrl)
          │       → PersistStreamUrlAsync()
          │       → FfmpegStartupStreamValidator.ValidateAsync(hlsUrl)
          │       → ContinuousCaptureUseCase.ExecuteAsync() → CaptureSession auto-sostenida
          │
          └── LiveStreamResolutionResult.Failure == AuthRequired
                  → YouTubeCookiesAlertService.WriteAlert()
                  → PersistExclusionAsync(excluded=true)
                  → operador renueva cookies + borra flag → reconciliación retoma
```

### 4.2 Recuperación automática (event-driven, sin heartbeat)

```
CaptureSession detecta fallo (HLS URL expiró)
    → ICaptureAttemptObserver.ReportAsync()
    → SourceAvailabilityReconciliationService.TryHotRecoverUntilRotationAsync()
    → TryRecoverSourceAsync() — rama TV:
        ├── cookiesAlertService.AlertExists()? → return null (esperar operador)
        ├── liveStreamUrlResolver.TryResolveStreamUrlAsync()
        │       Succeeded → ClearAlert() + return recovered source
        │       AuthRequired → WriteAlert() + return null (suspende hot-recovery)
        └── Unavailable → return null, hot-recovery reintenta en :MM:00 siguiente

Scheduled reconciliation (1-min tick, ejecuta en :00/:01/:30/:59):
    Para fuentes TV excluidas con flag ausente → mismo flujo TryRecoverSourceAsync
```

### 4.3 Fit hexagonal

| Capa | Artefacto | Tipo |
|---|---|---|
| `Capture.Application` | `ILiveStreamUrlResolver` | Nuevo outbound port |
| `Capture.Application` | `LiveStreamResolutionResult` / `LiveStreamResolutionFailure` | Nuevos tipos de dominio |
| `Operations.Worker` | `YtdlpBinaryProvider` | Nuevo — resolución/descarga binario |
| `Operations.Worker` | `YtdlpLiveStreamUrlResolver` | Nuevo — implementa port |
| `Operations.Worker` | `YouTubeCookiesAlertService` | Nuevo — manejo de flag de auth |
| `Operations.Worker` | `StartupSourceInitializationService` | Modificado — pre-paso TV |
| `Operations.Worker` | `SourceAvailabilityReconciliationService` | Modificado — rama TV |
| `Operations.Worker` | `Program.cs` | Modificado — DI + pre-warmup |
| `stage/` | `worker-options.json` | Modificado — allow-list + yt-dlp settings |
| `stage/` | `plugin-profiles.json` | Modificado — perfil `television-youtube` |
| `stage/` | `capture-sources.json` | Modificado — fuente `noticias-caracol-live` |

---

## 5. Cookies de YouTube (autenticación)

### Diseño — archivo global en `stage/cookies/`

```
stage/
  cookies/
    youtube.txt                    ← Netscape format, global para todos los canales TV
    youtube-auth-required.flag     ← se crea cuando las cookies fallan (gitignored)
```

Configuración en `worker-options.json`:
```json
{
  "youtubeCookiesFilePath": "stage/cookies/youtube.txt",
  "youtubeCookiesAlertFilePath": "stage/cookies/youtube-auth-required.flag"
}
```

### Comportamiento por tipo de fallo

| Fallo | Hot-recovery | Scheduled reconciliation |
|---|---|---|
| `Unavailable` (stream offline, red) | Reintenta minuto a minuto hasta `:59` | Reintenta en `:00/:01/:30/:59` |
| `AuthRequired` (cookies expiradas/ausentes) | Suspendida mientras flag existe | Reintenta cuando operador borra el flag |
| `BinaryNotFound` | No aplica (fallo fatal en startup) | No aplica |

### Flujo manual de renovación de cookies

1. Instalar la extensión de Chrome **"Get cookies.txt LOCALLY"**.
2. Iniciar sesión en YouTube con la cuenta de monitoreo.
3. Navegar a `https://www.youtube.com` y exportar cookies en formato **Netscape**.
4. Reemplazar `stage/cookies/youtube.txt` con el archivo exportado.
5. **Borrar `stage/cookies/youtube-auth-required.flag`** — señal al worker de que las cookies fueron renovadas.
6. La próxima reconciliación (`:00`, `:01`, `:30`, o `:59`) retoma la captura automáticamente.

> Las cookies de YouTube expiran típicamente en 1–2 años. Renovar cuando aparezca el flag.

---

## 6. Archivos creados / modificados

### Nuevos
| Archivo | Propósito |
|---|---|
| `src/Modules/Capture/Capture.Application/ILiveStreamUrlResolver.cs` | Puerto de salida |
| `src/Modules/Capture/Capture.Application/LiveStreamResolutionResult.cs` | Resultado tipado + enum de fallos |
| `src/Workers/Operations.Worker/YtdlpBinaryProvider.cs` | Resolución/descarga del binario yt-dlp |
| `src/Workers/Operations.Worker/YtdlpLiveStreamUrlResolver.cs` | Adapter — implementa el port |
| `src/Workers/Operations.Worker/YouTubeCookiesAlertService.cs` | Escribe/lee/borra flag de auth |
| `tests/Unit/MediaOpsCore.UnitTests/YtdlpLiveStreamUrlResolverTests.cs` | 16 tests unitarios |
| `tests/Unit/MediaOpsCore.UnitTests/YouTubeCookiesAlertServiceTests.cs` | 7 tests unitarios |
| `apps/media-core-worker/.gitignore` | Ignora `stage/cookies/`, `bin/`, `obj/` |

### Modificados
| Archivo | Cambio |
|---|---|
| `src/Workers/Operations.Worker/OperationsWorkerOptions.cs` | 4 campos nuevos: `YtdlpBinDirectory`, `YtdlpResolutionTimeoutSeconds`, `YoutubeCookiesFilePath`, `YoutubeCookiesAlertFilePath` |
| `src/Workers/Operations.Worker/OperationsWorkerOptionsLoader.cs` | Deserialización de los 4 campos nuevos |
| `src/Workers/Operations.Worker/StartupSourceInitializationService.cs` | Pre-paso de resolución TV antes del loop FFmpeg |
| `src/Workers/Operations.Worker/SourceAvailabilityReconciliationService.cs` | Rama TV en `TryRecoverSourceAsync` con lógica de cookie alert |
| `src/Workers/Operations.Worker/Program.cs` | Registro DI + pre-warmup de `YtdlpBinaryProvider` |
| `stage/worker-options.json` | `continuousMediaAllowList: "radio,television"` + sección yt-dlp |
| `stage/plugin-profiles.json` | Perfil `television-youtube` |
| `stage/capture-sources.json` | Fuente de ejemplo `noticias-caracol-live` |

---

## 7. Lo que NO cambia

| Componente | Razón |
|---|---|
| `CaptureSource` (domain) | `Media` y `Platform` son strings abiertos — sin enum nuevo |
| `MediaPlatformIngestionPluginResolver` | Ya soporta tuplas arbitrarias `(media, platform)` |
| `InProcessFfmpegAudioCapturePlugin` | URLs HLS son entrada válida de FFmpeg |
| `FfmpegStartupStreamValidator` | Ya abre HLS vía `avformat_open_input` |
| Todas las fuentes de radio | Enteramente sin impacto |
| Pipeline downstream | FLAC → OPUS → segmentación → Firebase sin cambio |

---

## 8. Criterios de aceptación

- [ ] `CanResolve("television", "youtube")` retorna `true`; case-insensitive.
- [ ] Worker arranca con `noticias-caracol-live`: log muestra URL HLS resuelta + validación FFmpeg OK.
- [ ] Fuentes radio existentes no son afectadas.
- [ ] Si `yt-dlp` no está en PATH, se descarga automáticamente en `./bin/`.
- [ ] HLS URL expira → hot-recovery re-resuelve y reanuda captura.
- [ ] Cookies expiradas → `AuthRequired` → flag creado + hot-recovery suspendida.
- [ ] Operador borra el flag → reconciliación retoma captura automáticamente.
- [ ] Tests arquitectura siguen pasando — sin violaciones de capas.

---

## 9. KPIs de control

| KPI | Meta |
|---|---|
| `tv_stream_url_resolution_success_rate` | ≥ 95% por hora |
| `tv_stream_url_resolution_latency_ms` | < 15 000 ms |
| `tv_hot_recovery_count` | < 3 por hora por fuente (Unavailable) |
| `tv_auth_alert_active` | 0 en operación normal |

---

## 10. Rollout y rollback

### Rollout
1. Deploy con `noticias-caracol-live` en `excluded: null`.
2. Activar en canary: agregar al `canaryPlatformAllowList`.
3. Verificar logs durante 2h: resolución URL, captura FLAC, segmentación OPUS.
4. Si KPIs estables: `continuousMediaAllowList: "radio,television"` ya activo.

### Rollback
1. Remover `"television"` de `continuousMediaAllowList` → worker ignora todas las fuentes TV.
2. Las fuentes radio no se ven afectadas en ningún escenario.

---

## 11. Verificación end-to-end

```bash
cd media-mentions-monitoring/apps/media-core-worker

dotnet test tests/Unit/
dotnet test tests/Architecture/
dotnet run --project src/Workers/Operations.Worker

# Logs esperados:
# [YtdlpBinaryProvider] yt-dlp found at ... (o descargado)
# Resolving YouTube live stream URL for TV source noticias-caracol-live...
# [YtdlpResolver] Resolved stream URL for noticias-caracol-live: https://manifest.googlevideo.com/...
# Startup source validation/discovery finished. Valid=1 [noticias-caracol-live]

# Test cookie failure:
# Borrar/corromper stage/cookies/youtube.txt (si configurado)
# → [ERROR] TV source noticias-caracol-live excluded — YouTube authentication required.
# → stage/cookies/youtube-auth-required.flag creado

# Test cookie renewal:
# Reemplazar youtube.txt + borrar el flag
# → Scheduled reconciliation at :00 Recovered=1 [noticias-caracol-live]
```

---

## 12. Evolución futura

- **Renovación automática de cookies** — script externo que actualiza `stage/cookies/youtube.txt` y borra el flag.
- **Otras plataformas TV** (Twitch, Facebook Live) — nuevos `CanResolve` predicados + adapters.
- **Métricas Prometheus** para resolución y latencia.
- **Actualización automática de yt-dlp** en reconciliación (diaria).

Referencia: `docs/requirements-tracking/phase-F2/plan-workers-plugins-base.md`
