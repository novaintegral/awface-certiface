@echo off
rem Atalho para scripts\publish.ps1: build e push das imagens no Docker Hub.
rem Exemplos: publish ^| publish -Latest ^| publish -DryRun ^| publish -Only api
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\publish.ps1" %*
exit /b %ERRORLEVEL%
