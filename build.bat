@echo off
REM Debug build you can run from Visual Studio / Rider or bin\.
cd /d "%~dp0"
dotnet build ReelKeep.sln -c Release
