# Plan de implementación: captura de audio con FFmpeg.AutoGen

## Contexto

- 50+ streams simultáneos de URLs (RTSP / HLS / HTTP)
- Grabación continua en **FLAC segmentado por hora** (lossless, ~40 MB/hora/stream)
- Flush periódico cada 30s para tolerancia a fallos — pérdida máxima 30 segundos de audio
- Chunks de audio en **WAV en memoria** para Speech Recognition — corte por silencio entre 20–30s, nunca a disco
- Conversión a **MP3 bajo demanda** cuando el usuario lo solicita
- Todo dentro de un solo proceso .NET — sin procesos externos de FFmpeg

---

## 1. Estructura general del pipeline

```
URL Stream
    │
    ▼
[ Decoder — AVFormatContext + AVCodecContext ]
    │  frames PCM raw
    ▼
[ Resampler — SwrContext ]
    │  16 kHz · mono · s16le
    ▼ ─────────────────────────────────────────────────
    │                                                  │
    ▼                                                  ▼
[ FLAC Encoder — AVCodecContext ]          [ WAV Chunk Builder ]
    │  lossless continuo                       │  en memoria · MemoryStream
    ▼                                          ▼
[ FileStream → disco ]              [ Channel<byte[]> bounded ]
    │                                          │
    ▼ (bajo demanda)                           ▼
[ MP3 Encoder — AVCodecContext ]     [ Speech Recognition consumer ]
    │
    ▼
[ archivo .mp3 final ]
```

Cada stream es una instancia independiente de `AudioStreamCapture : IDisposable`.
El pipeline dual (FLAC + WAV) comparte el mismo SwrContext — se resamplea una sola vez
y el PCM resultante se bifurca: uno va al encoder FLAC y el otro al buffer WAV.

---

## 2. Parámetros por etapa

### 2.1 Decoder (AVFormatContext + AVDictionary)

Objetivo: arranque rápido, baja latencia, reconexión automática.

| Parámetro | Valor | Razón |
|---|---|---|
| `probesize` | `32768` (32 KB) | Por defecto FFmpeg analiza 5 MB antes de arrancar. 32 KB es suficiente para identificar el codec de audio. |
| `analyzeduration` | `0` | Elimina el tiempo de análisis inicial del stream. |
| `fflags` | `nobuffer` | Desactiva el buffer interno del demuxer — reduce latencia de inicio. |
| `rtsp_transport` | `tcp` | Para RTSP: evita pérdida de paquetes que ocurre con UDP en redes inestables. Solo aplica si el stream es RTSP. |
| `reconnect` | `1` | Reconexión automática si el stream cae. |
| `reconnect_streamed` | `1` | Permite reconectar streams que ya estaban activos (no solo al inicio). |
| `reconnect_delay_max` | `5` | Máximo de segundos entre intentos de reconexión. |
| `stimeout` | `5000000` | Timeout de socket en microsegundos (5 segundos). Evita que un stream colgado bloquee el hilo. |

Estos parámetros se pasan como `AVDictionary` antes de llamar a `avformat_open_input`.

### 2.2 Resampler (SwrContext)

Objetivo: convertir el audio decodificado al formato que esperan tanto el encoder FLAC como el Speech Recognition.

| Parámetro | Valor | Razón |
|---|---|---|
| `out_sample_rate` | `16000` Hz | Estándar universal para Speech Recognition (Azure, Whisper, Google). Suficiente para voz. |
| `in_sample_rate` | El del stream original | Leer de `AVCodecContext->sample_rate`. Puede ser 44100, 48000, 22050, etc. |
| `out_channels` | `1` (mono) | El SR no se beneficia de estéreo y duplica el volumen de datos. |
| `in_channel_layout` | El del stream original | Leer de `AVCodecContext->ch_layout`. |
| `out_sample_fmt` | `AV_SAMPLE_FMT_S16` | Enteros 16-bit little-endian. Formato nativo de WAV para SR y compatible con FLAC. |
| `in_sample_fmt` | El del codec decodificado | Leer de `AVCodecContext->sample_fmt`. Suele ser `fltp` (float planar). |
| `filter_type` | `SWR_FILTER_TYPE_KAISER` | Mejor calidad de resampleo que el lineal por defecto. Costo de CPU mínimo adicional. |
| `dither_method` | `SWR_DITHER_TRIANGULAR` | Reduce artefactos de cuantización al convertir de float a int16. |

