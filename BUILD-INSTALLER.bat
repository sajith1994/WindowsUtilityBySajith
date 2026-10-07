@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0create-installer.ps1" -InstallInnoSetup
if errorlevel 1 (
  echo.
  echo Installer creation failed. Review the error shown above.
) else (
  echo.
  echo Installer creation completed successfully.
)
pause
