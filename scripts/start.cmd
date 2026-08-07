@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" %*
set RC=%ERRORLEVEL%
if not "%RC%"=="0" (
    echo.
    echo [start.cmd] start FAILED, exit code %RC%. See messages above and step logs in:
    echo   %~dp0..\logs
    pause
)
exit /b %RC%
