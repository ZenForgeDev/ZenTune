# Builds ZenTune into the "out" folder. Run from the ZenTune folder:
#   .\build.ps1          -> release build
#   .\build.ps1 -Run     -> build and start ZenTune

param([switch]$Run)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# Start from a clean slate so old files never mix with the new build.
# The obj folders matter too: without this, the compiled screens (ZenTune.pri) are sometimes not rebuilt
# after a XAML change, and the app shows the old menu.
foreach ($dir in @("out", "src\ZenTune.App\obj", "src\ZenTune.App\bin", "src\ZenTune.Core\obj", "src\ZenTune.Core\bin")) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}

dotnet publish src\ZenTune.App\ZenTune.App.csproj `
    -c Release `
    -r win-x64 `
    -p:Platform=x64 `
    -o out

if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; exit 1 }

if (-not (Test-Path out\ZenTune.pri)) {
    Write-Host "Warning: out\ZenTune.pri is missing. The app will not start." -ForegroundColor Yellow
}

if (-not (Test-Path out\Assets\ZenTune-graph.ico)) {
    Write-Host "Warning: out\Assets\ZenTune-graph.ico is missing. The app icon switch will not work." -ForegroundColor Yellow
}

Write-Host "Done: out\ZenTune.exe" -ForegroundColor Green

if ($Run) { Start-Process "$PSScriptRoot\out\ZenTune.exe" }
