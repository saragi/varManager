@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1" -Local
if errorlevel 1 echo Build failed.
pause
