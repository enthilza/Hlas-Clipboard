@echo off
setlocal
if not exist "%~dp0dist\Hlas.exe" (
 call "%~dp0build.cmd" -NoPause
 if errorlevel 1 (pause & exit /b 1)
)
start "" "%~dp0dist\Hlas.exe"
