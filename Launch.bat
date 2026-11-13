@echo off
setlocal
cd /d "%~dp0"

:: Strip Windows SmartScreen 'Mark-of-the-Web' Zone.Identifier tag if downloaded from the web
powershell -NoProfile -Command "Unblock-File -Path .\WifiAnalyzerHotspot.exe -ErrorAction SilentlyContinue" >nul 2>&1

:: Launch WiFi Analyzer & Hotspot Pro
start "" "%~dp0WifiAnalyzerHotspot.exe" %*
exit /b 0
