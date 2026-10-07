@echo off
chcp 65001 >nul
setlocal

:: ========== TrpgMaps - Win7 / 32 位兼容版：编译并启动 ==========
:: 双击本文件即可：用 VS2022 的 MSBuild 编译，然后运行程序。
:: 编译产物是 32 位 exe，32 位 / 64 位 Windows 都能跑，Win7 SP1 及以上均可。

cd /d "%~dp0"

where powershell >nul 2>&1
if %errorlevel% neq 0 (
    echo 未找到 PowerShell，无法继续。
    pause
    exit /b 1
)

echo ============================================
echo   TrpgMaps Win7 / 32 位版 - 编译
echo ============================================
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\build.ps1"
if %errorlevel% neq 0 (
    echo.
    echo 编译失败，请查看 build_logs 目录下的日志。
    pause
    exit /b %errorlevel%
)

set "EXE=bin\Release\TrpgMaps.exe"
if not exist "%EXE%" (
    echo 未找到可执行文件：%EXE%
    pause
    exit /b 1
)

echo.
echo ============================================
echo   启动 TrpgMaps
echo ============================================
start "" "%EXE%"
echo 已启动。玩家用手机扫描窗口里的二维码即可加入。
echo 本窗口可以关闭。
timeout /t 3 /nobreak >nul
exit /b 0
