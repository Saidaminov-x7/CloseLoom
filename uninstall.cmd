@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
if errorlevel 1 (
  echo CloseLoom removal failed. See the message above.
  pause
  exit /b 1
)
echo CloseLoom was removed.
pause
