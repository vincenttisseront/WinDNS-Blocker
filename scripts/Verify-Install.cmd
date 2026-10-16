@echo off
REM Double-click friendly post-install check (elevated recommended).
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0doctor.ps1" -StartServiceIfStopped
echo.
pause
endlocal
