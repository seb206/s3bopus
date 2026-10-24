@echo off
rem Compile la version Linux (Debian/Ubuntu, x64) et fabrique le paquet .deb
cd /d "%~dp0"
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish || pause
python build-deb.py 0.0.2 || pause
