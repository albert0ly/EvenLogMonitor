param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\publish'),
    [string]$ServiceName = 'EventLogMonitor'
)
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell window (Run as Administrator).'
}

$publishPath = (Resolve-Path $PublishDirectory).Path
$exePath = Join-Path $publishPath 'EventLogMonitor.exe'
$configPath = Join-Path $publishPath 'appsettings.json'
if (-not (Test-Path $exePath)) { throw "Executable not found: $exePath. Run scripts\publish.ps1 first." }
if (-not (Test-Path $configPath)) { throw "appsettings.json not found: $configPath" }

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    throw "Service '$ServiceName' already exists. Run scripts\uninstall.ps1 first if you intend to replace it."
}

New-Service -Name $ServiceName -BinaryPathName "`"$exePath`"" `
    -DisplayName 'Windows Event Log Monitor' -Description 'Monitors configured Windows Event Logs and sends email or SMS webhook notifications.' `
    -StartupType Automatic | Out-Null

Set-Service -Name $ServiceName -StartupType Automatic
Start-Service -Name $ServiceName
Write-Host "Installed and started $ServiceName."
Write-Host "Configuration: $configPath"
Write-Host 'The service runs as LocalSystem by default, which can read local Security logs.'
