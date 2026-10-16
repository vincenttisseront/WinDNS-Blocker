@echo off
REM WinDNS Blocker — recover from a broken/half-installed product (missing UI.exe, dead shortcuts).
REM Uninstalls ALL WinDNS Blocker ARP entries by UpgradeCode, clears Start Menu shortcuts,
REM then installs the newest DnsBlocker-*.msi found next to this script or in Downloads.
REM Run elevated (Administrator). No PowerShell required for the uninstall path.
setlocal EnableExtensions EnableDelayedExpansion
set "UPGRADE_CODE={8F3A2C1D-9B4E-4F6A-A7C8-1D2E3F4A5B6C}"
set "ROOT=%~dp0"
set "LOG=%TEMP%\dnsblocker-recover-%RANDOM%.log"

echo === WinDNS Blocker recover ===
echo Log: %LOG%
echo.

REM Prefer an MSI beside this script, else newest in Downloads, else prompt.
set "MSI="
for %%F in ("%ROOT%DnsBlocker-*.msi") do set "MSI=%%~fF"
if not defined MSI (
  for /f "delims=" %%F in ('dir /b /o-d "%USERPROFILE%\Downloads\DnsBlocker-*.msi" 2^>nul') do (
    if not defined MSI set "MSI=%USERPROFILE%\Downloads\%%F"
  )
)
if not defined MSI (
  echo [FAIL] No DnsBlocker-*.msi found next to this script or in Downloads.
  echo Download https://github.com/vincenttisseront/WinDNS-Blocker/releases/latest/download/DnsBlocker-1.0.20.msi
  echo then re-run this script, or pass the MSI path:
  echo   Recover-DnsBlocker.cmd "C:\path\DnsBlocker-1.0.20.msi"
  if not "%~1"=="" set "MSI=%~1"
)
if not "%~1"=="" set "MSI=%~1"
if not defined MSI (
  pause
  exit /b 1
)
if not exist "%MSI%" (
  echo [FAIL] MSI not found: %MSI%
  pause
  exit /b 1
)
echo Using MSI: %MSI%
echo.

echo [1/4] Stopping service and UI...
net stop DnsBlocker >>"%LOG%" 2>&1
taskkill /IM DnsBlocker.UI.exe /F >>"%LOG%" 2>&1
taskkill /IM DnsBlocker.Service.exe /F >>"%LOG%" 2>&1
ping -n 3 127.0.0.1 >nul

echo [2/4] Uninstalling every WinDNS Blocker product (UpgradeCode %UPGRADE_CODE%)...
REM Enumerate ProductCodes related to our UpgradeCode via MSI API (wmic + msiexec /x).
for /f "skip=1 tokens=*" %%P in ('wmic product where "Name like 'WinDNS Blocker%%'" get IdentifyingNumber 2^>nul') do (
  set "PC=%%P"
  set "PC=!PC: =!"
  if not "!PC!"=="" if not "!PC!"=="IdentifyingNumber" (
    echo   msiexec /x !PC!
    msiexec /x !PC! /qn /norestart >>"%LOG%" 2>&1
  )
)
REM Known ProductCodes for recent builds (safe no-op if not installed)
for %%G in (
  {D28B9777-D4D5-5E3D-B629-D3D9C215120A}
  {C0E48BDA-FEEB-58EA-AA60-D8D8D6A5D322}
  {E0CA6415-35DC-5408-8EAC-AC670C017A39}
  {868B963E-5A3E-508F-9667-3C1EA232098D}
) do (
  msiexec /x %%G /qn /norestart >>"%LOG%" 2>&1
)

echo [3/4] Clearing stale Start Menu shortcuts...
set "SM=%ProgramData%\Microsoft\Windows\Start Menu\Programs\WinDNS Blocker"
if exist "%SM%" (
  del /f /q "%SM%\*.*" >>"%LOG%" 2>&1
  rd "%SM%" >>"%LOG%" 2>&1
)

echo [4/4] Clean install...
msiexec /i "%MSI%" /l*v "%LOG%" /qb /norestart
set "EC=!ERRORLEVEL!"
if not "!EC!"=="0" (
  echo [FAIL] msiexec exit !EC! — see %LOG%
  echo If still broken, reboot then re-run this script.
  pause
  exit /b !EC!
)

echo.
echo [OK] Recover finished. Launch WinDNS Blocker from the Start Menu.
echo Log: %LOG%
pause
exit /b 0
