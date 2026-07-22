@echo off
setlocal
cd /d "%~dp0"

echo [Photoviewer] Starting environment check...

:: Check if pnpm is available
where pnpm >nul 2>&1
set HAS_PNPM=%errorlevel%

:: If node_modules was installed by pnpm but pnpm is not available, reinstall with npm
set NEED_REINSTALL=0
if exist "node_modules\.pnpm" if %HAS_PNPM% neq 0 set NEED_REINSTALL=1

if %NEED_REINSTALL%==1 (
    echo [Photoviewer] pnpm node_modules detected but pnpm is not available on this PC.
    echo [Photoviewer] Reinstalling with npm - first time only, please wait...
    rmdir /s /q node_modules
)

:: Install if node_modules does not exist
if not exist "node_modules" (
    echo [Photoviewer] Installing dependencies...
    if %HAS_PNPM%==0 (
        call pnpm install
        call pnpm approve-builds
        call pnpm install
    ) else (
        call npm install
    )
    if errorlevel 1 (
        echo [Photoviewer] Install failed.
        pause
        exit /b 1
    )
)

echo.
echo [Photoviewer] Launching production server...
echo [Photoviewer] The first launch will build the app (~1 min). After that, startup is instant.
echo [Photoviewer] Browser will open automatically when ready.
echo.

node scripts\prod_launcher.js

pause
