@echo off
setlocal
cd /d "%~dp0"

echo [1/3] Gracefully closing running instances if any...
powershell -NoProfile -Command "$p = Get-Process -Name WifiAnalyzerHotspot -ErrorAction SilentlyContinue; if ($p) { $p.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 1500; Stop-Process -Name WifiAnalyzerHotspot -Force -ErrorAction SilentlyContinue }" >nul 2>&1

echo [2/3] Building and publishing single-file win-x64 ReadyToRun executable to dist...
if exist ".\dist" rd /s /q ".\dist"
dotnet publish WifiAnalyzerHotspot\WifiAnalyzerHotspot.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o .\dist
if errorlevel 1 (
    echo [ERROR] Publish failed!
    exit /b 1
)

echo [3/3] Staging executable to repository root...
if exist ".\dist\WifiAnalyzerHotspot.exe" (
    copy /y ".\dist\WifiAnalyzerHotspot.exe" ".\WifiAnalyzerHotspot.exe" >nul
)
if exist ".\dist" rd /s /q ".\dist"
if exist ".\WifiAnalyzerHotspot.pdb" del /f /q ".\WifiAnalyzerHotspot.pdb"

echo.
echo ========================================================
echo   Publish complete: .\WifiAnalyzerHotspot.exe
powershell -NoProfile -Command "(Get-Item .\WifiAnalyzerHotspot.exe).VersionInfo | Select-Object FileVersion, ProductVersion"
echo ========================================================
exit /b 0
