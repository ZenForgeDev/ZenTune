@echo off
setlocal
title ZenTune - lag Setup.exe
cd /d "%~dp0"

echo === ZenTune: lager Setup.exe ===
echo.

if not exist "%~dp0build.ps1" (
    echo Fant ikke build.ps1 ved siden av denne filen.
    echo Legg byggsetup.bat i ZenTune-mappen, der build.ps1 ligger.
    echo.
    pause
    exit /b 1
)

if not exist "%~dp0installer\bygg-setup.ps1" (
    echo Fant ikke installer\bygg-setup.ps1. Mappen installer maa ligge i ZenTune-mappen.
    echo.
    pause
    exit /b 1
)

call :sjekk_sdk
if "%HAR_SDK%"=="1" goto lag

echo .NET 10 SDK mangler. Prover a installere det med winget...
echo.

where winget >nul 2>nul
if errorlevel 1 (
    echo winget finnes ikke paa denne PC-en.
    echo Last ned .NET 10 SDK her, installer det, og dobbeltklikk denne filen igjen:
    echo     https://dotnet.microsoft.com/download/dotnet/10.0
    echo.
    pause
    exit /b 1
)

winget install --id Microsoft.DotNet.SDK.10 -e --source winget --accept-package-agreements --accept-source-agreements
if errorlevel 1 (
    echo.
    echo Installasjonen av .NET 10 SDK feilet. Les feilmeldingen over.
    echo Du kan ogsaa laste det ned selv: https://dotnet.microsoft.com/download/dotnet/10.0
    echo.
    pause
    exit /b 1
)

rem Den nye PATH-en er ikke aktiv i dette vinduet ennaa, saa legg til standardmappen selv
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "PATH=%ProgramFiles%\dotnet;%PATH%"

call :sjekk_sdk
if not "%HAR_SDK%"=="1" (
    echo.
    echo .NET 10 SDK er installert, men er ikke klart i dette vinduet.
    echo Lukk dette vinduet og dobbeltklikk byggsetup.bat paa nytt.
    echo.
    pause
    exit /b 1
)

:lag
echo.
echo .NET 10 SDK er klart. Bygger ZenTune og lager Setup.exe...
echo Inno Setup installeres automatisk hvis det mangler.
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\bygg-setup.ps1"
if errorlevel 1 (
    echo.
    echo Det gikk ikke. Kopier feilmeldingen over og lim den inn til Claude.
    echo.
    pause
    exit /b 1
)

echo.
echo Ferdig! Setup.exe ligger i mappen dist.
timeout /t 5 >nul
exit /b 0

:sjekk_sdk
set "HAR_SDK=0"
where dotnet >nul 2>nul || exit /b 0
for /f "tokens=1 delims=." %%v in ('dotnet --list-sdks 2^>nul') do if "%%v"=="10" set "HAR_SDK=1"
exit /b 0
