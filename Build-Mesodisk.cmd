@echo off
rem Double-click to build Mesodisk.exe (see build.ps1). Output goes to bin\ and dist\.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
if errorlevel 1 (
  echo.
  echo BUILD FAILED - see the messages above.
  pause
  exit /b 1
)
echo.
pause