El SwrContext se inicializa una sola vez por stream y se reutiliza para todos los frames.

### 2.3 FLAC Encoder (AVCodecContext)

Objetivo: grabación lossless continua con mínima CPU. El FLAC es el archivo de archivo — de aquí se genera el MP3 cuando el usuario lo pida.

| Parámetro | Valor | Razón |
|---|---|---|
| `codec_id` | `AV_CODEC_ID_FLAC` | Codec lossless. Sin pérdida de calidad respecto al PCM original. |
| `sample_rate` | `16000` Hz | Mismo que la salida del resampler. |
| `ch_layout` | mono (`AV_CH_LAYOUT_MONO`) | Mismo que la salida del resampler. |
| `sample_fmt` | `AV_SAMPLE_FMT_S16` | El encoder FLAC acepta s16 directamente desde el resampler sin conversión adicional. |
| `compression_level` | `0` | Nivel 0 = velocidad máxima, cero CPU de compresión. El ahorro de espacio vs WAV viene del formato, no del nivel. A nivel 0, FLAC ocupa ~60-65% menos que WAV equivalente. |
| `frame_size` | `4096` muestras | Tamaño de bloque FLAC. Óptimo para escritura secuencial a disco. El encoder lo ajusta automáticamente si se deja en 0. |

Tamaño esperado en disco: **~40 MB por hora de stream** a 16kHz mono.
Con 50 streams: **~48 GB por día total** — dimensionar disco en consecuencia.

### 2.4 Segmentación horaria de archivos FLAC

Un único archivo FLAC por día tiene dos problemas críticos: si el proceso cae sin
cerrar el `STREAMINFO`, el archivo completo queda corrupto. Y hacer seek dentro
de varios GB para recortar un fragmento es lento y consume más RAM.

La solución es **un archivo FLAC por hora por stream**, con rotación en caliente.

**Nomenclatura:**
```
/audio/{stream_id}/{YYYYMMDD}_{HH}.flac

Ejemplo:
/audio/stream_001/20260604_13.flac   ← en escritura activa
/audio/stream_001/20260604_12.flac   ← cerrado, listo para backup
```

**Rotación en caliente — sin interrumpir el stream:**

El decoder y el resampler continúan sin pausa. Solo cambia el destino de escritura
del encoder FLAC al llegar a la hora:

```
:59:59 → avcodec_flush_buffers()     ← vacía frames pendientes del encoder
       → avio_flush()                ← fuerza escritura del buffer al FileStream
       → cierra FileStream anterior  ← archivo cerrado limpiamente y válido
       → abre nuevo FileStream       ← {stream_id}_HH+1.flac
       → encoder FLAC continúa       ← mismo AVCodecContext, solo cambia el output
:00:00 → escritura continúa sin interrupción perceptible
```

El offset de rotación por stream (sección 4.1) aplica también aquí — cada stream
rota en su propio segundo para distribuir los flushes de hora en hora.

**Si el recorte MP3 cruza dos archivos** (por ejemplo el usuario pide 13:45–14:15),
se leen los dos FLAC correspondientes y se concatenan los frames antes de encodear
a MP3. FFmpeg maneja esto de forma nativa con múltiples inputs.

### 2.5 Flush periódico tolerante a fallos

Sin flush periódico, un fallo del proceso corrompe el archivo FLAC activo completo
porque el `STREAMINFO` (header con metadatos de cierre) nunca se escribe.
Con flush cada 30 segundos, la pérdida máxima en cualquier fallo es de 30 segundos.

**Secuencia de flush por stream cada 30 segundos:**

```
1. avio_flush()                      ← vacía buffer interno al FileStream del OS
2. seek al byte 0 del archivo        ← STREAMINFO siempre está al inicio del FLAC
3. reescribir primeros ~42 bytes     ← actualiza num_samples, MD5 y duración actual
4. seek de vuelta al final           ← continúa escritura normal
```

**Impacto en disco según frecuencia (con 50 streams):**

