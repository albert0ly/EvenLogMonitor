param([string]$ServiceName = 'EventLogMonitor')
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell window (Run as Administrator).'
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "Service '$ServiceName' is not installed."
    exit 0
}

if ($service.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
sc.exe delete $ServiceName | Out-Null
Write-Host "Removed service '$ServiceName'. Published files were not deleted."
