#Requires -RunAsAdministrator
param(
    [string]$ServiceName = "DnsBlocker",
    [string]$InstallRoot = "C:\Program Files\DnsBlocker"
)

$ErrorActionPreference = "Stop"
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Service removed."
}

if (Test-Path $InstallRoot) {
    Remove-Item -Recurse -Force $InstallRoot
    Write-Host "Install directory removed."
}