| Frecuencia | Seeks/segundo | Pérdida máx. en fallo | Recomendación |
|---|---|---|---|
| Cada 1s | 50/s | ~1s | Excesivo en HDD |
| Cada 10s | 5/s | ~10s | Válido en SSD |
| Cada 30s | 1.6/s | ~30s | Óptimo HDD y SSD |
| Cada 60s | 0.8/s | ~60s | Solo si disco es limitante |

**Desfase de flushes para evitar seeks simultáneos:**

Con 50 streams haciendo flush cada 30 segundos al mismo tiempo, se producen
50 seeks simultáneos cada 30 segundos — el mismo problema que con los cortes horarios.
La solución es el mismo patrón de offset:

```
stream[i] hace flush en: ventana_30s + (i × 0.6 segundos)
```

Con 50 streams y offset de 0.6s, los flushes se distribuyen uniformemente
en los 30 segundos — aproximadamente 1.6 seeks por segundo de forma continua
en lugar de 50 seeks en el mismo instante.

### 2.6 WAV Chunk Builder (en memoria)

Los chunks WAV **nunca se escriben a disco**. Se construyen en un `MemoryStream` y se envían al `Channel<byte[]>`.

**Parámetros de formato:**

| Parámetro | Valor | Razón |
|---|---|---|
| `sample_rate` | `16000` Hz | Requerido por todos los motores de SR. |
| `channels` | `1` (mono) | |
| `bits_per_sample` | `16` | Corresponde a `AV_SAMPLE_FMT_S16`. |
| `audio_format` | `1` (PCM) | WAV estándar sin compresión. Los motores de SR lo consumen directamente. |
| `header WAV` | 44 bytes fijos | Se construye en código con los parámetros conocidos. No requiere FFmpeg — es aritmética de bytes. |

**Corte por silencio (reemplaza el corte por tiempo fijo):**

En lugar de cortar cada N segundos fijos, el chunk se corta en el primer silencio
detectado dentro de la ventana de 20–30 segundos. Esto elimina el problema de
palabras cortadas en el límite del chunk sin necesidad de overlap.

| Parámetro | Valor | Razón |
|---|---|---|
| `silence_threshold` | `-40 dB` | Por debajo de este nivel se considera silencio. Válido para voz en radio con algo de ruido de fondo. Bajar a `-50 dB` en entornos más limpios. |
| `silence_duration` | `0.3 segundos` | Mínimo de tiempo continuo en silencio para considerarlo un punto de corte válido. Evita cortar en oclusivas o pausas de respiración muy cortas. |
| `chunk_min` | `20 segundos` | No se corta antes de 20s aunque haya silencio — chunks cortos pierden contexto de oración para el SR. |
| `chunk_max` | `30 segundos` | Si no hay silencio en 30s, se corta igual en ese punto para no acumular buffer indefinidamente. |

**Lógica de decisión de corte:**

```
acumular PCM en MemoryStream
    │
    ├── buffer < 20s  → continuar acumulando siempre
    │
    ├── buffer 20–30s → monitorear nivel de dB frame a frame
    │       │
    │       └── nivel < -40dB durante ≥ 0.3s → cortar aquí (punto limpio)
    │
    └── buffer = 30s  → cortar ahora aunque no haya silencio (forzado)
```

La detección de silencio se hace sobre el PCM ya resampleado (s16le 16kHz mono)
directamente en .NET — calcular el RMS de cada frame de 20ms y comparar contra
el umbral. No requiere el filtro `silencedetect` de FFmpeg porque el PCM ya
está disponible en memoria antes de enviarlo al MemoryStream.

El `byte[]` resultante incluye el header WAV de 44 bytes + los datos PCM del chunk.

### 2.7 MP3 Encoder (bajo demanda)

Se instancia solo cuando el usuario solicita el archivo. Lee el FLAC de disco y produce el MP3.

| Parámetro | Valor | Razón |
|---|---|---|
| `codec_id` | `AV_CODEC_ID_MP3` | Encoder LAME vía FFmpeg. |
| `bit_rate` | VBR con `q:a = 2` | VBR calidad 2 ≈ 190 kbps promedio. Indistinguible de calidades superiores para voz. |
| `sample_rate` | `16000` Hz | Mismo que el FLAC origen. No es necesario resamplear. |
| `ch_layout` | mono | |
| `compression_level` | `2` | Balance entre velocidad de encoding y calidad. Rango 0–9, donde 0 es más lento y mejor. |
| `id3v2_version` | `3` | Compatibilidad máxima con reproductores. |

