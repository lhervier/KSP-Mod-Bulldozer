@echo off
setlocal

echo =========
echo Building
echo =========

echo Removing Release folder
if exist Release rmdir /s /q Release

echo Creating Release folders
mkdir Release\BulldozerMod

echo Building Mod DLL
dotnet build BulldozerMod.sln
if errorlevel 1 (
    echo ERROR: Failed to build the Mod DLL
    exit /b 1
)

echo Copying Mod dll file
copy /y "Output\bin\BulldozerMod.dll" "Release\BulldozerMod"
if errorlevel 1 (
    echo ERROR: Failed to copy the Mod DLL
    exit /b 1
)

echo Copying icon file
copy /y "GameData\BulldozerMod\*.png" "Release\BulldozerMod"
if errorlevel 1 (
    echo ERROR: Failed to copy the icon file
    exit /b 1
)

echo Zipping Mod
powershell -Command "Compress-Archive -Path 'Release\BulldozerMod\*' -DestinationPath 'Release\BulldozerMod.zip' -Force"
if errorlevel 1 (
    echo ERROR: Failed to zip the Mod
    exit /b 1
)

echo Removing Mod folder
rmdir /s /q Release\BulldozerMod

echo Build Complete
echo.
echo Run at: %date% %time%
