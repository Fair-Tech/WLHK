@echo off
title Wave Link Hotkey Manager - Build Installer

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
echo Verifying dependencies...
echo ========================================================
call npm install
if %errorLevel% neq 0 (
    echo ERROR: npm install failed. Aborting build.
    pause
    exit /b 1
)

echo ========================================================
echo Building Wave Link Hotkey Manager Installer (NSIS)...
echo ========================================================
call npm run build:installer

echo.
echo Build Complete! Check the 'dist' folder.
pause
