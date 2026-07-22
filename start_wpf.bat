@echo off
setlocal
cd /d "%~dp0"

set "PROJECT=local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
set "CONFIG=Release"
set "TARGET=local-native\PhotoViewer.Wpf\bin\%CONFIG%\net8.0-windows\PhotoViewer.Wpf.exe"

if not exist "%PROJECT%" (
    echo [Aibos WPF] Project not found: %PROJECT%
    pause
    exit /b 1
)

if /I "%PHOTOVIEWER_WPF_DOTNET_RUN%"=="1" (
    echo [Aibos WPF] Launching via dotnet run for development...
    echo.
    dotnet run --project "%PROJECT%" -- %*
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

echo [Aibos WPF] Launching direct %CONFIG% executable...
echo.
"%TARGET%" %*

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
dotnet build "%PROJECT%" -c %CONFIG% --nologo
if errorlevel 1 exit /b %ERRORLEVEL%
powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\check-wpf-launch-target.ps1" -ProjectPath "%PROJECT%" -TargetPath "%TARGET%" -Record
exit /b %ERRORLEVEL%
