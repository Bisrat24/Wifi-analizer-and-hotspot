@echo off
setlocal
cd /d "%~dp0"

echo [1/3] Stopping running instances if any...
powershell -NoProfile -Command "Stop-Process -Name WifiAnalyzerHotspot -Force -ErrorAction SilentlyContinue" >nul 2>&1

echo [2/3] Building and publishing single-file win-x64 ReadyToRun executable...
dotnet publish WifiAnalyzerHotspot\WifiAnalyzerHotspot.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o .
if errorlevel 1 (
    echo [ERROR] Publish failed!
    exit /b 1
)

echo [3/3] Cleaning up temporary publish files (.pdb)...
if exist ".\WifiAnalyzerHotspot.pdb" del /f /q ".\WifiAnalyzerHotspot.pdb"

echo.
echo ========================================================
echo   Publish complete: .\WifiAnalyzerHotspot.exe
powershell -NoProfile -Command "(Get-Item .\WifiAnalyzerHotspot.exe).VersionInfo | Select-Object FileVersion, ProductVersion"
echo ========================================================
exit /b 0
