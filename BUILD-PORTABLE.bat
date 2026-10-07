@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1"
if errorlevel 1 (
  echo.
  echo Portable build failed. Review the error shown above.
) else (
  echo.
  echo Portable build completed successfully.
)
pause
