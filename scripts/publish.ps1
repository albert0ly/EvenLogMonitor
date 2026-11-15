$ErrorActionPreference = 'Stop'
$serviceProject = Join-Path $PSScriptRoot '..\src\EventLogMonitor\EventLogMonitor.csproj'
$configuratorProject = Join-Path $PSScriptRoot '..\src\EventLogMonitor.Configurator\EventLogMonitor.Configurator.csproj'
$output = Join-Path $PSScriptRoot '..\publish'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0'
}

New-Item -ItemType Directory -Force -Path $output | Out-Null

dotnet publish $serviceProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publishing the Windows Service failed.' }

dotnet publish $configuratorProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publishing the Rule Builder failed.' }

# Keep one shared service configuration beside both executables. Do not overwrite an
# existing published configuration when the user is republishing an upgrade.
$configSource = Join-Path $PSScriptRoot '..\src\EventLogMonitor\appsettings.json'
$configTarget = Join-Path $output 'appsettings.json'
if (-not (Test-Path $configTarget)) {
    Copy-Item -Path $configSource -Destination $configTarget
}

Write-Host "Published files: $output"
Write-Host 'Run EventLogMonitor.Configurator.exe to create/edit rules.'
Write-Host 'Then run scripts\install.ps1 -PublishDirectory .\publish from elevated PowerShell to install the service.'
