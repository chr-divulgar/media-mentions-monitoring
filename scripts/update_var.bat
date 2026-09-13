@echo off

REM IMMEDIATE LOGGING TO TEMP - before anything else
set "TEMP_DEBUG_LOG=%TEMP%\update_var_debug.log"
(
  echo [%date% %time%] Script started
) > "%TEMP_DEBUG_LOG%" 2>&1

setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
set "LOG_FILE=%SCRIPT_DIR%update_var.log"
set "CLOUDFLARE_LOG=%SCRIPT_DIR%cloudflared_output.log"
set "SERVICE_ACCOUNT_FILE=%SCRIPT_DIR%media-mentions-monitoring-9ecb48f12fc4.json"
set "PROJECT_ID=media-mentions-monitoring"
set "GITHUB_REPO=chr-divulgar/media-mentions-monitoring"
set "TASK_ERROR=0"
set "QUICK_TUNNEL_ATTEMPTS=3"
set "URL_EXTRACTION_ATTEMPTS=18"
set "HEALTH_RETRIES=3"
set "HEALTH_TIMEOUT_SEC=10"

echo [%date% %time%] SCRIPT_DIR=%SCRIPT_DIR% >> "%TEMP_DEBUG_LOG%"

REM Try to create log file in SCRIPT_DIR, fallback to TEMP if it fails
set "LOG_FALLBACK=%TEMP%\update_var_fallback.log"
>> "%LOG_FILE%" echo [%date% %time%] Log file access check
if errorlevel 1 (
  echo [%date% %time%] Cannot write to %LOG_FILE%, using fallback >> "%TEMP_DEBUG_LOG%"
  >> "%LOG_FALLBACK%" echo [%date% %time%] WARNING: Cannot write to %LOG_FILE%, using fallback: %LOG_FALLBACK%
  set "LOG_FILE=%LOG_FALLBACK%"
) else (
  echo [%date% %time%] Primary log location available >> "%TEMP_DEBUG_LOG%"
)

cd /d "%SCRIPT_DIR%"
echo [%date% %time%] Changed to %CD% >> "%TEMP_DEBUG_LOG%"

echo.>>"%LOG_FILE%"
echo ================================================================>>"%LOG_FILE%"
echo [%date% %time%] Task Scheduler Execution>>"%LOG_FILE%"
echo [%date% %time%] SCRIPT_DIR: %SCRIPT_DIR%>>"%LOG_FILE%"
echo [%date% %time%] LOG_FILE: %LOG_FILE%>>"%LOG_FILE%"
echo [%date% %time%] Current User: %USERNAME%>>"%LOG_FILE%"
echo [%date% %time%] Current Path: %CD%>>"%LOG_FILE%"
echo [%date% %time%] TEMP_DEBUG_LOG: %TEMP_DEBUG_LOG%>>"%LOG_FILE%"
echo ================================================================>>"%LOG_FILE%"
echo [%date% %time%] Starting update_var.bat>>"%LOG_FILE%"

set "LOCK_DIR=%TEMP%\update_var_sync.lock"
set "LOCK_HEARTBEAT=%LOCK_DIR%\heartbeat.txt"
set "LOCK_STALE_SEC=480"

if exist "%LOCK_DIR%" (
  set "LOCK_AGE_SEC=999999"
  if exist "%LOCK_HEARTBEAT%" (
    for /f %%s in ('powershell -NoProfile -Command "[int]((Get-Date) - (Get-Item '%LOCK_HEARTBEAT%').LastWriteTime).TotalSeconds"') do set "LOCK_AGE_SEC=%%s"
  )
  if !LOCK_AGE_SEC! LSS %LOCK_STALE_SEC% (
    echo [%date% %time%] ERROR: Another instance is already running ^(lock heartbeat !LOCK_AGE_SEC!s old^). Exiting.>>"%LOG_FILE%"
    set "TASK_ERROR=1"
    goto :finalize
  )
  echo [%date% %time%] Stale lock detected ^(!LOCK_AGE_SEC!s old^), taking over.>>"%LOG_FILE%"
  rmdir /s /q "%LOCK_DIR%" >nul 2>&1
)
mkdir "%LOCK_DIR%" 2>nul
if errorlevel 1 (
  echo [%date% %time%] ERROR: Could not acquire lock ^(lost race to another instance^). Exiting.>>"%LOG_FILE%"
  set "TASK_ERROR=1"
  goto :finalize
)
call :touch_lock

