@echo off
setlocal
REM ─────────────────────────────────────────────────────────────────────────────
REM  Runs the WLHK test suite (no MSVC / AOT toolchain needed).
REM ─────────────────────────────────────────────────────────────────────────────

cd /d "%~dp0"
dotnet test WLHK.slnx -c Release
if errorlevel 1 (
    echo.
    echo Tests FAILED.
    pause
    exit /b 1
)

echo.
echo All tests passed.
pause
