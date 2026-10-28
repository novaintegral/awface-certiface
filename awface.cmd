@echo off
rem Atalho para scripts\awface.ps1 (Prompt de Comando ou duplo clique).
rem Uso: awface start ^| stop ^| restart ^| status ^| logs [servico] ^| check
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\awface.ps1" %*
set "AWFACE_EXIT=%ERRORLEVEL%"
if "%~1"=="" pause
exit /b %AWFACE_EXIT%
