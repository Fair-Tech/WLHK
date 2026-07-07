@echo off
setlocal
REM ─────────────────────────────────────────────────────────────────────────────
REM  WLHK v2 portable build: Native AOT single exe (requires .NET 10 SDK and
REM  the MSVC C++ build tools for the native linker step).
REM ─────────────────────────────────────────────────────────────────────────────

REM Put vswhere on PATH (the AOT toolchain uses it to locate link.exe)
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"

REM Enter a VS developer environment if available so link.exe resolves
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
)
if defined VSDIR call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul

cd /d "%~dp0src"
dotnet publish -c Release -r win-x64 -o "..\dist"
if errorlevel 1 (
    echo.
    echo Build FAILED.
    pause
    exit /b 1
)

del "..\dist\WLHK.pdb" 2>nul
echo.
echo ─────────────────────────────────────────────
echo  Done: %~dp0dist\WLHK.exe
echo  (true single exe - no runtime, no extraction)
echo ─────────────────────────────────────────────
pause