for %%a in (cloudflared gh powershell) do (
  where %%a >nul 2>&1
  if errorlevel 1 (
    echo [%date% %time%] ERROR: Required command not found: %%a>>"%LOG_FILE%"
    set "TASK_ERROR=1"
    goto :finalize
  )
)

if not exist "%SERVICE_ACCOUNT_FILE%" (
  echo [%date% %time%] ERROR: Missing service account file: %SERVICE_ACCOUNT_FILE%>>"%LOG_FILE%"
  set "TASK_ERROR=1"
  goto :finalize
)

:main_cycle
call :touch_lock
set "IP_ADDRESS="
for /L %%a in (1,1,12) do (
  set "IP_ADDRESS="
  for /f "usebackq delims=" %%i in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%get_local_ipv4.ps1"`) do set "IP_ADDRESS=%%i"
  for /f "tokens=* delims= " %%i in ("!IP_ADDRESS!") do set "IP_ADDRESS=%%i"
  if defined IP_ADDRESS goto :ip_resolved
  echo [%date% %time%] Waiting for network stack ^(attempt %%a/12^)...>>"%LOG_FILE%"
  timeout /t 5 /nobreak >nul
)

:ip_resolved

if not defined IP_ADDRESS (
  echo [%date% %time%] ERROR: Could not resolve a valid IPv4 address.>>"%LOG_FILE%"
  goto :cycle_failed
)

