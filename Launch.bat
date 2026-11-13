@echo off
setlocal
cd /d "%~dp0"

:: Strip Windows SmartScreen 'Mark-of-the-Web' Zone.Identifier stream only if present
if exist "%~dp0WifiAnalyzerHotspot.exe:Zone.Identifier" (
    powershell -NoProfile -Command "Unblock-File -Path '%~dp0WifiAnalyzerHotspot.exe' -ErrorAction SilentlyContinue" >nul 2>&1
)

:: Launch WiFi Analyzer & Hotspot Pro
start "" "%~dp0WifiAnalyzerHotspot.exe" %*
exit /b 0
