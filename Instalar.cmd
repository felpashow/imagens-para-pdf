@echo off
start "" /wait "%~dp0FotosParaPdf.exe" --install
exit /b %errorlevel%
