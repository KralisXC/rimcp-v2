@echo off
chcp 65001 >nul
setlocal

set /p proceed=Continue and rebuild index? [Y/N]
if /i not "%proceed%"=="Y" (
    echo Cancelled.
    pause
    exit /b 0
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-and-index.ps1" -ForceClean

echo.
echo Done.
pause