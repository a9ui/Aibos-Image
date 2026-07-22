@echo off
setlocal
cd /d "%~dp0"

set "AIBOS_TARGET="
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\select-launch-target.ps1"`) do set "AIBOS_TARGET=%%I"

if /I "%AIBOS_TARGET%"=="browser" (
    call ".\start_viewer.bat" %*
    exit /b %ERRORLEVEL%
)

if /I "%AIBOS_TARGET%"=="wpf" (
    call ".\start_wpf.bat" %*
    exit /b %ERRORLEVEL%
)

if not "%AIBOS_TARGET%"=="cancel" (
    echo [Aibos Image] The launch target dialog could not be opened.
    pause
    exit /b 1
)

exit /b 0
