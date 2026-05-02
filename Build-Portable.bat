@echo off
title Wave Link Hotkey Manager - Build Portable

:: Request Administrator privileges
net session >nul 2>&1
if %errorLevel% == 0 (
    goto :build
) else (
    echo Requesting Administrator privileges...
    powershell -Command "Start-Process '%~dpnx0' -Verb RunAs"
    exit /b
)

:build
cd /d "%~dp0"
echo ========================================================
echo Building Wave Link Hotkey Manager Portable App...
echo ========================================================
call npm run build:portable

echo.
echo Build Complete! Check the 'dist' folder.
pause
