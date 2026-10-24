@echo off
rem Starts the API and the portal it serves (port 3001), and starts it again if it ever stops.
rem Run when Windows starts by the scheduled task "MediaMentionsWebApi" (see docs, PLAN_MAESTRO RQ-072).
cd /d "%~dp0"
set NODE_ENV=production
set BACK_PORT=3001
if not exist "%~dp0logs" mkdir "%~dp0logs"
:run
echo [%date% %time%] starting web-api >> "%~dp0logs\web-api.log"
node dist\main >> "%~dp0logs\web-api.log" 2>&1
echo [%date% %time%] web-api stopped (exit %errorlevel%); starting again in 10 s >> "%~dp0logs\web-api.log"
rem ping instead of timeout: timeout needs a console, which a scheduled task does not have.
ping -n 11 127.0.0.1 > nul
goto run
