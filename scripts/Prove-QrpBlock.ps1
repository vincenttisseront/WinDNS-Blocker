#Requires -Version 5.1
<#
.SYNOPSIS
  Minimal Microsoft-supported QRP that MUST block beeg.com on Windows Server DNS.

.DESCRIPTION
  Proves the Action/Fqdn shape WinDNS Blocker relies on. Run elevated on the DNS
  server (or with -ComputerName). After this script, Resolve-DnsName beeg.com
  against that server must NOT return public A/AAAA (IGNORE → timeout/drop;
  DENY → REFUSED).

  Docs:
    https://learn.microsoft.com/en-us/windows-server/networking/dns/deploy/apply-filters-on-dns-queries
    Add-DnsServerQueryResolutionPolicy -Action IGNORE -Fqdn 'EQ,*.contoso.com'

.PARAMETER ComputerName
  Optional remote DNS server. Omit for local DNS Server role.

.PARAMETER Action
  IGNORE (default, Microsoft blackhole) or DENY (REFUSED).

.EXAMPLE
  .\Prove-QrpBlock.ps1
  .\Prove-QrpBlock.ps1 -ComputerName windc04.ar-systems.fr -Action IGNORE
#>
[CmdletBinding()]
param(
    [string]$ComputerName = '',
    [ValidateSet('IGNORE', 'DENY')]
    [string]$Action = 'IGNORE',
    [string]$Name = 'DnsBlocker_Test',
    [string]$Domain = 'beeg.com'
)

$ErrorActionPreference = 'Stop'
Import-Module DnsServer -ErrorAction Stop

$fqdn = "EQ,$Domain,*.$Domain"
$local = [string]::IsNullOrWhiteSpace($ComputerName) -or $ComputerName -in @('.', 'localhost', '127.0.0.1')

Write-Host "=== Prove QRP block ===" -ForegroundColor Cyan
Write-Host "Exact command that must work (server-level QueryProcessing, NOT -ApplyOnRecursion):"
if ($local) {
    Write-Host @"
Add-DnsServerQueryResolutionPolicy ``
  -Name '$Name' ``
  -Action $Action ``
  -Fqdn '$fqdn' ``
  -ProcessingOrder 1 ``
  -PassThru
"@ -ForegroundColor Yellow
} else {
    Write-Host @"
Add-DnsServerQueryResolutionPolicy ``
  -ComputerName '$ComputerName' ``
  -Name '$Name' ``
  -Action $Action ``
  -Fqdn '$fqdn' ``
  -ProcessingOrder 1 ``
  -PassThru
"@ -ForegroundColor Yellow
}

# Remove leftover test policy
try {
    if ($local) {
        Remove-DnsServerQueryResolutionPolicy -Name $Name -Force -ErrorAction SilentlyContinue
    } else {
        Remove-DnsServerQueryResolutionPolicy -ComputerName $ComputerName -Name $Name -Force -ErrorAction SilentlyContinue
    }
} catch { }

if ($local) {
    $created = Add-DnsServerQueryResolutionPolicy -Name $Name -Action $Action -Fqdn $fqdn -ProcessingOrder 1 -PassThru -ErrorAction Stop
} else {
    $created = Add-DnsServerQueryResolutionPolicy -ComputerName $ComputerName -Name $Name -Action $Action -Fqdn $fqdn -ProcessingOrder 1 -PassThru -ErrorAction Stop
}

$created | Format-List Name, Action, IsEnabled, ProcessingOrder, Level
Write-Host "IsEnabled must be True; Level must be Server; AppliesOn must be QueryProcessing (not Recursion)." -ForegroundColor DarkGray

Clear-DnsServerCache -Force -ErrorAction SilentlyContinue
if ($local) {
    Restart-Service DNS -Force -ErrorAction Stop
} else {
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='DNS'" -ComputerName $ComputerName -ErrorAction Stop
    $null = Invoke-CimMethod -InputObject $svc -MethodName StopService -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='DNS'" -ComputerName $ComputerName -ErrorAction Stop
    $null = Invoke-CimMethod -InputObject $svc -MethodName StartService -ErrorAction Stop
    Start-Sleep -Seconds 5
}

$server = $(if ($local) { $env:COMPUTERNAME } else { $ComputerName })
# Canary: recursion must still work
$canary = @(Resolve-DnsName -Name www.microsoft.com -Server $server -Type A -DnsOnly -ErrorAction Stop |
    Where-Object { $_.IPAddress })
if ($canary.Count -eq 0) { throw "Canary www.microsoft.com did not resolve via $server — cannot trust block proof." }
Write-Host "CANARY OK via $server" -ForegroundColor Green

try {
    $addrs = @(Resolve-DnsName -Name $Domain -Server $server -Type A -DnsOnly -ErrorAction Stop |
        Where-Object { $_.IPAddress } | ForEach-Object { $_.IPAddress.ToString() })
    if ($addrs.Count -gt 0) {
        throw "FAIL: $Domain still resolves to $($addrs -join ',') via $server — QRP not blocking."
    }
    Write-Host "PASS: $Domain returned no A/AAAA via $server" -ForegroundColor Green
} catch {
    if ($_.Exception.Message -match 'FAIL:') { throw }
    if ($_.Exception.Message -match '(?i)NXDOMAIN|REFUSED|timed out|Timeout|SERVFAIL|No response') {
        Write-Host "PASS: $Domain blocked ($($_.Exception.Message))" -ForegroundColor Green
    } else {
        throw
    }
}

Write-Host @"

Cleanup (optional):
  Remove-DnsServerQueryResolutionPolicy -Name '$Name' -Force

WinDNS Blocker sync must create the same shape (sentinel DnsBlocker_00001 = EQ,beeg.com,*.beeg.com)
and FAIL the sync if this Resolve still returns Cloudflare IPs.
"@ -ForegroundColor Cyan
