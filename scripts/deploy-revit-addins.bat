@echo off
rem Launcher for Deploy-RevitAddins.ps1 (for non-PowerShell shells like nu).
rem All arguments pass through, e.g.:
rem   deploy-revit-addins.bat -RevitYears 2025
rem   deploy-revit-addins.bat -Remove
where pwsh >nul 2>nul
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-RevitAddins.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-RevitAddins.ps1" %*
)
exit /b %errorlevel%
