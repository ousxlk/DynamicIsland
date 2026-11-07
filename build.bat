@echo off
cd /d "%~dp0"
dotnet publish DynamicIsland.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
echo.
echo Done: %~dp0out\DynamicIsland.exe
pause
