@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1"
if errorlevel 1 echo Release failed.
pause
