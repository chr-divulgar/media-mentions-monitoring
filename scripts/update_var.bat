@echo off

setlocal

:: Initialize variables
set "adapter_found="
set "IP_ADDRESS="

:: Get the IP address of the Ethernet adapter Ethernet
for /f "tokens=*" %%i in ('ipconfig') do (
    echo %%i | findstr /i /c:"Ethernet adapter Ethernet" >nul && set "adapter_found=true"
    if defined adapter_found (
        echo %%i | findstr /i /c:"IPv4 Address" >nul && for /f "tokens=14 delims= " %%j in ("%%i") do set "IP_ADDRESS=%%j"
        if defined IP_ADDRESS goto :done
    )
)

:done


:: Check if cloudflared is running and terminate it
tasklist /FI "IMAGENAME eq cloudflared.exe" 2>NUL | find /I /N "cloudflared.exe">NUL
if "%ERRORLEVEL%"=="0" (
    echo Stopping existing cloudflared process...
    taskkill /F /IM cloudflared.exe >nul
)

:: Start Cloudflare tunnel in the background and redirect output to a file
start /B cmd /c "cloudflared tunnel --url http://%IP_ADDRESS%:3001 > cloudflared_output.log 2>&1"

:: Wait for a few seconds to ensure some output is generated
timeout /t 10 /nobreak > nul

:: Extract the URL from cloudflared_output.log using PowerShell
for /f "tokens=*" %%i in ('powershell -Command "Select-String -Path 'cloudflared_output.log' -Pattern 'https.*\.com' | ForEach-Object { if ($_.Matches[0].Value -notlike 'https://developers*') { $_.Matches[0].Value } }"') do set EXTRACTED_URL=%%i

:: Display the extracted URL
echo Extracted URL: %EXTRACTED_URL%

:: Set the VITE_API_LOCAL value
set "VITE_API_LOCAL=%EXTRACTED_URL%"

:: Authenticate with GitHub CLI using GH_TOKEN
:: echo %GH_TOKEN% | gh auth login --with-token

:: Set the repository variable using GitHub CLI
gh secret set VITE_API --body "%VITE_API_LOCAL%" --repo chr-divulgar/media-mentions-monitoring

:: Dispatch the GitHub Actions workflow
gh workflow run update-redirect.yml --repo chr-divulgar/media-mentions-monitoring

:: -----------------------------------------------------------------------
:: Update Firebase Authorized Domains with the new Cloudflare tunnel domain
:: -----------------------------------------------------------------------
echo Updating Firebase authorized domain...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$SA_JSON = '%~dp0media-mentions-monitoring-9ecb48f12fc4.json';" ^
  "$PROJECT_ID = 'media-mentions-monitoring';" ^
  "$NEW_DOMAIN = '%EXTRACTED_URL%' -replace 'https?://','';" ^
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
  "$tokenResp = Invoke-RestMethod -Uri 'https://oauth2.googleapis.com/token' -Method POST -Body @{ grant_type='urn:ietf:params:oauth:grant-type:jwt-bearer'; assertion=$jwt } -ContentType 'application/x-www-form-urlencoded';" ^
  "$token = $tokenResp.access_token;" ^
  "try { $cfg = Invoke-RestMethod -Uri \"https://identitytoolkit.googleapis.com/admin/v2/projects/$PROJECT_ID/config\" -Headers @{ Authorization=\"Bearer $token\" } -Method GET -ErrorAction Stop; $domains = @($cfg.authorizedDomains) } catch { Write-Host 'GET config failed, using default domains'; $domains = @('localhost', ($PROJECT_ID + '.firebaseapp.com'), ($PROJECT_ID + '.web.app')) };" ^
  "$domains = @($domains | Where-Object { $_ -ne '' -and $_ -ne $null });" ^
  "if ($domains -notcontains $NEW_DOMAIN) {" ^
  "  $domains += $NEW_DOMAIN;" ^
  "  $body = '{\"authorizedDomains\":[' + (($domains | ForEach-Object { '\"' + $_ + '\"' }) -join ',') + ']}';" ^
  "  Write-Host 'PATCH body:' $body;" ^
  "  try { Invoke-RestMethod -Uri \"https://identitytoolkit.googleapis.com/admin/v2/projects/$PROJECT_ID/config?updateMask=authorizedDomains\" -Headers @{ Authorization=\"Bearer $token\"; 'Content-Type'='application/json' } -Method PATCH -Body $body | Out-Null; Write-Host 'Firebase domain updated:' $NEW_DOMAIN } catch { $errBody = $_.Exception.Response; if ($errBody) { $reader = New-Object System.IO.StreamReader($errBody.GetResponseStream()); Write-Host 'ERROR:' $reader.ReadToEnd() } else { Write-Host 'ERROR:' $_.Exception.Message } };" ^
  "} else { Write-Host 'Domain already authorized:' $NEW_DOMAIN }"

:: -----------------------------------------------------------------------
:: Remove old trycloudflare.com domains from Firebase authorized domains
:: keeping only the most recent one
:: -----------------------------------------------------------------------
echo Cleaning up old Firebase authorized domains...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$SA_JSON = '%~dp0media-mentions-monitoring-9ecb48f12fc4.json';" ^
  "$PROJECT_ID = 'media-mentions-monitoring';" ^
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
  "$tokenResp = Invoke-RestMethod -Uri 'https://oauth2.googleapis.com/token' -Method POST -Body @{ grant_type='urn:ietf:params:oauth:grant-type:jwt-bearer'; assertion=$jwt } -ContentType 'application/x-www-form-urlencoded';" ^
  "$token = $tokenResp.access_token;" ^
  "$cfg = Invoke-RestMethod -Uri \"https://identitytoolkit.googleapis.com/admin/v2/projects/$PROJECT_ID/config\" -Headers @{ Authorization=\"Bearer $token\" } -Method GET;" ^
  "$domains = @($cfg.authorizedDomains);" ^
  "$cfDomains = @($domains | Where-Object { $_ -like '*.trycloudflare.com' });" ^
  "Write-Host 'trycloudflare.com domains found:' ($cfDomains -join ', ');" ^
  "if ($cfDomains.Count -le 1) { Write-Host 'Nothing to clean up.'; exit 0 };" ^
  "$latest = $cfDomains | Select-Object -Last 1;" ^
  "Write-Host 'Keeping:' $latest;" ^
  "$cleaned = @($domains | Where-Object { $_ -notlike '*.trycloudflare.com' -or $_ -eq $latest });" ^
  "$body = '{\"authorizedDomains\":[' + (($cleaned | ForEach-Object { '\"' + $_ + '\"' }) -join ',') + ']}';" ^
  "Invoke-RestMethod -Uri \"https://identitytoolkit.googleapis.com/admin/v2/projects/$PROJECT_ID/config?updateMask=authorizedDomains\" -Headers @{ Authorization=\"Bearer $token\"; 'Content-Type'='application/json' } -Method PATCH -Body $body | Out-Null;" ^
  "Write-Host 'Removed' ($cfDomains.Count - 1) 'old domain(s). Current:' $latest"

endlocal
