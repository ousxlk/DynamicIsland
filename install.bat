@echo off
setlocal
title Install Dynamic Island

set "SOURCE=%~dp0DynamicIsland.exe"
set "INSTALL_DIR=%LOCALAPPDATA%\Programs\DynamicIsland"
set "TARGET=%INSTALL_DIR%\DynamicIsland.exe"

if not exist "%SOURCE%" (
  echo ERROR: DynamicIsland.exe was not found beside this installer.
  echo Extract the complete downloaded package, then run install.bat again.
  pause
  exit /b 1
)

if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
copy /Y "%SOURCE%" "%TARGET%" >nul
if errorlevel 1 (
  echo ERROR: Could not copy Dynamic Island to your user Programs folder.
  pause
  exit /b 1
)

reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v DynamicIsland /t REG_SZ /d "\"%TARGET%\""/f >nul
if errorlevel 1 (
  echo ERROR: Could not enable Windows startup.
  pause
  exit /b 1
)

start "" "%TARGET%"
echo.
echo Dynamic Island is installed and will start automatically when you sign in to Windows.
echo You can turn this off from the Dynamic Island tray icon: Start with Windows.
timeout /t 4 >nul