echo(%IP_ADDRESS%| findstr /R /C:"^[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >nul
if errorlevel 1 (
  echo [%date% %time%] ERROR: Invalid IPv4 format detected: "%IP_ADDRESS%".>>"%LOG_FILE%"
  goto :cycle_failed
)

echo [%date% %time%] Using local IP: %IP_ADDRESS%>>"%LOG_FILE%"

set "BACKEND_URL=http://%IP_ADDRESS%:3001/index.html"
set /a BACKEND_WAIT_COUNT=0
:wait_backend
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; try { $r = Invoke-WebRequest -UseBasicParsing -Uri '%BACKEND_URL%' -TimeoutSec 3; if ($r.StatusCode -ge 200 -and $r.StatusCode -lt 400) { exit 0 } else { exit 1 } } catch { exit 1 }" >nul 2>&1
if errorlevel 1 (
  set /a BACKEND_WAIT_COUNT+=1
  if !BACKEND_WAIT_COUNT! EQU 1 echo [%date% %time%] Backend not ready on %BACKEND_URL%, polling every 5s until it is...>>"%LOG_FILE%"
  call :touch_lock
  ping -n 6 127.0.0.1 >nul
  goto :wait_backend
)
echo [%date% %time%] Backend is up after !BACKEND_WAIT_COUNT! check^(s^), proceeding with tunnel bootstrap.>>"%LOG_FILE%"

set "EXTRACTED_URL="
set "TUNNEL_READY=0"
for /L %%t in (1,1,%QUICK_TUNNEL_ATTEMPTS%) do (
  echo [%date% %time%] Tunnel bootstrap attempt %%t/%QUICK_TUNNEL_ATTEMPTS%...>>"%LOG_FILE%"

  tasklist /FI "IMAGENAME eq cloudflared.exe" 2>NUL | find /I "cloudflared.exe" >NUL
  if !ERRORLEVEL! EQU 0 (
    echo [%date% %time%] Stopping existing cloudflared.exe process...>>"%LOG_FILE%"
    taskkill /F /IM cloudflared.exe >nul 2>&1
  )

  del /q "%CLOUDFLARE_LOG%" >nul 2>&1
  echo [%date% %time%] Starting cloudflared tunnel...>>"%LOG_FILE%"
  start "cloudflared-tunnel" /min cmd /c cloudflared tunnel --url http://%IP_ADDRESS%:3001 ^> "%CLOUDFLARE_LOG%" 2^>^&1

  set "EXTRACTED_URL="
  for /L %%r in (1,1,%URL_EXTRACTION_ATTEMPTS%) do (
    if not defined EXTRACTED_URL (
      for /f "usebackq delims=" %%u in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%get_cloudflare_url.ps1" -LogPath "%CLOUDFLARE_LOG%"`) do set "EXTRACTED_URL=%%u"
      if not defined EXTRACTED_URL (
        echo [%date% %time%] Waiting for Cloudflare URL ^(attempt %%r/%URL_EXTRACTION_ATTEMPTS%^)...>>"%LOG_FILE%"
        ping -n 6 127.0.0.1 >nul
      )
    )
  )

  for /f "tokens=* delims= " %%u in ("!EXTRACTED_URL!") do set "EXTRACTED_URL=%%u"
  if not defined EXTRACTED_URL (
    echo [%date% %time%] ERROR: Could not extract Cloudflare URL from %CLOUDFLARE_LOG% on attempt %%t.>>"%LOG_FILE%"
    ping -n 3 127.0.0.1 >nul
  ) else (
    echo(!EXTRACTED_URL!| findstr /R /C:"^https://[A-Za-z0-9-][A-Za-z0-9-]*\.trycloudflare\.com$" >nul
    if errorlevel 1 (
      echo [%date% %time%] ERROR: Invalid Cloudflare URL extracted: "!EXTRACTED_URL!" on attempt %%t.>>"%LOG_FILE%"
      set "EXTRACTED_URL="
      ping -n 3 127.0.0.1 >nul
    ) else (
      call :check_endpoint_health "!EXTRACTED_URL!"
      if "!HEALTH_OK!"=="1" (
        set "TUNNEL_READY=1"
        goto :tunnel_ready
      )
      echo [%date% %time%] Endpoint health check failed for !EXTRACTED_URL! on attempt %%t.>>"%LOG_FILE%"
    )
  )
)

:tunnel_ready
if not "%TUNNEL_READY%"=="1" (
  echo [%date% %time%] ERROR: Failed to bootstrap a healthy Cloudflare quick tunnel after %QUICK_TUNNEL_ATTEMPTS% attempts.>>"%LOG_FILE%"
  goto :cycle_failed
)

set "VITE_API_LOCAL=%EXTRACTED_URL%"
echo [%date% %time%] Extracted URL: %VITE_API_LOCAL%>>"%LOG_FILE%"

echo [%date% %time%] Checking gh authentication...>>"%LOG_FILE%"
gh auth status >>"%LOG_FILE%" 2>&1
if errorlevel 1 (
  echo [%date% %time%] ERROR: gh CLI is not authenticated.>>"%LOG_FILE%"
  goto :cycle_failed
)

echo [%date% %time%] Attempting to set GitHub secret VITE_API...>>"%LOG_FILE%"
echo [%date% %time%] Command: gh secret set VITE_API --body "%VITE_API_LOCAL%" --repo %GITHUB_REPO%>>"%LOG_FILE%"
gh secret set VITE_API --body "%VITE_API_LOCAL%" --repo %GITHUB_REPO% >>"%LOG_FILE%" 2>&1
set "GH_SECRET_RESULT=%errorlevel%"
echo [%date% %time%] gh secret set exit code: %GH_SECRET_RESULT%>>"%LOG_FILE%"
if %GH_SECRET_RESULT% neq 0 (
  echo [%date% %time%] ERROR: Failed to update GitHub secret VITE_API.>>"%LOG_FILE%"
  goto :cycle_failed
)
echo [%date% %time%] GitHub secret VITE_API updated successfully>>"%LOG_FILE%"

echo [%date% %time%] Attempting to dispatch GitHub workflow update-redirect.yml...>>"%LOG_FILE%"
echo [%date% %time%] Command: gh workflow run update-redirect.yml --repo %GITHUB_REPO%>>"%LOG_FILE%"
gh workflow run update-redirect.yml --repo %GITHUB_REPO% >>"%LOG_FILE%" 2>&1
set "GH_WORKFLOW_RESULT=%errorlevel%"
echo [%date% %time%] gh workflow run exit code: %GH_WORKFLOW_RESULT%>>"%LOG_FILE%"
if %GH_WORKFLOW_RESULT% neq 0 (
  echo [%date% %time%] ERROR: Failed to dispatch workflow update-redirect.yml.>>"%LOG_FILE%"
  goto :cycle_failed
)
echo [%date% %time%] GitHub workflow dispatched successfully>>"%LOG_FILE%"

echo [%date% %time%] Updating Firebase authorized domain...>>"%LOG_FILE%"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference = 'Stop';" ^
  "$SA_JSON = '%SERVICE_ACCOUNT_FILE%';" ^
  "$PROJECT_ID = '%PROJECT_ID%';" ^
  "$NEW_DOMAIN = ('%EXTRACTED_URL%' -replace 'https?://','').Trim();" ^
  "if ([string]::IsNullOrWhiteSpace($NEW_DOMAIN)) { throw 'NEW_DOMAIN is empty.' };" ^
  "$sa = Get-Content $SA_JSON | ConvertFrom-Json;" ^
  "$now = [int][DateTimeOffset]::UtcNow.ToUnixTimeSeconds();" ^
  "$header = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{\"alg\":\"RS256\",\"typ\":\"JWT\"}')) -replace '=+$','' -replace '\+','-' -replace '/','_';" ^
  "$payloadJson = '{\"iss\":\"' + $sa.client_email + '\",\"scope\":\"https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/firebase https://www.googleapis.com/auth/identitytoolkit\",\"aud\":\"https://oauth2.googleapis.com/token\",\"exp\":' + ($now+3600) + ',\"iat\":' + $now + '}';" ^
  "$payloadB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payloadJson)) -replace '=+$','' -replace '\+','-' -replace '/','_';" ^
  "$signingInput = $header + '.' + $payloadB64;" ^
  "$pkPem = $sa.private_key -replace '-----BEGIN PRIVATE KEY-----','' -replace '-----END PRIVATE KEY-----','' -replace '\n','' -replace '\r','';" ^
  "$pkBytes = [Convert]::FromBase64String($pkPem);" ^
  "$cngKey = [System.Security.Cryptography.CngKey]::Import($pkBytes, [System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob);" ^
  "$rsa = New-Object System.Security.Cryptography.RSACng($cngKey);" ^
  "$sig = $rsa.SignData([Text.Encoding]::UTF8.GetBytes($signingInput), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1);" ^
  "$sigB64 = [Convert]::ToBase64String($sig) -replace '=+$','' -replace '\+','-' -replace '/','_';" ^
  "$jwt = $signingInput + '.' + $sigB64;" ^
  "$tokenResp = Invoke-RestMethod -Uri 'https://oauth2.googleapis.com/token' -Method POST -Body @{ grant_type='urn:ietf:params:oauth:grant-type:jwt-bearer'; assertion=$jwt } -ContentType 'application/x-www-form-urlencoded' -ErrorAction Stop;" ^
  "$token = $tokenResp.access_token;" ^
  "try { $cfg = Invoke-RestMethod -Uri \"https://identitytoolkit.googleapis.com/admin/v2/projects/$PROJECT_ID/config\" -Headers @{ Authorization=\"Bearer $token\" } -Method GET -ErrorAction Stop; $domains = @($cfg.authorizedDomains) } catch { $domains = @('localhost', ($PROJECT_ID + '.firebaseapp.com'), ($PROJECT_ID + '.web.app')) };" ^
  "$domains = @($domains | Where-Object { $_ -ne '' -and $_ -ne $null });" ^
  "if ($domains -notcontains $NEW_DOMAIN) {" ^
  "  $domains += $NEW_DOMAIN;" ^
  "  $body = '{\"authorizedDomains\":[' + (($domains | ForEach-Object { '\"' + $_ + '\"' }) -join ',') + ']}';" ^
  "  Invoke-RestMethod -Uri \"https://identitytoolkit.googleapis.com/admin/v2/projects/$PROJECT_ID/config?updateMask=authorizedDomains\" -Headers @{ Authorization=\"Bearer $token\"; 'Content-Type'='application/json' } -Method PATCH -Body $body -ErrorAction Stop | Out-Null;" ^
  "}" ^
  "Write-Host 'Firebase domain ensured:' $NEW_DOMAIN" >>"%LOG_FILE%" 2>&1
if errorlevel 1 (
  echo [%date% %time%] ERROR: Failed to update Firebase authorized domain.>>"%LOG_FILE%"
  goto :cycle_failed
)

echo [%date% %time%] Cleaning up old Firebase trycloudflare domains...>>"%LOG_FILE%"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference = 'Stop';" ^
  "$SA_JSON = '%SERVICE_ACCOUNT_FILE%';" ^
  "$PROJECT_ID = '%PROJECT_ID%';" ^
  "$sa = Get-Content $SA_JSON | ConvertFrom-Json;" ^
  "$now = [int][DateTimeOffset]::UtcNow.ToUnixTimeSeconds();" ^
  "$header = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{\"alg\":\"RS256\",\"typ\":\"JWT\"}')) -replace '=+$','' -replace '\+','-' -replace '/','_';" ^
  "$payloadJson = '{\"iss\":\"' + $sa.client_email + '\",\"scope\":\"https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/firebase https://www.googleapis.com/auth/identitytoolkit\",\"aud\":\"https://oauth2.googleapis.com/token\",\"exp\":' + ($now+3600) + ',\"iat\":' + $now + '}';" ^
  "$payloadB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payloadJson)) -replace '=+$','' -replace '\+','-' -replace '/','_';" ^
  "$signingInput = $header + '.' + $payloadB64;" ^
  "$pkPem = $sa.private_key -replace '-----BEGIN PRIVATE KEY-----','' -replace '-----END PRIVATE KEY-----','' -replace '\n','' -replace '\r','';" ^
  "$pkBytes = [Convert]::FromBase64String($pkPem);" ^
  "$cngKey = [System.Security.Cryptography.CngKey]::Import($pkBytes, [System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob);" ^
  "$rsa = New-Object System.Security.Cryptography.RSACng($cngKey);" ^
  "$sig = $rsa.SignData([Text.Encoding]::UTF8.GetBytes($signingInput), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1);" ^
  "$sigB64 = [Convert]::ToBase64String($sig) -replace '=+$','' -replace '\+','-' -replace '/','_';" ^
  "$jwt = $signingInput + '.' + $sigB64;" ^
  "$tokenResp = Invoke-RestMethod -Uri 'https://oauth2.googleapis.com/token' -Method POST -Body @{ grant_type='urn:ietf:params:oauth:grant-type:jwt-bearer'; assertion=$jwt } -ContentType 'application/x-www-form-urlencoded' -ErrorAction Stop;" ^
  "$token = $tokenResp.access_token;" ^
  "$cfg = Invoke-RestMethod -Uri \"https://identitytoolkit.googleapis.com/admin/v2/projects/$PROJECT_ID/config\" -Headers @{ Authorization=\"Bearer $token\" } -Method GET -ErrorAction Stop;" ^
  "$domains = @($cfg.authorizedDomains);" ^
  "$cfDomains = @($domains | Where-Object { $_ -like '*.trycloudflare.com' });" ^
  "if ($cfDomains.Count -gt 1) {" ^
  "  $latest = $cfDomains | Select-Object -Last 1;" ^
  "  $cleaned = @($domains | Where-Object { $_ -notlike '*.trycloudflare.com' -or $_ -eq $latest });" ^
  "  $body = '{\"authorizedDomains\":[' + (($cleaned | ForEach-Object { '\"' + $_ + '\"' }) -join ',') + ']}';" ^
  "  Invoke-RestMethod -Uri \"https://identitytoolkit.googleapis.com/admin/v2/projects/$PROJECT_ID/config?updateMask=authorizedDomains\" -Headers @{ Authorization=\"Bearer $token\"; 'Content-Type'='application/json' } -Method PATCH -Body $body -ErrorAction Stop | Out-Null;" ^
  "}" ^
  "Write-Host 'Firebase cleanup completed.'" >>"%LOG_FILE%" 2>&1
if errorlevel 1 (
  echo [%date% %time%] ERROR: Failed to clean old Firebase domains.>>"%LOG_FILE%"
  goto :cycle_failed
)

tasklist /FI "IMAGENAME eq cloudflared.exe" 2>NUL | find /I "cloudflared.exe" >NUL
if errorlevel 1 (
  echo [%date% %time%] ERROR: cloudflared.exe is not running after sync.>>"%LOG_FILE%"
  goto :cycle_failed
)

echo [%date% %time%] Script completed successfully.>>"%LOG_FILE%"
goto :monitor_loop

:cycle_failed
echo [%date% %time%] Cycle failed, retrying in 5 minutes...>>"%LOG_FILE%"
call :touch_lock
ping -n 301 127.0.0.1 >nul
goto :main_cycle

:monitor_loop
call :touch_lock
ping -n 301 127.0.0.1 >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; try { $r = Invoke-WebRequest -UseBasicParsing -Uri '%BACKEND_URL%' -TimeoutSec 3; if ($r.StatusCode -ge 200 -and $r.StatusCode -lt 400) { exit 0 } else { exit 1 } } catch { exit 1 }" >nul 2>&1
if errorlevel 1 (
  echo [%date% %time%] Monitor: backend %BACKEND_URL% is down, re-running full sync...>>"%LOG_FILE%"
  goto :main_cycle
)
call :check_endpoint_health "%EXTRACTED_URL%"
if not "!HEALTH_OK!"=="1" (
  echo [%date% %time%] Monitor: tunnel %EXTRACTED_URL% is down, re-running full sync...>>"%LOG_FILE%"
  goto :main_cycle
)
echo [%date% %time%] Monitor: backend and tunnel still healthy.>>"%LOG_FILE%"
goto :monitor_loop

:check_endpoint_health
set "HEALTH_URL=%~1/index.html"
set "HEALTH_OK=0"
for /L %%h in (1,1,%HEALTH_RETRIES%) do (
  echo [%date% %time%] Health-check %%h/%HEALTH_RETRIES% for !HEALTH_URL!...>>"%LOG_FILE%"
  powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; try { $r = Invoke-WebRequest -UseBasicParsing -Uri '%HEALTH_URL%' -TimeoutSec %HEALTH_TIMEOUT_SEC%; if ($r.StatusCode -ge 200 -and $r.StatusCode -lt 400) { exit 0 } else { exit 1 } } catch { exit 1 }" >nul 2>&1
  if not errorlevel 1 (
    set "HEALTH_OK=1"
    echo [%date% %time%] Health-check passed for !HEALTH_URL!.>>"%LOG_FILE%"
    goto :check_endpoint_health_done
  )
  echo [%date% %time%] Health-check failed for !HEALTH_URL! on try %%h.>>"%LOG_FILE%"
  ping -n 3 127.0.0.1 >nul
)

:check_endpoint_health_done
exit /b 0

:touch_lock
>"%LOCK_HEARTBEAT%" echo %date% %time%
exit /b 0

:finalize
if not "%TASK_ERROR%"=="0" (
  echo [%date% %time%] Script failed with TASK_ERROR=%TASK_ERROR%.>>"%LOG_FILE%"
) else (
  echo [%date% %time%] Script finished with exit code 0.>>"%LOG_FILE%"
)
rmdir /s /q "%LOCK_DIR%" >nul 2>&1

endlocal & exit /b %TASK_ERROR%
