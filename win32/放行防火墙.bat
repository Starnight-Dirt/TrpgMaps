@echo off
chcp 65001 >nul
setlocal

:: ========== TrpgMaps - 放行 Windows 防火墙 ==========
:: 手机 / 平板扫了码却打不开玩家页面？多半是这里。
::
:: 背景：本程序要监听局域网端口、手机才能连进来。Windows 会为"新程序"弹一次
:: "是否允许通信"的授权框，而本程序默认是全屏置顶的 —— 那个框会被压在全屏界面
:: 底下。万一被随手关掉，Windows 就会写下一条"永久阻止"规则，而且**以后再也不问**。
:: 表现就是：DM 端一切正常、二维码和地址都对，手机就是打不开。
::
:: 双击本文件即可修复：会删掉针对本程序的入站阻止规则、加上入站放行。
:: 过程中会弹一次管理员确认，点"是"即可。

cd /d "%~dp0"

:: 开发目录里 exe 在 bin\Release\；发布出来的安装目录 / 便携目录里 exe 就在本文件旁边。
:: 两种布局都要能找到，这样同一个文件既能开发时用，也能随包发出去。
set "EXE=%~dp0TrpgMaps.exe"
if not exist "%EXE%" set "EXE=%~dp0bin\Release\TrpgMaps.exe"
if not exist "%EXE%" (
    echo 未找到 TrpgMaps.exe（既不在本目录，也不在 bin\Release\）
    echo 请先双击"编译并运行.bat"编译一次，再运行本文件。
    echo.
    pause
    exit /b 1
)

echo ============================================
echo   TrpgMaps - 放行防火墙（局域网访问）
echo ============================================
echo.
echo 即将请求管理员权限，请在弹窗中点"是"。
echo.

"%EXE%" --fix-firewall
set "RC=%errorlevel%"

echo.
echo 详细过程记录在程序目录下的 app.log。
if "%RC%"=="0" (
    echo 结果：已放行。手机上重新打开玩家地址即可。
) else (
    echo 结果：未成功（代码 %RC%），请看 app.log。
)
echo.
pause
exit /b %RC%
