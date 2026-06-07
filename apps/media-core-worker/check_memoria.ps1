$workerProcess = Get-Process -Name "MediaOpsCore.Workers.Operations.Worker" -ErrorAction SilentlyContinue
if ($null -eq $workerProcess) {
  Write-Host "No se encontro el proceso MediaOpsCore.Workers.Operations.Worker en ejecucion." -ForegroundColor Yellow
  exit 1
}

$workerPid = $workerProcess.Id
$out = "c:\Users\juanb\Documents\chr-divulgar\worker-memory-log.csv"

if (-not (Test-Path $out)) {
  "timestamp,pid,workingSetMB,privateMB,handles,threads" | Out-File -FilePath $out -Encoding ascii
}

while ($true) {
  $p = Get-Process -Id $workerPid -ErrorAction SilentlyContinue
  if ($null -eq $p) { break }

  $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
  $ws = [math]::Round($p.WorkingSet64 / 1MB, 2)
  $pm = [math]::Round($p.PrivateMemorySize64 / 1MB, 2)
  $line = "$ts,$($p.Id),$ws,$pm,$($p.Handles),$($p.Threads.Count)"
  Add-Content -Path $out -Value $line -Encoding ascii

  Start-Sleep -Seconds 10
}