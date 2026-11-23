@echo off
setlocal
set "central_program=%~dp0app\Central.exe"
if not exist "%central_program%" (
    if not exist "%~dp0atual.txt" goto ausente
    set /p central_versao=<"%~dp0atual.txt"
    call :release
)
if not exist "%central_program%" goto ausente
"%central_program%" --abrir
if errorlevel 1 pause
exit /b

:release
set "central_program=%~dp0releases\%central_versao%\Central.exe"
exit /b

:ausente
echo Os arquivos da Central nao foram encontrados.
pause
exit /b 1
