@echo off
setlocal
cd /d "%~dp0"

echo [1/3] Gracefully closing running instances if any...
powershell -NoProfile -Command "$p = Get-Process -Name WifiAnalyzerHotspot -ErrorAction SilentlyContinue; if ($p) { $p.CloseMainWindow() | Out-Null; $w = 0; while ($p -and -not $p.HasExited -and $w -lt 20) { Start-Sleep -Milliseconds 250; $w++ }; if ($p -and -not $p.HasExited) { Stop-Process -Name WifiAnalyzerHotspot -Force -ErrorAction SilentlyContinue } }" >nul 2>&1

echo [2/3] Building and publishing single-file win-x64 ReadyToRun executable to dist...
if exist ".\dist" rd /s /q ".\dist"
dotnet publish WifiAnalyzerHotspot\WifiAnalyzerHotspot.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o .\dist
if errorlevel 1 (
    echo [ERROR] Publish compilation failed!
    exit /b 1
)

echo [3/3] Staging executable to repository root...
if not exist ".\dist\WifiAnalyzerHotspot.exe" (
    echo [ERROR] Build output .\dist\WifiAnalyzerHotspot.exe not found!
    exit /b 1
)

copy /y ".\dist\WifiAnalyzerHotspot.exe" ".\WifiAnalyzerHotspot.exe" >nul
if errorlevel 1 (
    echo [ERROR] Failed to stage .\WifiAnalyzerHotspot.exe! File may be locked by a running process.
    exit /b 1
)

if exist ".\dist" rd /s /q ".\dist"
if exist ".\WifiAnalyzerHotspot.pdb" del /f /q ".\WifiAnalyzerHotspot.pdb"

echo.
echo ========================================================
echo   Publish complete: .\WifiAnalyzerHotspot.exe
powershell -NoProfile -Command "(Get-Item .\WifiAnalyzerHotspot.exe).VersionInfo | Select-Object FileVersion, ProductVersion"
echo ========================================================
exit /b 0
