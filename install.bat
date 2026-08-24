@echo off
REM Thin wrapper: delegates to the shared generic install in KSP-Shared\tools.
setlocal
cd /d "%~dp0"
set "MOD_NAME=BulldozerMod"
call "KSP-Shared\tools\install.bat"
exit /b %errorlevel%
