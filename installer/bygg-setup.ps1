# Builds ZenTune and wraps it in a Setup.exe with Inno Setup.
# Run it through byggsetup.bat, or directly:  powershell -ExecutionPolicy Bypass -File installer\bygg-setup.ps1
# The finished installer ends up in the "dist" folder.

$ErrorActionPreference = "Stop"

function Find-Iscc {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

try {
    $root = Split-Path -Parent $PSScriptRoot
    Set-Location $root

    # The version comes from the app project, so the Setup.exe name always matches the app
    $csproj = Join-Path $root "src\ZenTune.App\ZenTune.App.csproj"
    $m = [regex]::Match((Get-Content $csproj -Raw), "<Version>\s*([^<\s]+)\s*</Version>")
    $version = if ($m.Success) { $m.Groups[1].Value } else { "0.0.0" }
    Write-Host "ZenTune $version" -ForegroundColor Cyan

    Write-Host ""
    Write-Host "1/3  Building ZenTune..." -ForegroundColor Cyan
    & (Join-Path $root "build.ps1")
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $root "out\ZenTune.exe"))) {
        throw "The build failed, so no Setup.exe was made. Read the error above."
    }

    Write-Host ""
    Write-Host "2/3  Looking for Inno Setup..." -ForegroundColor Cyan
    $iscc = Find-Iscc
    if (-not $iscc) {
        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
            throw "Inno Setup is not installed and winget is not available. Install Inno Setup 6 from https://jrsoftware.org/isdl.php and run this again."
        }
        Write-Host "Inno Setup is missing. Installing it with winget..."
        winget install --id JRSoftware.InnoSetup -e --source winget --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -ne 0) {
            throw "Could not install Inno Setup with winget. Install Inno Setup 6 from https://jrsoftware.org/isdl.php and run this again."
        }
        $iscc = Find-Iscc
        if (-not $iscc) {
            throw "Inno Setup was installed, but ISCC.exe was not found yet. Close this window and run byggsetup.bat again."
        }
    }
    Write-Host "Using $iscc"

    Write-Host ""
    Write-Host "3/3  Making Setup.exe..." -ForegroundColor Cyan
    & $iscc "/DAppVersion=$version" (Join-Path $root "installer\ZenTune.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup could not make the installer. Read the error above." }

    $setup = Join-Path $root "dist\ZenTune-Setup-$version.exe"
    if (-not (Test-Path $setup)) { throw "Inno Setup finished, but $setup was not created." }

    Write-Host ""
    Write-Host "Done: $setup" -ForegroundColor Green
    Start-Process explorer.exe "/select,`"$setup`""
}
catch {
    Write-Host ""
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
