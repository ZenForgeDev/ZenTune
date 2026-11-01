@echo off
title ZenTune - bygg
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET SDK mangler. Installer den med:
    echo     winget install Microsoft.DotNet.SDK.10
    echo og dobbeltklikk Build.bat igjen.
    echo.
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Run
if errorlevel 1 (
    echo.
    echo Bygget feilet. Kopier feilmeldingen over og lim den inn til Claude.
    pause
    exit /b 1
)

echo.
echo Ferdig! ZenTune starter fra out\ZenTune.exe
timeout /t 3 >nul
