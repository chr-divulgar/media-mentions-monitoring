# Script para generar cookies de YouTube una sola vez
# Uso: .\setup_youtube_cookies.ps1
# Escribe en la ruta canonica compartida con NestJS (shared-cookies/ en la raiz del repo),
# la misma que lee worker-options.json (youtubeCookiesFilePath).

$CookiesDir = "../../shared-cookies"
$CookiesFile = "$CookiesDir/youtube-cookies.txt"
$YtdlpBin = "bin/yt-dlp.exe"

# Crear directorio si no existe
if (-not (Test-Path $CookiesDir)) {
    New-Item -ItemType Directory -Path $CookiesDir -Force | Out-Null
    Write-Host "[OK] Directorio $CookiesDir creado"
}

# Verificar que yt-dlp existe
if (-not (Test-Path $YtdlpBin)) {
    Write-Host "[ERROR] yt-dlp no encontrado en $YtdlpBin"
    Write-Host "        Descarga desde https://github.com/yt-dlp/yt-dlp/releases"
    exit 1
}

Write-Host "[INFO] Generando cookies de YouTube desde Edge..."

# Ejecutar yt-dlp para extraer cookies de Edge
& $YtdlpBin --cookies-from-browser edge --cookies "$CookiesFile" --quiet --no-warnings "https://www.youtube.com" 2>&1 | Out-Null

# Verificar que el archivo se genero
if (Test-Path $CookiesFile) {
    Write-Host "[OK] Cookies generadas"
    Write-Host "     Ruta: $CookiesFile"
    exit 0
} else {
    Write-Host "[ERROR] No se genero el archivo de cookies"
    exit 1
}
