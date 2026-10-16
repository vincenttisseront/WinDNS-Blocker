@echo off
REM WinDNS Blocker — GUI repair / install health (no PowerShell CLI required).
REM If UI.exe is present → launch /setup. If missing (broken upgrade) → Recover-DnsBlocker.cmd.
setlocal
set "ROOT=%~dp0"
set "UI=%ROOT%UI\DnsBlocker.UI.exe"
set "RECOVER=%ROOT%Recover-DnsBlocker.cmd"

if exist "%UI%" (
  start "" "%UI%" /setup
  exit /b 0
)

echo [WARN] Introuvable: %UI%
echo Installation incomplete (souvent apres une mise a jour in-app).
echo Lancement de Recover-DnsBlocker.cmd ...
echo.
if exist "%RECOVER%" (
  call "%RECOVER%" %*
  exit /b %ERRORLEVEL%
)

echo [FAIL] Recover-DnsBlocker.cmd manquant.
echo Telechargez le MSI 1.0.20+ puis:
echo   msiexec /x {E0CA6415-35DC-5408-8EAC-AC670C017A39} /qn
echo   msiexec /i DnsBlocker-1.0.20.msi /qb
pause
exit /b 1
