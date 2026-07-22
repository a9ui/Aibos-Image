@echo off
setlocal
cd /d "%~dp0"

echo [Aibos Browser] Checking the exact checkout and toolchain...

where node >nul 2>&1
if errorlevel 1 (
    echo [Aibos Browser] Node.js 20.9 or later is required.
    pause
    exit /b 1
)

set "NODE_MAJOR="
set "NODE_MINOR="
for /f "tokens=1,2 delims=." %%A in ('node -p "process.versions.node"') do (
    set "NODE_MAJOR=%%A"
    set "NODE_MINOR=%%B"
)
if not defined NODE_MAJOR (
    echo [Aibos Browser] Could not determine the Node.js version.
    pause
    exit /b 1
)
if %NODE_MAJOR% LSS 20 goto node_version_error
if %NODE_MAJOR% EQU 20 if %NODE_MINOR% LSS 9 goto node_version_error
goto node_version_ok

:node_version_error
    echo [Aibos Browser] Node.js 20.9 or later is required. Current version:
    node --version
    pause
    exit /b 1

:node_version_ok

where corepack >nul 2>&1
if errorlevel 1 (
    echo [Aibos Browser] Corepack is required to use the pinned pnpm version.
    pause
    exit /b 1
)

echo [Aibos Browser] Restoring the pinned dependency graph...
call corepack pnpm install --frozen-lockfile --prefer-offline
if errorlevel 1 (
    echo [Aibos Browser] Dependency restore failed. No server was started.
    pause
    exit /b 1
)

echo.
echo [Aibos Browser] Launching the production build from this checkout...
echo [Aibos Browser] The launcher rebuilds when the current source is newer.
echo [Aibos Browser] The browser opens automatically when the loopback server is ready.
echo.

node scripts\prod_launcher.js %*
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" pause
exit /b %EXIT_CODE%
