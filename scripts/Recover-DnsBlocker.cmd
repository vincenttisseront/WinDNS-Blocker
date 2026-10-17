@echo off
REM WinDNS Blocker — recover from broken / stuck-version installs.
REM Uninstalls ALL WinDNS Blocker ARP products, clears shortcuts + Program Files,
REM then clean-installs the MSI next to this script or in Downloads.
REM Run elevated (Administrator).
setlocal EnableExtensions EnableDelayedExpansion
set "UPGRADE_CODE={8F3A2C1D-9B4E-4F6A-A7C8-1D2E3F4A5B6C}"
set "ROOT=%~dp0"
set "LOG=%TEMP%\dnsblocker-recover-%RANDOM%.log"
set "PF=%ProgramFiles%\DnsBlocker"
if defined ProgramFiles(x86) set "PF=%ProgramFiles%\DnsBlocker"

echo === WinDNS Blocker recover ===
echo Log: %LOG%
echo.

set "MSI="
for %%F in ("%ROOT%DnsBlocker-*.msi") do set "MSI=%%~fF"
if not defined MSI (
  for /f "delims=" %%F in ('dir /b /o-d "%USERPROFILE%\Downloads\DnsBlocker-*.msi" 2^>nul') do (
    if not defined MSI set "MSI=%USERPROFILE%\Downloads\%%F"
  )
)
if not "%~1"=="" set "MSI=%~1"
if not defined MSI (
  echo [FAIL] No DnsBlocker-*.msi found.
  echo Download:
  echo   https://github.com/vincenttisseront/WinDNS-Blocker/releases/latest/download/DnsBlocker-1.0.23.msi
  echo Then: Recover-DnsBlocker.cmd "C:\path\DnsBlocker-1.0.23.msi"
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

echo [1/5] Stopping service and UI...
net stop DnsBlocker >>"%LOG%" 2>&1
taskkill /IM DnsBlocker.UI.exe /F >>"%LOG%" 2>&1
taskkill /IM DnsBlocker.Service.exe /F >>"%LOG%" 2>&1
ping -n 3 127.0.0.1 >nul

echo [2/5] Uninstalling every WinDNS Blocker product...
for /f "skip=1 tokens=*" %%P in ('wmic product where "Name like 'WinDNS Blocker%%'" get IdentifyingNumber 2^>nul') do (
  set "PC=%%P"
  set "PC=!PC: =!"
  if not "!PC!"=="" if not "!PC!"=="IdentifyingNumber" (
    echo   msiexec /x !PC!
    msiexec /x !PC! /qn /norestart >>"%LOG%" 2>&1
  )
)
REM Known ProductCodes (uuid5 DnsBlocker/1.0.x) — safe no-op if absent
for %%G in (
  {D28B9777-D4D5-5E3D-B629-D3D9C215120A}
  {C0E48BDA-FEEB-58EA-AA60-D8D8D6A5D322}
  {E0CA6415-35DC-5408-8EAC-AC670C017A39}
  {868B963E-5A3E-508F-9667-3C1EA232098D}
  {60726742-C306-5F22-B988-CC3CB3A4AC43}
  {6AEED747-277F-52EF-B308-BE92C5015C2C}
) do (
  msiexec /x %%G /qn /norestart >>"%LOG%" 2>&1
)

echo [3/5] Clearing Start Menu shortcuts...
set "SM=%ProgramData%\Microsoft\Windows\Start Menu\Programs\WinDNS Blocker"
if exist "%SM%" (
  del /f /q "%SM%\*.*" >>"%LOG%" 2>&1
  rd "%SM%" >>"%LOG%" 2>&1
)

echo [4/5] Removing leftover Program Files...
if exist "%PF%" (
  rd /s /q "%PF%" >>"%LOG%" 2>&1
)

echo [5/5] Clean install...
msiexec /i "%MSI%" /l*v "%LOG%" /qb /norestart
set "EC=!ERRORLEVEL!"
if not "!EC!"=="0" (
  echo [FAIL] msiexec exit !EC! — see %LOG%
  echo Reboot then re-run this script if needed.
  pause
  exit /b !EC!
)

echo.
echo [OK] Recover finished. Start Menu → WinDNS Blocker should show the new version.
echo Log: %LOG%
pause
exit /b 0
