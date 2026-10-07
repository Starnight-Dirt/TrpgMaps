@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

set "EXE=bin\Release\TrpgMaps.exe"
if not exist "%EXE%" (
    echo 尚未编译，请先运行「编译并运行.bat」。
    pause
    exit /b 1
)

start "" "%EXE%"
exit /b 0
