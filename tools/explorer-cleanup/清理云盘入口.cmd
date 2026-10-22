@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0cleanup-explorer.ps1" -Gui
if errorlevel 1 pause
endlocal
