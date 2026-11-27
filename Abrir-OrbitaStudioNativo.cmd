@echo off
setlocal
powershell.exe -NoLogo -NoProfile -File "%~dp0scripts\Abrir-OrbitaHermesNativo.ps1" %*
if errorlevel 1 pause
