param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\publish'),
    [string]$ConfigPath = ''
)
$ErrorActionPreference = 'Stop'
$publishPath = (Resolve-Path $PublishDirectory).Path
$exePath = Join-Path $publishPath 'EventLogMonitor.Configurator.exe'
if (-not (Test-Path $exePath)) { throw "Configurator not found: $exePath. Run scripts\publish.ps1 first." }
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $publishPath 'appsettings.json'
}
if (Test-Path $ConfigPath) {
    Start-Process -FilePath $exePath -ArgumentList ('"{0}"' -f $ConfigPath)
} else {
    Start-Process -FilePath $exePath
}
