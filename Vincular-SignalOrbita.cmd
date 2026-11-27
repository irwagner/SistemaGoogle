@echo off
setlocal
chcp 65001 >nul
powershell.exe -NoLogo -NoProfile -File "%~dp0scripts\Signal-Orbita.ps1" -Vincular
pause
