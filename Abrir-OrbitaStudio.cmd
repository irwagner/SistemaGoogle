@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
if exist "%~dp0Orbita.Desktop.exe" (
  if /I "%~1"=="--navegador" (
    "%~dp0studio\Orbita.Studio.exe"
  ) else (
    start "" "%~dp0Orbita.Desktop.exe"
  )
  exit /b
)
if exist "%~dp0Orbita.Studio.exe" (
  "%~dp0Orbita.Studio.exe" %*
  exit /b
)
set "SDK=dotnet"
if exist "%LOCALAPPDATA%\CentralProducaoBuild\sdk-10.0.404\dotnet.exe" set "SDK=%LOCALAPPDATA%\CentralProducaoBuild\sdk-10.0.404\dotnet.exe"
"%SDK%" run --project "%~dp0src\Orbita.Studio\Orbita.Studio.csproj" -c Release -- %*
exit /b %ERRORLEVEL%
