@echo off
setlocal

echo =====================================
echo Removing existing Mod folder
echo =====================================

if exist "%KSPDIR%\GameData\BulldozerMod" rmdir /s /q "%KSPDIR%\GameData\BulldozerMod"

echo.
echo =====================================
echo Unzipping Mod
echo =====================================

powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -Path 'Release\BulldozerMod.zip' -DestinationPath '%KSPDIR%\GameData\BulldozerMod' -Force"
if errorlevel 1 (
    echo ERROR: Failed to unzip the Mod
    exit /b 1
)

echo.
echo Mod installed
echo.
echo Run at: %date% %time%
