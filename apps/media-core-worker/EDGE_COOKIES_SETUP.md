# Edge Browser Cookies Setup para YouTube

## Modo navegador ✅

Esta guía es para habilitar el modo en que el worker **lee cookies directamente de Edge**, sin
archivo intermedio. El modo activo por defecto en este repo es el de archivo
(`useBrowserCookies: false` — ver `README_YOUTUBE_AUTHENTICATION.md`); estos pasos son para
cuando quieras cambiar a este otro modo.

### Configuración necesaria

En `stage/worker-options.json`:

```json
{
  "useBrowserCookies": true,
  "browserCookiesSource": "edge",
  "youtubeCookiesFilePath": "../../shared-cookies/youtube-cookies.txt",
  "youtubeCookiesAlertFilePath": "stage/cookies/youtube-auth-required.flag"
}
```

**Explicación**:
- `useBrowserCookies: true` → Usa cookies del navegador directamente (sin archivo)
- `browserCookiesSource: "edge"` → Lee de Microsoft Edge
- `youtubeCookiesFilePath` → Se ignora cuando `useBrowserCookies: true` (fallback solo si desactivas)

## ¿Qué Necesitas Hacer? 🎬

### Paso 1: Asegúrate de estar logueado en YouTube con Edge
```
1. Abre Microsoft Edge
2. Ve a https://www.youtube.com
3. Arriba a la derecha debe mostrar TU PERFIL (no "Iniciar sesión")
4. Si ves "Iniciar sesión" → Haz login primero
```

### Paso 2: Ejecuta el Worker
```bash
cd apps/media-core-worker
dotnet run --project src/Workers/Operations.Worker
```

**El worker automáticamente usará tus cookies de Edge logueado.**

## Cómo Funciona 🔧

Cuando el worker resuelve URLs de YouTube:

```
1. Lee cookies automáticamente de Edge (via yt-dlp)
2. Ejecuta: yt-dlp --cookies-from-browser edge <youtube_url>
3. Obtiene la URL del stream
4. ¡Listo! Sin archivos, sin complicaciones
```

## Si Quieres Volver a Archivo de Cookies ⚙️

Si necesitas usar un archivo de cookies en lugar de Edge:

```json
{
  "useBrowserCookies": false,
  "browserCookiesSource": "edge",
  "youtubeCookiesFilePath": "../../shared-cookies/youtube-cookies.txt"
}
```

Luego coloca el archivo `.txt` en `../../shared-cookies/youtube-cookies.txt` (formato Netscape).

## Cambiar a Otro Navegador 🌐

Si quieres usar Chrome, Firefox u Opera en lugar de Edge:

```json
{
  "useBrowserCookies": true,
  "browserCookiesSource": "chrome"  // o "firefox", "opera"
}
```

**Valores soportados**:
- `"chrome"` → Google Chrome
- `"firefox"` → Mozilla Firefox
- `"edge"` → Microsoft Edge
- `"opera"` → Opera Browser

## Troubleshooting ❌

### "Permission Denied" o "No cookies found"

Edge necesita estar cerrado durante la primera lectura:

```powershell
# 1. Cierra todas las ventanas de Edge
# 2. Ejecuta el worker
dotnet run --project src/Workers/Operations.Worker

# 3. Recién luego puedes abrir Edge nuevamente
```

### "YouTube: Private Video" o "Sign In Required"

Tus cookies expiraron o no son válidas:

```powershell
# 1. Ve a https://www.youtube.com en Edge
# 2. Cierra Edge completamente
# 3. Vuelve a ejecutar el worker
```

### Quieres Ver qué Cookies Lee

El worker registra en los logs:

```
[YtdlpResolver] Using edge browser cookies for source youtube_123.
```

## Resumen Final ✨

- ✅ No necesitas exportar cookies manualmente
- ✅ Edge logueado = Cookies automáticas
- ✅ Cambiar navegador = Solo editar `browserCookiesSource`
- ✅ Sin archivos `.txt`, sin complicaciones
- ✅ yt-dlp maneja todo internamente

**¡Listo para capturar YouTube!** 🎥
