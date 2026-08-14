@echo off
setlocal
cd /d "%~dp0"

set "PROJECT=local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
set "CONFIG=Release"
set "TARGET=local-native\PhotoViewer.Wpf\bin\%CONFIG%\net10.0-windows\PhotoViewer.Wpf.exe"
set "TARGET_DLL=local-native\PhotoViewer.Wpf\bin\%CONFIG%\net10.0-windows\PhotoViewer.Wpf.dll"
set "DOTNET_CMD=dotnet"
set "LOCAL_DOTNET10=%LOCALAPPDATA%\Microsoft\dotnet10\dotnet.exe"

if exist "%LOCAL_DOTNET10%" (
    set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet10"
    set "PATH=%LOCALAPPDATA%\Microsoft\dotnet10;%PATH%"
    set "DOTNET_CMD=%LOCAL_DOTNET10%"
)

if not exist "%PROJECT%" (
    echo [Aibos WPF] Project not found: %PROJECT%
    pause
    exit /b 1
)

if not defined AIBOS_COMPANION_ROOT if defined AIBOS_H25_COMPANION_ROOT set "AIBOS_COMPANION_ROOT=%AIBOS_H25_COMPANION_ROOT%"
if not defined AIBOS_COMPANION_ROOT call :resolve_companion_root

if /I "%PHOTOVIEWER_WPF_DOTNET_RUN%"=="1" (
    echo [Aibos WPF] Launching via dotnet run for development...
    echo.
    "%DOTNET_CMD%" run --project "%PROJECT%" -- %*
    goto capture_exit_code
)

if /I "%PHOTOVIEWER_WPF_REBUILD%"=="1" (
    call :build_target
    if errorlevel 1 exit /b 1
)

if not exist "%TARGET%" (
    call :build_target
    if errorlevel 1 exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\check-wpf-launch-target.ps1" -ProjectPath "%PROJECT%" -TargetPath "%TARGET%"
set "CHECK_CODE=%ERRORLEVEL%"
if "%CHECK_CODE%"=="10" (
    call :build_target
    if errorlevel 1 exit /b 1
) else if not "%CHECK_CODE%"=="0" (
    echo [Aibos WPF] Could not verify whether the Release executable is current.
    exit /b %CHECK_CODE%
)

echo [Aibos WPF] Launching with %DOTNET_CMD%...
echo.
if exist "%LOCAL_DOTNET10%" (
    "%DOTNET_CMD%" "%TARGET_DLL%" %*
) else (
    "%TARGET%" %*
)

:capture_exit_code
set "EXIT_CODE=%ERRORLEVEL%"

:exit_with_code
if "%EXIT_CODE%"=="0" exit /b 0

echo.
echo [Aibos WPF] Exited with code %EXIT_CODE%.
pause
exit /b %EXIT_CODE%

:build_target
echo [Aibos WPF] Building direct %CONFIG% launch target...
"%DOTNET_CMD%" build "%PROJECT%" -c %CONFIG% --nologo
if errorlevel 1 exit /b %ERRORLEVEL%
powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\check-wpf-launch-target.ps1" -ProjectPath "%PROJECT%" -TargetPath "%TARGET%" -Record
exit /b %ERRORLEVEL%

:resolve_companion_root
for /f "tokens=1,*" %%I in ('git -C "%~dp0" worktree list --porcelain 2^>nul') do (
    if /I "%%I"=="worktree" call :try_companion_root "%%J"
)
if defined AIBOS_COMPANION_ROOT exit /b 0
for /f "usebackq delims=" %%I in (`git -C "%~dp0" rev-parse --path-format^=absolute --git-common-dir 2^>nul`) do (
    for %%J in ("%%~fI\..") do (
        call :try_companion_root "%%~fJ"
    )
)
exit /b 0

:try_companion_root
if exist "%~1\package.json" if exist "%~1\project.toml" if exist "%~1\scripts\enhancement_companion.js" set "AIBOS_COMPANION_ROOT=%~f1"
exit /b 0
