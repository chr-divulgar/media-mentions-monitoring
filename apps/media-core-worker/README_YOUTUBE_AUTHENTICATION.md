# Autenticación de YouTube sin Dependencia de Navegador

## Problema

El worker necesita autenticarse en YouTube para acceder a streams en vivo. Las soluciones tradicionales requieren:
- Dependencia de navegadores (Chrome, Edge, Firefox) disponibles en runtime
- Cookies que expiran y necesitan renovación manual
- Configuración compleja en entornos CI/CD

## Solución Implementada

El worker soporta **dos modos de autenticación**:

### Modo 1: Cookies de Archivo (Recomendado)

**Ventajas:**
- ✅ Sin dependencia del navegador en runtime
- ✅ Reutilizable múltiples veces
- ✅ Funciona en servidores/contenedores/CI
- ✅ Fácil de configurar

**Pasos:**

1. **Generar cookies una sola vez** (requiere Edge/Chrome instalado localmente):

   ```bash
   # Opción A: Usando yt-dlp directamente
   yt-dlp --cookies-from-browser edge --cookies ../../shared-cookies/youtube-cookies.txt https://www.youtube.com

   # Opción B: Automatizado con PowerShell
   .\setup_youtube_cookies.ps1
   ```

2. **Verificar el archivo se creó:**
   ```bash
   ls ../../shared-cookies/youtube-cookies.txt
   # Debe mostrar un archivo con contenido Netscape format
   ```

3. **Configurar en worker-options.json:**
   ```json
   {
     "useBrowserCookies": false,
     "youtubeCookiesFilePath": "../../shared-cookies/youtube-cookies.txt"
   }
   ```

4. **Ejecutar el worker:**
   ```bash
   dotnet run --project src/Workers/Operations.Worker
   ```

---

### Modo 2: Cookies del Navegador en Runtime

**Ventajas:**
- Cookies siempre frescas

**Desventajas:**
- ❌ Requiere navegador instalado y cerrado durante ejecución
- ❌ No funciona en servidores headless
- ❌ No funciona en CI/CD sin X11/Xvfb

**Configuración:**
```json
{
  "useBrowserCookies": true,
  "browserCookiesSource": "edge"
}
```

Navegadores soportados: `edge`, `chrome`, `firefox`, `opera`

---

## Troubleshooting

### Error: `ERROR: Could not copy Chrome cookie database`

**Causa:** El navegador especificado estaba abierto cuando se ejecutó yt-dlp

**Solución:**
```bash
# Cierra completamente el navegador
taskkill /F /IM msedge.exe
taskkill /F /IM chrome.exe

# Intenta de nuevo
yt-dlp --cookies-from-browser edge --cookies ../../shared-cookies/youtube-cookies.txt https://www.youtube.com
```

### Error: `Cookies file required but not found`

**Causa:** `youtubeCookiesFilePath` apunta a un archivo que no existe

**Solución:**
1. Verifica la ruta en `worker-options.json`
2. Genera el archivo siguiendo los pasos de arriba
3. Verifica permisos de lectura: `icacls ../../shared-cookies/youtube-cookies.txt`

### YouTube videos siguen bloqueados con `[AuthRequired]`

**Causa:** Las cookies han expirado o no tienen permisos suficientes

**Solución:**
1. Regenera las cookies: `yt-dlp --cookies-from-browser edge --cookies ../../shared-cookies/youtube-cookies.txt https://www.youtube.com --force-overwrites`
2. Verifica que la cuenta Edge tiene acceso a YouTube (prueba manualmente)
3. Algunos videos pueden requerir OAuth2 en lugar de cookies

---

## Alternativa: OAuth2 (Producción)

Para máxima robustez en producción, considera usar **credenciales de servicio de YouTube**:

1. Crear proyecto en Google Cloud
2. Generar credenciales de servicio (JSON)
3. Usar Google Auth en lugar de cookies

*(Por implementar en fase siguiente si es necesario)*

---

## Estado en vivo y reintento

El worker expone `GET http://localhost:5000/youtube/health` (ver
`docs/YOUTUBE_COOKIES_HTTP_ENDPOINT_SPEC.md` en la raíz del repo) — NestJS lo consulta en cada poll
de `GET /settings/youtube/status` para saber si el worker está vivo, sin inferirlo de archivos
locales. Al recibir cookies nuevas vía `POST /youtube/cookies`, el worker intenta recuperar de
inmediato cualquier fuente de YouTube actualmente excluida (en vez de esperar el próximo ciclo de
reconciliación programado, que puede tardar hasta ~29 minutos).

## Archivos Relacionados

- `stage/worker-options.json` — Configuración principal
- `../../shared-cookies/youtube-cookies.txt` — Archivo de cookies (generado)
- `setup_youtube_cookies.ps1` — Script para generar cookies
- `src/Workers/Operations.Worker/YtdlpLiveStreamUrlResolver.cs` — Lógica de resolución
- `src/Workers/Operations.Worker/OperationsWorkerOptionsLoader.cs` — Loader de configuración

---

## Resumen de Cambios

✅ **Cargador de configuración actualizado:**
- `OperationsWorkerOptionsLoader.cs` ahora deserializa `useBrowserCookies` y `browserCookiesSource` del JSON
- Soporta precedencia: env vars > JSON > defaults

✅ **Resolver de URLs mejorado:**
- `YtdlpLiveStreamUrlResolver.cs` comprueba `UseBrowserCookies` antes de usar navegador
- Fallback automático a archivo de cookies si la opción está desactivada
- Logging detallado para diagnóstico

✅ **Configuración actualizada:**
- `worker-options.json` con ejemplos comentados
- Valores por defecto configurables
