<#
.SYNOPSIS
  Converts the radio hour files already on disk to the quality the capture now records at (Opus mono, 16 kHz, 32 kbps).

.DESCRIPTION
  Run once, after confirming that the new hours are being recorded at the new quality (the closed hours weigh ~14 MB
  instead of ~28 MB). The work is done by the worker service itself (it has to be running), through its
  /maintenance/radio-opus routes: it keeps the archive ledger consistent, so nothing is uploaded to the FTP again.

  -DryRun -Limit 3   converts 3 hours into the service's stage-evidence\radio-opus-preview folder, touching nothing,
                     so they can be listened to and the time per file measured.
  (no switches)      converts every radio hour that is still at the old quality, newest first.
  -Status            shows how it is going. -Stop lets the files in hand finish and stops.

  Stopping this script with Ctrl+C does NOT stop the conversion (it runs in the service): use -Stop.

.EXAMPLE
  .\convert-radio-opus.ps1 -DryRun -Limit 3
  .\convert-radio-opus.ps1
  .\convert-radio-opus.ps1 -Stop
#>
param(
    [switch]$DryRun,
    [int]$Limit = 0,
    [switch]$Stop,
    [switch]$Status,
    # Start even when the latest closed hours are still big (the capture is not on the new quality yet).
    [switch]$Force,
    [int]$PollSeconds = 10,
    [string]$Endpoint = 'http://localhost:5000'
)

$ErrorActionPreference = 'Stop'
$base = "$Endpoint/maintenance/radio-opus"

function Show-Status($s) {
    $savedGb = ($s.bytesBefore - $s.bytesAfter) / 1GB
    $left = if ($null -ne $s.estimatedMinutesLeft) { " · faltan ~$([int]$s.estimatedMinutesLeft) min" } else { '' }
    $mode = if ($s.dryRun) { ' (prueba)' } else { '' }
    Write-Host ("[{0:HH:mm:ss}] {1}{2}: {3} de {4} convertidos · {5} ya pequeños · {6} fallidos · {7} para después · liberado {8:N1} GB · {9} s por archivo{10}" -f `
            (Get-Date), $s.state, $mode, $s.converted, $s.total, $s.skipped, $s.failed, $s.deferred, $savedGb, $s.averageSecondsPerFile, $left)
}

try {
    if ($Stop) {
        Show-Status (Invoke-RestMethod -Uri "$base/stop" -Method Post)
        return
    }

    $current = Invoke-RestMethod -Uri $base -Method Get
    if ($Status) {
        Show-Status $current
        if ($null -ne $current.recentClosedHoursAverageMb) { Write-Host ("Las últimas horas cerradas pesan {0:N1} MB en promedio." -f $current.recentClosedHoursAverageMb) }
        return
    }

    if ($null -ne $current.recentClosedHoursAverageMb) {
        Write-Host ("Las últimas horas cerradas pesan {0:N1} MB en promedio (~14 MB con la calidad nueva, ~28 MB con la anterior)." -f $current.recentClosedHoursAverageMb)
        if ($current.recentClosedHoursAverageMb -gt 20 -and -not $Force) {
            Write-Host 'La captura todavía no graba con la calidad nueva. Reinicia el servicio con radioOpusBitrateKbps = 32 y espera ~2 horas, o usa -Force.' -ForegroundColor Yellow
            return
        }
    }

    $body = @{ dryRun = [bool]$DryRun }
    if ($Limit -gt 0) { $body.limit = $Limit }
    $started = Invoke-RestMethod -Uri "$base/convert" -Method Post -Body ($body | ConvertTo-Json) -ContentType 'application/json'
    Write-Host ("Conversión iniciada{0}: {1} archivos." -f $(if ($DryRun) { ' (prueba)' } else { '' }), $started.total)

    do {
        Start-Sleep -Seconds $PollSeconds
        $current = Invoke-RestMethod -Uri $base -Method Get
        Show-Status $current
    } while ($current.state -eq 'running')

    if ($DryRun) {
        Write-Host "Archivos de prueba en: $($current.previewDirectory)" -ForegroundColor Green
    }
}
catch {
    Write-Host "No se pudo hablar con el servicio en $Endpoint (¿está corriendo?): $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
