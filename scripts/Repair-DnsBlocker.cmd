@echo off
REM WinDNS Blocker — GUI repair / install health (no PowerShell CLI required).
REM Looks for DnsBlocker.UI.exe under UI\ next to this script.
setlocal
set "ROOT=%~dp0"
set "UI=%ROOT%UI\DnsBlocker.UI.exe"
if not exist "%UI%" (
  echo [FAIL] Introuvable: %UI%
  echo Reinstallez le MSI WinDNS Blocker 1.0.7+.
  pause
  exit /b 1
)
start "" "%UI%" /setup
exit /b 0
