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
                }
            }

            // 自检放在提权检查之前：它只往临时目录写文件，本来就不需要管理员。
            if (!string.IsNullOrEmpty(selfTestDir))
            {
                return SelfTest(selfTestDir);
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
