using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace TrpgMaps
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 启动参数：
            //   （无）               默认全屏无边框，适合投屏当 DM 屏用
            //   --windowed / -w     以普通窗口启动（不置顶），方便调试 / 自动化测试
            //   --ui <模式> <路径>   摆好界面 → 渲染成 PNG → 退出（无人值守界面自检）
            //                        模式：all / base / sidebar / map / mapcollapsed /
            //                              grid35 / closeall / gridreopen / net / about /
            //                              rotate90 / gridcolor / draw / drawbrush / drawcone /
            //                              drawbrushloose / drawtangent
            //                        模式传 all 时 <路径> 是输出目录，一个进程截完所有画面
            //   --no-server         不起内置 Web 服务器（--ui 已默认带上）
            //   --selfcheck [目录]  无人值守自检：离屏渲染 + 打印几何，**完全不开窗口**
            //   --port <端口>       用指定端口起服务（避免自动化测试撞上正在运行的实例）
            //   --height <高>       窗口化时的窗口高度（默认 800）。
            //                       用来验证小屏（比如 1366x768 的老笔记本）上面板会不会
            //                       溢出、细滑块长什么样。
            //   --fix-firewall      只修防火墙：删掉阻止本程序的入站规则、加上入站放行，
            //                       打印结果后退出（不建窗口）。没管理员权限时会自己弹 UAC 重开。
            //   --quiet             配合 --fix-firewall：不弹结果对话框（给界面里的"一键放行"用）
            //   --no-prompts        不弹任何提示框（自动化测试用）。防火墙被挡住时只写日志，
            //                       交给界面上的"放行防火墙"按钮处理。
            //   --repair-registry   重写本程序的安装注册信息（卸载项 + 快捷方式）。
            //                       安装版的升级包里那个"修复注册信息.bat"就是调它。
            //                       需要管理员权限，会自己弹 UAC 重开。
            //   --check-update      只在命令行里查一次更新，结果写进 app.log 并用退出码表达：
            //                       0 = 有新版本，1 = 已是最新，2 = 检查失败。不弹任何窗口。
            //
            // 为什么自检模式默认不起服务器：起服务要监听局域网地址，会弹 Windows 防火墙
            // 的"是否允许通信"授权框；无人值守时没人点它，框就一直挂在屏幕上挡住流程。
            //
            // 关于防火墙：本程序要监听局域网端口，手机才能连进来。Windows 会为"新程序"
            // 弹一次授权框；本程序默认是全屏置顶的，那个框会被压在全屏界面底下 ——
            // 要是被用户随手关掉，Windows 会写下一条**永久的入站阻止规则**，而且**以后不再问**。
            // 症状就是"DM 端一切正常、二维码也对，手机就是打不开"。所以启动时会查一次，
            // 被挡住就直接告诉用户并提供一键修复。
            var windowed = false;
            string shotMode = null;
            string shotPath = null;
            var port = 0;
            var noServer = false;
            var selfCheck = false;
            var selfCheckDir = string.Empty;
            var windowHeight = 0;
            var fixFirewall = false;
            var quiet = false;
            var noPrompts = false;
            var repairRegistry = false;
            var checkUpdate = false;

            if (args != null)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    var arg = args[i];

                    if (string.Equals(arg, "--windowed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "-w", StringComparison.OrdinalIgnoreCase))
                    {
                        windowed = true;
                    }
                    else if (string.Equals(arg, "--ui", StringComparison.OrdinalIgnoreCase) && i + 2 < args.Length)
                    {
                        shotMode = args[i + 1];
                        shotPath = args[i + 2];
                        windowed = true;      // 截图必须在普通窗口下做
                        noServer = true;      // 纯界面自检不需要服务器
                        i += 2;
                    }
                    else if (string.Equals(arg, "--selfcheck", StringComparison.OrdinalIgnoreCase))
                    {
                        selfCheck = true;
                        // 可选：后面跟一个目录参数（不是以 - 开头就当目录）
                        if (i + 1 < args.Length && args[i + 1].Length > 0 && args[i + 1][0] != '-')
                        {
                            selfCheckDir = args[i + 1];
                            i += 1;
                        }
                    }
                    else if (string.Equals(arg, "--no-server", StringComparison.OrdinalIgnoreCase))
                    {
                        noServer = true;
                    }
                    else if (string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        int.TryParse(args[i + 1], out port);
                        i += 1;
                    }
                    else if (string.Equals(arg, "--height", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        int.TryParse(args[i + 1], out windowHeight);
                        i += 1;
                    }
                    else if (string.Equals(arg, "--fix-firewall", StringComparison.OrdinalIgnoreCase))
                    {
                        fixFirewall = true;
                    }
                    else if (string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase))
                    {
                        quiet = true;
                    }
                    else if (string.Equals(arg, "--no-prompts", StringComparison.OrdinalIgnoreCase))
                    {
                        noPrompts = true;
                    }
                    else if (string.Equals(arg, "--repair-registry", StringComparison.OrdinalIgnoreCase))
                    {
                        repairRegistry = true;
                    }
                    else if (string.Equals(arg, "--check-update", StringComparison.OrdinalIgnoreCase))
                    {
                        checkUpdate = true;
                    }
                }
            }

            // 只刷注册信息：不建窗口、不起服务，改完就退出。
            if (repairRegistry)
            {
                Environment.ExitCode = RunRegistryRepair(quiet);
                return;
            }

            // 命令行查更新：不建窗口。用退出码表达结果，方便脚本 / 排查。
            if (checkUpdate)
            {
                Environment.ExitCode = RunUpdateCheck();
                return;
            }

            // 只修防火墙：不建窗口、不起服务，改完就退出。
            if (fixFirewall)
            {
                Environment.ExitCode = RunFirewallRepair(quiet);
                return;
            }

            if (port > 0 && ServerSettings.IsValidPort(port)) ServerSettings.PortOverride = port;

            // 无人值守自检：离屏渲染 + 打印几何，不创建窗口、不走消息循环，
            // 因此不可能弹出任何"是否允许运行/是否允许通信"的对话框。
            if (selfCheck)
            {
                Environment.ExitCode = SelfCheck.Run(selfCheckDir);
                return;
            }

            // 任何未处理异常都记进 app.log，方便在老机器上排错
            Application.ThreadException += delegate (object sender, ThreadExceptionEventArgs e)
            {
                AppLog.Write("UI 未处理异常", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                AppLog.Write("进程未处理异常", e.ExceptionObject as Exception);
            };

            try
            {
                // 高度必须在构造期传进去：窗口 Bounds 是构造函数里定的，
                // new 完再赋值就晚了（见 MainForm 的双参构造）。
                var form = new MainForm(windowed, windowHeight);
                form.Headless = shotMode != null;
                form.NoServer = noServer;
                form.NoPrompts = noPrompts;
                if (shotMode != null) form.ScheduleUiShot(shotMode, shotPath);
                Application.Run(form);
            }
            catch (Exception ex)
            {
                AppLog.Write("启动失败", ex);
                MessageBox.Show("程序启动失败：\r\n" + ex.Message, "TrpgMaps",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// --fix-firewall 的实现：把防火墙里针对本程序的入站阻止规则删掉，加上入站放行。
        /// 改防火墙必须要管理员权限，所以没有权限时会弹一次 UAC、用管理员身份把自己重开。
        /// 返回进程退出码：0 = 复查确认已经放行。
        /// </summary>
        private static int RunFirewallRepair(bool quiet)
        {
            if (!FirewallCheck.IsElevated)
            {
                try
                {
                    var psi = new ProcessStartInfo();
                    psi.FileName = Application.ExecutablePath;
                    psi.Arguments = quiet ? "--fix-firewall --quiet" : "--fix-firewall";
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";

                    using (var child = Process.Start(psi))
                    {
                        child.WaitForExit();
                        return child.ExitCode;
                    }
                }
                catch (Win32Exception)
                {
                    // 用户在 UAC 弹窗上点了"否"
                    AppLog.Write("防火墙修复：用户取消了管理员授权，规则未改动。");
                    if (!quiet) ShowFirewallResult(false, "已取消管理员授权，防火墙规则没有改动。");
                    return 1;
                }
                catch (Exception ex)
                {
                    AppLog.Write("防火墙修复：无法启动管理员进程", ex);
                    if (!quiet) ShowFirewallResult(false, "无法启动管理员进程：" + ex.Message);
                    return 1;
                }
            }

            string log;
            var ok = FirewallCheck.Repair(out log);
            AppLog.Write("=== 防火墙修复 ===" + Environment.NewLine + log);

            if (!quiet)
            {
                ShowFirewallResult(ok, ok
                    ? "防火墙已放行，手机 / 平板现在可以打开玩家页面了。"
                    : "修复没有成功，详情见程序目录下的 app.log。");
            }

            return ok ? 0 : 1;
        }

        private static void ShowFirewallResult(bool ok, string message)
        {
            MessageBox.Show(message, "TrpgMaps - 防火墙", MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        /// <summary>
        /// --repair-registry：把安装注册信息按"当前这份 exe + 当前版本号"重写一遍，
        /// 并把注册表里记着的快捷方式勾选情况重新落实到位。
        ///
        /// 谁在用：安装版的升级包（里面那个"修复注册信息.bat"）。升级只换文件、
        /// 目录不变，所以快捷方式本来就还有效；这一步真正解决的是两件事 ——
        /// **版本号要跟着更新**（不然控制面板里显示的永远是最初装的版本），
        /// 以及**用户手滑删掉的快捷方式能自己长回来**。
        /// </summary>
        private static int RunRegistryRepair(bool quiet)
        {
            if (!AppRegistry.IsElevated)
            {
                try
                {
                    var psi = new ProcessStartInfo();
                    psi.FileName = Application.ExecutablePath;
                    psi.Arguments = quiet ? "--repair-registry --quiet" : "--repair-registry";
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";

                    using (var child = Process.Start(psi))
                    {
                        child.WaitForExit();
                        return child.ExitCode;
                    }
                }
                catch (Win32Exception)
                {
                    AppLog.Write("注册信息修复：用户取消了管理员授权。");
                    if (!quiet)
                    {
                        MessageBox.Show("已取消管理员授权，注册信息没有改动。", "TrpgMaps",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    return 1;
                }
                catch (Exception ex)
                {
                    AppLog.Write("注册信息修复：无法启动管理员进程", ex);
                    if (!quiet) MessageBox.Show("无法启动管理员进程：" + ex.Message, "TrpgMaps");
                    return 1;
                }
            }

            try
            {
                AppRegistry.Repair(AppEnv.Root, AppInfo.Version);
                AppLog.Write("注册信息已刷新：" + AppEnv.Root + "  版本 " + AppInfo.Version);

                // 升级包里若带了一个新版本的卸载程序副本，顺手把安装目录里的那份换掉 ——
                // 卸载程序本身就是"安装包改个名的副本"，版本不换新的，
                // 卸载时显示的还是旧版本号。
                var self = Application.ExecutablePath;
                if (string.Equals(Path.GetFileName(self), AppRegistry.UninstallerName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Write("当前就是安装目录里的卸载程序副本，无需自我更新。");
                }

                if (!quiet)
                {
                    MessageBox.Show("注册信息已刷新。\r\n\r\n安装位置：" + AppEnv.Root +
                                    "\r\n版本：" + AppInfo.VersionText, "TrpgMaps",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return 0;
            }
            catch (Exception ex)
            {
                AppLog.Write("注册信息修复失败", ex);
                if (!quiet) MessageBox.Show("修复失败：" + ex.Message, "TrpgMaps");
                return 1;
            }
        }

        /// <summary>
        /// --check-update：命令行查一次更新。
        /// 退出码：0 = 有新版本，1 = 已是最新（或该版本已被"不再提示"），2 = 检查失败。
        /// 结果同时写进 app.log —— 无人值守时全靠它。
        /// </summary>
        private static int RunUpdateCheck()
        {
            var result = UpdateCheck.Query();

            if (!result.Ok)
            {
                AppLog.Write("=== 检查更新 === 失败：" + result.Error);
                return 2;
            }

            AppLog.Write("=== 检查更新 ===" + Environment.NewLine +
                         "检查源   = " + result.Source + Environment.NewLine +
                         "本机版本 = " + AppInfo.VersionText + Environment.NewLine +
                         "仓库版本 = v" + result.Manifest.Version + Environment.NewLine +
                         "有新版本 = " + result.HasUpdate + Environment.NewLine +
                         "说明     = " + result.Manifest.Notes);

            return result.HasUpdate ? 0 : 1;
        }
    }
}
