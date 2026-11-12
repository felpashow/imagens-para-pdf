@echo off
start "" /wait "%~dp0FotosParaPdf.exe" --uninstall
exit /b %errorlevel%
