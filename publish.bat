@echo off
REM Builds dist\ReelKeep.exe - one self-contained file you can share.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File build\publish.ps1
pause