---

## 3. Ciclo de vida y liberación de recursos

### 3.1 Orden de creación

El orden importa porque cada contexto puede depender del anterior:

```
1. avformat_alloc_context()          → AVFormatContext*
2. avformat_open_input()             → abre el stream
3. avformat_find_stream_info()       → detecta codec del stream
4. avcodec_find_decoder()            → encuentra el decoder
5. avcodec_alloc_context3()          → AVCodecContext* (decoder)
6. avcodec_parameters_to_context()   → copia parámetros del stream al codec
7. avcodec_open2()                   → abre el decoder
8. swr_alloc_set_opts()              → SwrContext*
9. swr_init()                        → inicializa el resampler
10. avcodec_find_encoder(FLAC)       → encoder FLAC
11. avcodec_alloc_context3()         → AVCodecContext* (encoder FLAC)
12. avcodec_open2()                  → abre el encoder FLAC
13. av_packet_alloc()                → AVPacket* (reutilizable en el loop)
14. av_frame_alloc()                 → AVFrame* (reutilizable en el loop)
```

### 3.2 Orden de liberación — CRÍTICO

**Siempre en orden inverso al de creación.** Si se libera un contexto que todavía
es referenciado por otro, el comportamiento es indefinido y produce leaks o crashes silenciosos.

```
1. av_frame_free()                   → liberar frame antes que el codec que lo produjo
2. av_packet_free()                  → liberar packet antes que el format context
3. avcodec_free_context() [encoder]  → encoder FLAC primero (ya no necesita el decoder)
4. swr_free()                        → resampler después del encoder (ya no produce frames)
5. avcodec_free_context() [decoder]  → decoder después del resampler
6. avformat_close_input()            → format context al final (contenedor de todo)
```

Si se instancia un encoder MP3 bajo demanda, debe liberarse con `avcodec_free_context()`
antes de que su AVFormatContext de salida sea cerrado con `avformat_free_context()`.

### 3.3 Liberación dentro del loop de lectura

Dentro del loop de `av_read_frame`, cada packet y frame debe liberarse
al terminar de procesarlo, **antes de volver al inicio del loop**:

```
av_read_frame() → procesar packet → av_packet_unref()   ← no av_packet_free, solo unref
avcodec_receive_frame() → procesar frame → av_frame_unref()  ← no av_frame_free, solo unref
```

La diferencia: `av_packet_free` destruye el objeto. `av_packet_unref` libera el buffer
interno pero mantiene el objeto para reutilizarlo en la siguiente iteración.
Asignar un nuevo `av_packet_alloc()` en cada iteración del loop es un leak garantizado.

### 3.4 Encapsulación en IDisposable

Toda la gestión de recursos se encapsula en una clase que implemente `IDisposable`.
El `Dispose()` ejecuta la secuencia de liberación del punto 3.2 dentro de un bloque
que garantice ejecución incluso si el stream termina con excepción.

El patrón recomendado es usar un `CancellationToken` para detener el loop de lectura
de forma limpia antes de que `Dispose()` intente liberar los contextos — si el loop
sigue corriendo mientras se liberan los contextos, el comportamiento es indefinido.

Secuencia de shutdown limpio por stream:

```
1. Cancelar el CancellationToken del stream
2. Esperar que el Task del loop termine (await con timeout de 3s)
3. Llamar Dispose() → ejecuta la secuencia de liberación
```

---

## 4. Gestión de 50+ streams simultáneos

### 4.1 Desfase de cortes (evitar picos de CPU)

Cuando todos los streams cortan al mismo tiempo, los flushes de encoder se acumulan
y producen picos. La solución es distribuir los cortes en una ventana de tiempo:

```
stream[i] corta en: hora_base + (i × 3 segundos)
```

Con 50 streams y offset de 3s, los cortes se distribuyen en 150 segundos.
El pico de CPU se convierte en carga sostenida y moderada.

### 4.2 Channel de WAV chunks

