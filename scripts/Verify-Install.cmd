@echo off
REM Double-click friendly post-install check — opens the GUI Setup assistant (no PowerShell required).
setlocal
set "ROOT=%~dp0"
set "UI=%ROOT%UI\DnsBlocker.UI.exe"
if exist "%UI%" (
  start "" "%UI%" /setup
  exit /b 0
)
REM Fallback if UI binary missing (sparse install) — last resort PowerShell doctor
if exist "%ROOT%doctor.ps1" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%doctor.ps1" -StartServiceIfStopped
  echo.
  pause
  exit /b %ERRORLEVEL%
)
echo [FAIL] Ni UI\DnsBlocker.UI.exe ni doctor.ps1 — reinstallez le MSI 1.0.7+.
pause
exit /b 1
