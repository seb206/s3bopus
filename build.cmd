@echo off
cd /d "%~dp0src"
dotnet build -c Release || pause
copy /y bin\Release\net48\S3bopus.exe* "%~dp0" >nul
echo S3bopus.exe est pret.
