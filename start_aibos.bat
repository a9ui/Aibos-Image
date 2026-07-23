@echo off
setlocal
cd /d "%~dp0"

call ".\start_wpf.bat" %*
exit /b %ERRORLEVEL%
