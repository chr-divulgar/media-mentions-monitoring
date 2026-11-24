<#
.SYNOPSIS
  Converts the radio hour files already on disk to the quality the capture now records at (Opus mono, 16 kHz, 32 kbps).

.DESCRIPTION
  Run after confirming that the new hours are being recorded at the new quality (the closed hours weigh ~14 MB
  instead of ~28 MB). The work is done by the worker service itself (it has to be running), through its
  /maintenance/radio-opus routes: it keeps the archive ledger consistent, so nothing is uploaded to the FTP again.

  It can be run as many times as needed: what is already converted is skipped, so running it again CONTINUES.

  -Parallel N        how many files are re-encoded at the same time (1 to 16, default 8). If a conversion is already
                     running with another number, it is stopped (the files in hand finish) and restarted with this one.
  -DryRun -Limit 3   converts 3 hours into the service's stage-evidence\radio-opus-preview folder, touching nothing,
                     so they can be listened to and the time per file measured.
  (no switches)      converts every radio hour that is still at the old quality, newest first.
  -Status            shows how it is going. -Stop lets the files in hand finish and stops.

  Stopping this script with Ctrl+C does NOT stop the conversion (it runs in the service): use -Stop.

.EXAMPLE
  .\convert-radio-opus.ps1 -DryRun -Limit 3
  .\convert-radio-opus.ps1 -Parallel 8
  .\convert-radio-opus.ps1 -Stop
#>
param(
    [switch]$DryRun,
    [int]$Limit = 0,
    [ValidateRange(1, 16)]
    [int]$Parallel = 8,
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
    Write-Host ("[{0:HH:mm:ss}] {1}{2} ({3} a la vez): {4} de {5} convertidos · {6} ya pequeños · {7} fallidos · {8} para después · liberado {9:N1} GB · {10} s por archivo{11}" -f `
            (Get-Date), $s.state, $mode, $s.parallel, $s.converted, $s.total, $s.skipped, $s.failed, $s.deferred, $savedGb, $s.averageSecondsPerFile, $left)
}

function Watch-Conversion {
    do {
        Start-Sleep -Seconds $PollSeconds
        $current = Invoke-RestMethod -Uri $base -Method Get
        Show-Status $current
    } while ($current.state -eq 'running')
    return $current
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

    if ($Parallel -gt 12) {
        Write-Host "Con más de 12 a la vez la captura en vivo puede quedarse sin procesador: vigila con -Status y baja el número si hace falta." -ForegroundColor Yellow
    }

    if ($current.state -eq 'running') {
        if ($PSBoundParameters.ContainsKey('Parallel') -and $current.parallel -ne $Parallel) {
            Write-Host "La conversión va con $($current.parallel) a la vez: se detiene (terminan los archivos en curso) y sigue con $Parallel."
            Invoke-RestMethod -Uri "$base/stop" -Method Post | Out-Null
            $deadline = (Get-Date).AddMinutes(15)
            do {
                Start-Sleep -Seconds 5
                $current = Invoke-RestMethod -Uri $base -Method Get
            } while ($current.state -eq 'running' -and (Get-Date) -lt $deadline)
            if ($current.state -eq 'running') {
                throw 'La conversión no terminó de detenerse en 15 minutos; vuelve a intentarlo con -Status.'
            }
        }
        else {
            Write-Host "Ya hay una conversión en marcha ($($current.parallel) a la vez). Mostrando el avance; usa -Parallel N para cambiar el número."
            Show-Status (Watch-Conversion)
            return
        }
    }

    if ($null -ne $current.recentClosedHoursAverageMb) {
        Write-Host ("Las últimas horas cerradas pesan {0:N1} MB en promedio (~14 MB con la calidad nueva, ~28 MB con la anterior)." -f $current.recentClosedHoursAverageMb)
        if ($current.recentClosedHoursAverageMb -gt 20 -and -not $Force) {
            Write-Host 'La captura todavía no graba con la calidad nueva. Reinicia el servicio con radioOpusBitrateKbps = 32 y espera ~2 horas, o usa -Force.' -ForegroundColor Yellow
            return
        }
    }

    $body = @{ dryRun = [bool]$DryRun; parallel = $Parallel }
    if ($Limit -gt 0) { $body.limit = $Limit }
    $started = Invoke-RestMethod -Uri "$base/convert" -Method Post -Body ($body | ConvertTo-Json) -ContentType 'application/json'
    Write-Host ("Conversión iniciada{0}: {1} archivos, {2} a la vez." -f $(if ($DryRun) { ' (prueba)' } else { '' }), $started.total, $started.parallel)

    $current = Watch-Conversion
    if ($DryRun) {
        Write-Host "Archivos de prueba en: $($current.previewDirectory)" -ForegroundColor Green
    }
}
catch {
    Write-Host "No se pudo completar (¿el servicio está corriendo en $Endpoint?): $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
