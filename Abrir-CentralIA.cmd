@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
set "APP=%~dp0src\CentralIA"
set "EXE=%APP%\app\CentralIA.exe"
if exist "%EXE%" goto run
if not exist "%APP%\CentralIA.csproj" goto missing
set "SDK=dotnet"
if exist "%LOCALAPPDATA%\CentralProducaoBuild\sdk-10.0.404\dotnet.exe" set "SDK=%LOCALAPPDATA%\CentralProducaoBuild\sdk-10.0.404\dotnet.exe"
echo Compilando Central IA...
"%SDK%" build "%APP%\CentralIA.csproj" -c Release --configfile "%APP%\NuGet.Config" -o "%APP%\bin\launcher"
if errorlevel 1 goto missing
set "EXE=%APP%\bin\launcher\CentralIA.exe"
:run
"%EXE%" %*
set "RESULT=%ERRORLEVEL%"
if "%RESULT%"=="0" exit /b 0
if not "%~1"=="--sem-navegador" pause
exit /b %RESULT%
:missing
echo Build indisponivel. Extraia todo o pacote Windows ou instale o SDK do .NET 10 para compilar o codigo.
if not "%~1"=="--sem-navegador" pause
exit /b 1
