@echo off
setlocal enabledelayedexpansion

set NODE_ENV=production
set SCRIPT_DIR=%~dp0
set LOG_FILE=%SCRIPT_DIR%radio-alert.log
set PROJECT_DIR=%SCRIPT_DIR%..\

cd /d "%PROJECT_DIR%"

echo.>>"%LOG_FILE%"
echo [%date% %time%] Starting Radio Alert>>"%LOG_FILE%"
echo [%date% %time%] NODE_ENV=%NODE_ENV%>>"%LOG_FILE%"

REM Run in a new window so this script can exit
start "Radio Alert - %NODE_ENV%" cmd /k "cd /d "%PROJECT_DIR%" && pnpm run start >> "%LOG_FILE%" 2>&1"

echo [%date% %time%] Application started in new window>>"%LOG_FILE%"

endlocal
exit /b 0
exit /b %EXIT_CODE%