```
Tipo:        Channel<(string streamId, byte[] wav)>
Capacidad:   bounded — capacidad = número de streams × 3
FullMode:    DropOldest si la latencia de transcripción es aceptable
             Wait si no se puede perder ningún chunk
```

Un `Channel` central con múltiples consumers de SR es más eficiente que
un `Channel` por stream, porque balancea automáticamente la carga entre consumers.

Número recomendado de consumers de SR: entre 4 y 8, dependiendo de si el SR
es local (Whisper) o remoto (Azure / Google). Para SR remoto, más consumers
= más concurrencia de requests HTTP, que suele ser el cuello de botella.

### 4.3 Monitoreo de recursos

Para detectar leaks temprano sin instrumentación externa, exponer como métricas internas:

- `GC.GetTotalMemory(false)` cada 60 segundos → si crece linealmente hay leak nativo
- `channel.Reader.Count` → si crece sostenidamente, el SR no sigue el ritmo
- Contador de streams activos vs streams con contexto abierto → deben ser iguales

---

## 5. Resumen de parámetros críticos

| Etapa | Parámetro clave | Valor |
|---|---|---|
| Decoder | probesize | 32768 |
| Decoder | analyzeduration | 0 |
| Decoder | fflags | nobuffer |
| Decoder | reconnect_delay_max | 5 |
| Decoder | stimeout | 5000000 |
| Resampler | out_sample_rate | 16000 Hz |
| Resampler | out_channels | 1 (mono) |
| Resampler | out_sample_fmt | AV_SAMPLE_FMT_S16 |
| Resampler | filter_type | SWR_FILTER_TYPE_KAISER |
| FLAC encoder | compression_level | 0 |
| FLAC encoder | sample_rate | 16000 Hz |
| FLAC encoder | frame_size | 4096 muestras |
| Segmentación FLAC | duración por archivo | 1 hora |
| Segmentación FLAC | offset de rotación | stream[i] × 3s |
| Segmentación FLAC | nomenclatura | {stream_id}_{YYYYMMDD}_{HH}.flac |
| Flush tolerante a fallos | frecuencia (HDD) | cada 30s |
| Flush tolerante a fallos | frecuencia (SSD) | cada 10s |
| Flush tolerante a fallos | offset entre streams | stream[i] × 0.6s |
| Flush tolerante a fallos | pérdida máxima en fallo | 30s (HDD) / 10s (SSD) |
| WAV chunk | chunk_min | 20 segundos |
| WAV chunk | chunk_max | 30 segundos |
| WAV chunk | silence_threshold | -40 dB |
| WAV chunk | silence_duration | 0.3 segundos |
| WAV chunk | formato | PCM s16le · 16kHz · mono |
| MP3 (demanda) | calidad VBR | q:a = 2 |
| MP3 (demanda) | id3v2_version | 3 |

---

## 6. Flujo de rotación horaria y flush periódico

```
─── cada 30s (con offset por stream) ───────────────────────────────────
avio_flush()
    │
seek byte 0 → reescribir STREAMINFO (~42 bytes)
    │
seek final → continuar escritura normal
─────────────────────────────────────────────────────────────────────────

─── cada hora (con offset por stream) ───────────────────────────────────
avcodec_flush_buffers()          ← vacía frames pendientes del encoder
    │
avio_flush()                     ← fuerza escritura del buffer al OS
    │
cerrar FileStream anterior       ← archivo FLAC válido y completo
    │
abrir nuevo FileStream           ← {stream_id}_{YYYYMMDD}_{HH+1}.flac
    │
encoder FLAC continúa            ← mismo AVCodecContext, nuevo output
─────────────────────────────────────────────────────────────────────────
```

## 7. Flujo de liberación de recursos

```
CancellationToken.Cancel()
        │
        ▼
await loopTask (timeout 3s)
        │
        ▼
avcodec_flush_buffers()          ← flush final antes de cerrar
        │
avio_flush()                     ← garantiza que el último bloque llegó al disco
        │
av_frame_free()
        │
av_packet_free()
        │
avcodec_free_context() ← encoder FLAC
        │
swr_free()
        │
avcodec_free_context() ← decoder
        │
avformat_close_input()
        │
        ▼
      [LISTO — sin memoria nativa retenida — archivo FLAC válido]
```
