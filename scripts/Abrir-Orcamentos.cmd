@echo off
setlocal
"%~dp0app\Orcamentos.exe" --abrir
if errorlevel 1 pause
