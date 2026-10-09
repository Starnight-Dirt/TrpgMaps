using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace TrpgMaps
{
    /// <summary>
    /// 安装程序入口。
    ///
    /// 一个 exe 干两件事：
    ///   `TrpgMapsSetup.exe`                   → 安装向导
    ///   `TrpgMapsSetup.exe /uninstall`        → 卸载向导（安装时会被拷成
    ///                                            `TrpgMapsUninstall.exe` 放进安装目录，
    ///                                            控制面板"卸载"按钮就是调它）
    /// 加 `/quiet` 则不显示界面，直接做（控制面板的 QuietUninstallString 用得上）。
    /// 另有 `/selftest <目录>`：只把内嵌负载解出来验证一遍，不碰系统 ——
    /// 打包脚本用它确认"装出来的东西是完整的"，而且**不需要管理员权限**。
    ///
    /// `/uipreview <目录>`：**只为截图**。把向导的每一种视觉状态（位置页、
    /// 选项页未选中 / 悬浮 / 选中、卸载页）各渲染一张 PNG 然后退出。
    /// 它跳过提权检查、不碰注册表、不起任何安装动作，所以在无人值守下也能跑
    /// —— 正常启动路径带 requireAdministrator，一跑就弹 UAC，没人点就卡死。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var uninstall = false;
            var quiet = false;
            var selfTestDir = string.Empty;
            var previewDir = string.Empty;

            if (args != null)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    if (string.Equals(arg, "/uninstall", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase))
                    {
                        uninstall = true;
                    }
                    else if (string.Equals(arg, "/quiet", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(arg, "/S", StringComparison.OrdinalIgnoreCase))
                    {
                        quiet = true;
                    }
                    else if (string.Equals(arg, "/selftest", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        selfTestDir = args[i + 1];
                        i++;
                    }
                    else if (string.Equals(arg, "/uipreview", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        previewDir = args[i + 1];
                        i++;
                    }
                }
            }

            // 自检放在提权检查之前：它只往临时目录写文件，本来就不需要管理员。
            if (!string.IsNullOrEmpty(selfTestDir))
            {
                return SelfTest(selfTestDir);
            }

            // 截图预览同理：不碰注册表、不起安装，跳过提权检查。
            if (!string.IsNullOrEmpty(previewDir))
            {
                return UiPreview(previewDir);
            }

            // 提权是 app.manifest 里声明的，正常不会走到这里；留一道保险，
            // 免得有人改了 manifest 之后得到一个"半路报拒绝访问"的诡异安装。
            if (!AppRegistry.IsElevated)
            {
                if (!quiet)
                {
                    MessageBox.Show("安装程序需要管理员权限，请右键选择「以管理员身份运行」。",
                        "TrpgMaps", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return 5;
            }

            if (quiet)
            {
                try
                {
                    return uninstall ? SetupForm.RunUninstallQuiet() : SetupForm.RunInstallQuiet();
                }
                catch (Exception ex)
                {
                    AppLog.Write("静默安装/卸载失败", ex);
                    return 1;
                }
            }

            Application.Run(new SetupForm(uninstall));
            return 0;
        }

        /// <summary>
        /// `/uipreview &lt;目录&gt;`：把向导的每种视觉状态渲染成 PNG。
        ///
        /// 存在的理由：安装程序的界面自检没法靠 HTTP 接口或断言来验（本来就没有接口），
        /// 只能看图；而正常启动要过 UAC。所以留这一个**无副作用**的入口：
        /// 关掉“开始安装”按钮的实际动作（不点它就不会有任何安装行为），
        /// 用 Win32 的 PrintWindow 把窗口抓成位图存盘。
        ///
        /// 退出码 0 = 全部图片写出。
        /// </summary>
        private static int UiPreview(string outDir)
        {
            try
            {
                Directory.CreateDirectory(outDir);
                AppLog.Write("=== 安装程序界面预览 === " + outDir);

                // 状态清单：页码 / 悬浮在第几行（-1 = 无）/ 选中的行（-1 = 全不选）
                // 覆盖用户示意图里的全部四张：整页、未选择、鼠标扫过、选中。
                var shots = new[]
                {
                    new object[] { "01-location",        0, -1, -1 },
                    new object[] { "02-options-none",    1, -1, -1 },
                    new object[] { "03-options-hover",   1,  0, -1 },
                    new object[] { "04-options-checked", 1, -1,  0 },
                    new object[] { "05-options-mixed",   1,  2,  0 }
                };

                var layoutFailures = 0;

                // --- 向导流程自检（先做，用的是全新的第 0 页窗口）---
                using (var flowForm = new SetupForm(false))
                {
                    AppLog.Write("  --- 向导流程自检 ---");
                    var flow = flowForm.PreviewWizardFlowReport();
                    AppLog.Write(flow);
                    if (flow.IndexOf("FAILED", StringComparison.Ordinal) >= 0) layoutFailures++;
                }

                foreach (var shot in shots)
                {
                    var name = (string)shot[0];
                    var page = (int)shot[1];
                    var hover = (int)shot[2];
                    var checkedRow = (int)shot[3];

                    using (var form = new SetupForm(false))
                    {
                        form.PreviewSetup(page, hover, checkedRow);
                        Application.DoEvents();

                        var path = Path.Combine(outDir, name + ".png");
                        form.PreviewCapture(path);
                        AppLog.Write("  写出 " + path);
                        AppLog.Write("  --- 布局自检 " + name + " ---");
                        var report = form.PreviewLayoutReport();
                        AppLog.Write(report);
                        if (report.IndexOf("FAILED", StringComparison.Ordinal) >= 0) layoutFailures++;
                    }
                }

                using (var form = new SetupForm(true))
                {
                    Application.DoEvents();
                    var path = Path.Combine(outDir, "06-uninstall.png");
                    form.PreviewCapture(path);
                    AppLog.Write("  写出 " + path);
                    AppLog.Write("  --- 布局自检 06-uninstall ---");
                    var report = form.PreviewLayoutReport();
                    AppLog.Write(report);
                    if (report.IndexOf("FAILED", StringComparison.Ordinal) >= 0) layoutFailures++;
                }

                AppLog.Write("  失败总数   = " + layoutFailures);
                AppLog.Write("  结果       = " + (layoutFailures == 0 ? "OK" : "FAILED"));
                return layoutFailures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                AppLog.Write("界面预览失败", ex);
                return 1;
            }
        }

        /// <summary>
        /// `/selftest &lt;目录&gt;`：把内嵌的程序负载解到指定目录并核对文件数与总字节数。
        /// 退出码 0 = 负载完整且能被我们自己的 ZIP 读取器解开。
        /// 报告写在 <see cref="AppLog"/> 指向的 app.log 里。
        /// </summary>
        private static int SelfTest(string targetDir)
        {
            try
            {
                var payload = SetupEngine.LoadPayload();
                AppLog.Write("=== 安装包自检 === 负载字节=" + payload.Length);

                if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
                Directory.CreateDirectory(targetDir);

                var entries = MiniZip.ReadEntries(payload);
                var written = MiniZip.ExtractTo(payload, targetDir, true);

                long total = 0;
                foreach (var file in Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories))
                {
                    total += new FileInfo(file).Length;
                }

                AppLog.Write("  条目数     = " + entries.Count);
                AppLog.Write("  写出文件数 = " + written.Count);
                AppLog.Write("  写出总字节 = " + total);

                var ok = written.Count > 0 && written.Count == entries.Count && total > 0;
                AppLog.Write(ok ? "  结果       = OK" : "  结果       = FAILED");
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                AppLog.Write("安装包自检失败", ex);
                return 1;
            }
        }
    }
}
