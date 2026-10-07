using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TrpgMaps
{
    /// <summary>
    /// 升级器 —— 升级包里的那个小 exe。
    ///
    /// **为什么必须有这么一个独立程序**：正在运行的 `TrpgMaps.exe` 换不掉自己
    /// （Windows 会锁住映像文件）。所以主程序的流程是：
    ///   下载升级包 → 解到临时目录 → 把控制权交给本程序 → 主程序自己退出，
    /// 由本程序在进程外完成覆盖/删除/注册表，最后把主程序重新拉起来。
    ///
    /// 另一个好处是 **彻底绕开批处理的中文路径编码问题**：
    /// cmd.exe 按当前代码页解释 .bat 的字节，安装目录里一旦有中文
    /// （`C:\Program Files (x86)\TrpgMaps` 没问题，但用户自定义目录就不好说了），
    /// 批处理里的字面量路径就可能变成问号。这里全程走托管 API + CreateProcessW，
    /// 路径是 Unicode 的，不经过代码页。
    ///
    /// 用法（都由主程序或升级包里的 .bat 调用，不需要人工敲）：
    ///   TrpgMapsUpdater.exe --apply         &lt;升级包目录&gt; &lt;安装目录&gt; [--installed]
    ///   TrpgMapsUpdater.exe --delete-only   &lt;升级包目录&gt; &lt;安装目录&gt;
    ///   TrpgMapsUpdater.exe --register-only &lt;升级包目录&gt; &lt;安装目录&gt;
    ///   TrpgMapsUpdater.exe --test-zip      &lt;zip 文件&gt;
    /// 安装目录传空串（或用 `-`）时，自动取注册表里登记的安装位置 ——
    /// 手工跑"修复注册信息.bat"就不用去翻安装路径了。
    ///
    /// --apply 会自己判断要不要提权：只有"修注册表"那一步需要管理员权限，
    /// 所以它是**先以普通权限覆盖文件、再把自己用 runas 拉起来做注册表**。
    /// 这样程序本体不会以管理员身份运行（那会让拖放到窗口上的文件都变"管理员所有"）。
    /// </summary>
    internal static class Program
    {
        private const string AppExeName = AppInfo.Name + ".exe";
        private const int WaitForExitMs = 60000;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args == null || args.Length < 2)
                {
                    Fail("参数不对。\n\n用法：\n  TrpgMapsUpdater.exe --apply <升级包目录> <安装目录> [--installed]\n" +
                         "  TrpgMapsUpdater.exe --test-zip <zip 文件>");
                    return 2;
                }

                var mode = args[0];

                // 独立的自检模式：只验证"我们自己的 ZIP 读取器能解开这个包"。
                // 打包脚本会用它验一遍生成出来的升级包，免得发出去的包自己都解不开。
                if (string.Equals(mode, "--test-zip", StringComparison.OrdinalIgnoreCase))
                {
                    return TestZip(args[1]);
                }

                if (args.Length < 3)
                {
                    Fail("参数不对：需要 <升级包目录> 和 <安装目录>。");
                    return 2;
                }

                var stageDir = args[1];
                var installDir = args[2];

                var installed = false;
                var noRelaunch = false;
                for (var i = 3; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--installed", StringComparison.OrdinalIgnoreCase)) installed = true;
                    // 打包脚本的端到端自检用：做完别把程序拉起来（无人值守时不能弹出一个全屏窗口）。
                    if (string.Equals(args[i], "--no-relaunch", StringComparison.OrdinalIgnoreCase)) noRelaunch = true;
                }

                // 没给安装目录（手工跑 bat 时常见）就从注册表里找
                if (string.IsNullOrEmpty(installDir) || installDir == "-")
                {
                    installDir = AppRegistry.Read().InstallDir;
                    Log("未指定安装目录，从注册表取得：" + installDir);
                }

                Log("=== TrpgMaps 升级器 ===");
                Log("模式=" + mode + "  升级包=" + stageDir + "  安装目录=" + installDir + "  安装版=" + installed);

                if (!Directory.Exists(installDir))
                {
                    Fail("安装目录不存在：" + installDir +
                         "\n\n如果是安装版，请确认程序装在哪个目录，然后手工指定：\n" +
                         "  TrpgMapsUpdater.exe --register-only <升级包目录> \"<安装目录>\"");
                    return 3;
                }

                var manifest = UpdateManifestFile.Load(stageDir);
                if (manifest == null)
                {
                    Log("升级包里没有 update.json，按「只有 payload 目录」处理。");
                    manifest = UpdateManifestFile.Default();
                }
                Log("升级包版本=" + manifest.Version);

                if (string.Equals(mode, "--register-only", StringComparison.OrdinalIgnoreCase))
                {
                    AppRegistry.Repair(installDir, manifest.Version);
                    Log("注册信息已刷新，快捷方式已重建。");
                    return 0;
                }

                if (string.Equals(mode, "--delete-only", StringComparison.OrdinalIgnoreCase))
                {
                    var n = DeleteFiles(installDir, manifest);
                    Log("已删除 " + n + " 个文件。");
                    return 0;
                }

                // ---- 正常升级流程 ----

                WaitForAppExit(installDir);

                var copied = CopyPayload(stageDir, installDir, manifest);
                Log("已覆盖 " + copied + " 个文件。");

                var deleted = DeleteFiles(installDir, manifest);
                Log("已删除 " + deleted + " 个文件。");

                if (installed)
                {
                    Log("安装版：准备刷新注册信息（需要管理员权限）。");
                    if (!RefreshRegistryElevated(stageDir, installDir))
                    {
                        Log("注册信息刷新失败（可能用户在 UAC 上点了否）。文件已经更新完成。");
                    }
                }
                else
                {
                    Log("便携版：跳过注册表步骤。");
                }

                if (noRelaunch)
                {
                    Log("--no-relaunch：跳过重启主程序。");
                }
                else
                {
                    RelaunchApp(installDir);
                }

                Log("升级完成。");
                return 0;
            }
            catch (Exception ex)
            {
                Log("升级失败：" + ex);
                Fail("升级过程中出错：\n\n" + ex.Message +
                     "\n\n程序文件可能只更新了一部分，建议重新下载完整安装包覆盖安装。");
                return 1;
            }
        }

        // ------------------------------------------------------------ 自检

        /// <summary>
        /// `--test-zip`：把整包解开一遍，逐条报告条目数与字节数。
        ///
        /// 存在的意义：升级包和安装包的负载都是**我们自己手写的 ZIP 读取器**解开的
        /// （MiniZip，见 Core\MiniZip.cs）。打包脚本在发版前用它验一遍，
        /// 能挡住"包打错了 / 用了 zip64 / 字段错位"这类只有到用户机器上才炸的问题。
        /// 报告写在升级器同目录的 TrpgMapsUpdater.log 里。
        /// </summary>
        private static int TestZip(string zipPath)
        {
            try
            {
                if (!File.Exists(zipPath))
                {
                    Log("--test-zip：文件不存在 " + zipPath);
                    return 1;
                }

                var data = File.ReadAllBytes(zipPath);
                var entries = MiniZip.ReadEntries(data);

                long totalRaw = 0;
                var ok = 0;
                var bad = 0;
                var backslashNames = 0;      // 名字里出现 '\' 说明打包工具写错了分隔符
                var nonAsciiNames = 0;       // 有非 ASCII 名字（中文底图）说明 UTF-8 往返成功

                foreach (var entry in entries)
                {
                    if (entry.Name.IndexOf('\\') >= 0) backslashNames++;

                    var nonAscii = false;
                    foreach (var ch in entry.Name) { if (ch > 127) { nonAscii = true; break; } }
                    if (nonAscii) nonAsciiNames++;

                    try
                    {
                        var bytes = MiniZip.ReadEntry(data, entry);
                        totalRaw += bytes.Length;
                        if (bytes.Length != entry.UncompressedSize) bad++;
                        else ok++;
                    }
                    catch (Exception ex)
                    {
                        bad++;
                        Log("  条目解压失败：" + entry.Name + " —— " + ex.Message);
                    }
                }

                Log("--test-zip " + zipPath);
                Log("  条目数        = " + entries.Count);
                Log("  解压成功      = " + ok);
                Log("  解压失败      = " + bad);
                Log("  解压总字节    = " + totalRaw);
                Log("  名字含反斜杠  = " + backslashNames + "（应当为 0）");
                Log("  名字含非 ASCII = " + nonAsciiNames + "（中文底图应当 > 0）");

                return bad == 0 && entries.Count > 0 && backslashNames == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Log("--test-zip 失败：" + ex);
                return 1;
            }
        }

        // ------------------------------------------------------------ 步骤

        /// <summary>等旧的 TrpgMaps.exe 退出。主程序是"启动升级器后立刻退出"，通常一瞬间就好。</summary>
        private static void WaitForAppExit(string installDir)
        {
            var deadline = Environment.TickCount + WaitForExitMs;
            while (Environment.TickCount < deadline)
            {
                Process[] running;
                try { running = Process.GetProcessesByName(AppInfo.Name); }
                catch { running = new Process[0]; }

                var alive = false;
                foreach (var process in running)
                {
                    try
                    {
                        if (process.Id == Process.GetCurrentProcess().Id) continue;
                        alive = true;
                        break;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                if (!alive) return;
                Thread.Sleep(300);
            }

            Log("等待旧进程退出超时（" + WaitForExitMs + " 毫秒），继续尝试覆盖。");
        }

        /// <summary>把 payload 目录整个覆盖到安装目录。</summary>
        private static int CopyPayload(string stageDir, string installDir, UpdateManifestFile manifest)
        {
            var payload = Path.Combine(stageDir, manifest.Payload);
            if (!Directory.Exists(payload))
            {
                // 有些包直接就是"根目录下的文件"，没有单独的 payload 子目录
                payload = stageDir;
            }

            var count = 0;
            var failures = new List<string>();

            foreach (var file in Directory.GetFiles(payload, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(payload.Length).TrimStart('\\', '/');

                // 升级器自己和那几个说明文件不要覆盖进安装目录
                if (IsToolFile(relative)) continue;

                var target = Path.Combine(installDir, relative);
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                try
                {
                    File.Copy(file, target, true);
                    count++;
                }
                catch (Exception ex)
                {
                    failures.Add(relative + "：" + ex.Message);
                    Log("覆盖失败：" + relative + " —— " + ex.Message);
                }
            }

            if (failures.Count > 0)
            {
                throw new IOException("有 " + failures.Count + " 个文件没能覆盖：\n" +
                                      string.Join("\n", failures.ToArray()));
            }

            return count;
        }

        /// <summary>包里的工具类文件不参与覆盖。</summary>
        private static bool IsToolFile(string relative)
        {
            var name = Path.GetFileName(relative).ToLowerInvariant();
            return name == "trpgmapsupdater.exe";
        }

        /// <summary>按清单删文件。清单是相对安装目录的路径，一律先消毒再删。</summary>
        private static int DeleteFiles(string installDir, UpdateManifestFile manifest)
        {
            var count = 0;

            foreach (var relative in manifest.DeleteFiles)
            {
                if (string.IsNullOrEmpty(relative)) continue;

                var clean = relative.Replace('\\', '/').TrimStart('/');
                if (clean.Contains("..") || clean.Length > 1 && clean[1] == ':')
                {
                    Log("跳过可疑的删除路径：" + relative);
                    continue;
                }

                var full = Path.Combine(installDir, clean.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (File.Exists(full))
                    {
                        File.Delete(full);
                        count++;
                        Log("已删除：" + clean);
                    }
                }
                catch (Exception ex)
                {
                    Log("删除失败：" + clean + " —— " + ex.Message);
                }
            }

            // 清单里没列、但不该出现在安装目录里的东西（升级器自己的日志）
            TryDelete(Path.Combine(installDir, "TrpgMapsUpdater.log"));

            return count;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        /// <summary>用管理员权限再跑一次自己，只做注册表那一步。</summary>
        private static bool RefreshRegistryElevated(string stageDir, string installDir)
        {
            if (AppRegistry.IsElevated)
            {
                // 本来就是管理员（比如用户直接右键"以管理员身份运行"跑的安装版）
                AppRegistry.Repair(installDir, UpdateManifestFile.Load(stageDir) == null
                    ? AppInfo.Version
                    : UpdateManifestFile.Load(stageDir).Version);
                return true;
            }

            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.Arguments = "--register-only " + Quote(stageDir) + " " + Quote(installDir);
                psi.UseShellExecute = true;
                psi.Verb = "runas";

                using (var child = Process.Start(psi))
                {
                    child.WaitForExit();
                    return child.ExitCode == 0;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // 用户在 UAC 弹窗上点了"否"
                Log("用户取消了管理员授权。");
                return false;
            }
            catch (Exception ex)
            {
                Log("提权失败：" + ex.Message);
                return false;
            }
        }

        private static void RelaunchApp(string installDir)
        {
            try
            {
                var exe = Path.Combine(installDir, AppExeName);
                if (!File.Exists(exe))
                {
                    Log("找不到主程序，无法自动重启：" + exe);
                    return;
                }

                var psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.WorkingDirectory = installDir;
                psi.UseShellExecute = true;
                Process.Start(psi);
                Log("已重新启动主程序。");
            }
            catch (Exception ex)
            {
                Log("自动重启失败：" + ex.Message);
                MessageBox.Show("更新已完成，但自动重启失败，请手动打开 TrpgMaps。\n\n" + ex.Message,
                    "TrpgMaps 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // ------------------------------------------------------------ 杂项

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        private static void Fail(string message)
        {
            try
            {
                MessageBox.Show(message, "TrpgMaps 更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        private static void Log(string message)
        {
            try
            {
                var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message;
                File.AppendAllText(Path.Combine(AppEnv.Root, "TrpgMapsUpdater.log"), line + Environment.NewLine);
            }
            catch { }
        }
    }

    /// <summary>
    /// 升级包里的 `update.json`。
    /// 单独一个类（而不是复用主程序那边的 UpdateManifest）：那个描述的是
    /// **仓库里的清单**（远端有什么版本），这个描述的是**包里装了什么**
    /// （要覆盖哪些文件、要删哪些文件）。两者字段不重叠，混着用容易看错。
    /// </summary>
    internal sealed class UpdateManifestFile
    {
        public string Version = AppInfo.Version;
        public string Payload = "payload";
        public readonly List<string> DeleteFiles = new List<string>();

        public static UpdateManifestFile Default()
        {
            return new UpdateManifestFile();
        }

        public static UpdateManifestFile Load(string stageDir)
        {
            try
            {
                var path = Path.Combine(stageDir, "update.json");
                if (!File.Exists(path)) return null;

                var text = ReadAllTextUtf8(path);
                var obj = MiniJson.ParseObject(text);
                if (obj == null) return null;

                var result = new UpdateManifestFile();
                var version = MiniJson.GetString(obj, "version");
                if (!string.IsNullOrEmpty(version)) result.Version = version;

                var payload = MiniJson.GetString(obj, "payload");
                if (!string.IsNullOrEmpty(payload)) result.Payload = payload;

                object list;
                if (obj.TryGetValue("deleteFiles", out list))
                {
                    var array = list as List<object>;
                    if (array != null)
                    {
                        foreach (var item in array)
                        {
                            var value = item as string;
                            if (!string.IsNullOrEmpty(value)) result.DeleteFiles.Add(value);
                        }
                    }
                }

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static string ReadAllTextUtf8(string path)
        {
            var bytes = File.ReadAllBytes(path);
            // 有 BOM 就去掉（UTF8Encoding(false, true) 会被 BOM 噎住）
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch { return Encoding.Default.GetString(bytes); }
        }
    }
}
