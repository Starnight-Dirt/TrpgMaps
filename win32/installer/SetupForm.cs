using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace TrpgMaps
{
    /// <summary>
    /// 安装 / 卸载的**实际动作**。界面（<see cref="SetupForm"/>）只负责收集参数、
    /// 显示进度，真正的活儿都在这里 —— 这样 `/quiet` 静默模式能走同一套代码，
    /// 不用维护两条路径（历史上"界面能装、静默装不上"就是这么来的）。
    /// </summary>
    internal static class SetupEngine
    {
        private const string PayloadResource = "TrpgMaps.payload.zip";

        /// <summary>安装目录默认值。</summary>
        public static string DefaultInstallDir()
        {
            // 64 位系统上 `ProgramFiles(x86)` 这个环境变量才有值。
            // 本程序是 32 位的：在 64 位系统里，即使是 `SpecialFolder.ProgramFiles`
            // 也会被 WOW64 重定向成 "C:\Program Files (x86)"，两个写法结果一样；
            // 但显式读环境变量更能表达意图，也方便以后改成"装到 Program Files 原生目录"。
            var pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (string.IsNullOrEmpty(pf86))
            {
                pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            }
            if (string.IsNullOrEmpty(pf86)) pf86 = @"C:\Program Files";

            return Path.Combine(pf86, AppInfo.Name);
        }

        public static bool Is64BitOs
        {
            get { return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ProgramFiles(x86)")); }
        }

        /// <summary>取出内嵌的程序负载。</summary>
        public static byte[] LoadPayload()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(PayloadResource))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException(
                        "安装包里没有程序负载（编译前需要先生成 installer\\payload.zip）。");
                }

                using (var memory = new MemoryStream())
                {
                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        var read = stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;
                        memory.Write(buffer, 0, read);
                    }
                    return memory.ToArray();
                }
            }
        }

        // ------------------------------------------------------------ 安装

        public static void Install(string installDir, bool desktop, bool startMenu, bool taskbar,
            Action<string> log, Action<int, int> progress)
        {
            if (string.IsNullOrEmpty(installDir)) throw new ArgumentException("安装目录不能为空。");
            installDir = Path.GetFullPath(installDir.Trim());

            if (IsDriveRoot(installDir))
                throw new ArgumentException("不能把程序直接装到盘符根目录下。");

            log("安装目录：" + installDir);
            log("目标系统：" + (Is64BitOs ? "64 位 Windows" : "32 位 Windows"));

            var existing = AppRegistry.Read();
            if (existing.Installed && !SameDir(existing.InstallDir, installDir))
            {
                log("提示：检测到程序已经装在 " + existing.InstallDir + "，本次会再装一份到新位置。");
                log("（旧的卸载记录会被这一份覆盖，请自行把旧目录删掉。）");
            }

            KillRunningApp(installDir, log);

            // --- 1. 建目录 ---
            progress(1, 6);
            log("创建目录…");
            Directory.CreateDirectory(installDir);

            // --- 2. 解负载 ---
            progress(2, 6);
            var payload = LoadPayload();
            log(string.Format("释放程序文件（{0:0.0} MB）…", payload.Length / 1048576.0));
            var written = MiniZip.ExtractTo(payload, installDir, true);
            log("写入 " + written.Count + " 个文件。");

            // --- 3. 给普通用户写权限 ---
            progress(3, 6);
            // 这一条是本程序"绿色部署"的代价：底图上传、绘图进度、运行日志
            // 都写在程序自己的目录里（见 AppEnv），而 Program Files 默认只读。
            // 不放开的话，装完之后上传底图会失败、画的东西存不下来。
            // 只给 BUILTIN\Users，不给 Everyone；不放开对系统目录的权限。
            log("授予普通用户对本目录的修改权限（程序需要往这里写底图与绘图数据）…");
            GrantUsersModify(installDir, log);

            // --- 4. 注册表 ---
            progress(4, 6);
            log("写入注册表（卸载项 + 安装信息）…");
            AppRegistry.WriteInstall(installDir, AppInfo.Version, desktop, startMenu, taskbar);

            // --- 5. 快捷方式 ---
            progress(5, 6);
            log("创建快捷方式…");
            Shortcuts.RemoveAll(installDir);
            if (desktop) log("  桌面：" + (Shortcuts.CreateDesktop(installDir) ? "已创建" : "失败"));
            if (startMenu) log("  开始菜单：" + (Shortcuts.CreateStartMenu(installDir) ? "已创建" : "失败"));
            if (taskbar)
            {
                var ok = Shortcuts.PinTaskbar(installDir);
                log("  任务栏：" + (ok
                    ? "已尝试固定（Win10 1607 之后系统不再允许脚本固定，若没出现请手动右键固定）"
                    : "固定失败（不影响使用）"));
            }

            // --- 6. 卸载程序 ---
            progress(6, 6);
            var uninstaller = Path.Combine(installDir, AppRegistry.UninstallerName);
            try
            {
                File.Copy(Application.ExecutablePath, uninstaller, true);
                log("已放出卸载程序：" + AppRegistry.UninstallerName);
            }
            catch (Exception ex)
            {
                log("放出卸载程序失败（控制面板里的卸载按钮会失效）：" + ex.Message);
            }

            log("");
            log("安装完成。程序目录：" + installDir);
        }

        // ------------------------------------------------------------ 卸载

        public static void Uninstall(bool keepUserData, Action<string> log, Action<int, int> progress)
        {
            // 卸载程序本身就是从安装目录里运行的，所以"安装目录"就是它自己的目录。
            var installDir = AppEnv.Root;
            log("安装目录：" + installDir);

            var info = AppRegistry.Read();
            if (info.Installed && !SameDir(info.InstallDir, installDir))
            {
                log("提示：注册表里记的安装位置是 " + info.InstallDir + "，与当前目录不一致；");
                log("按当前位置继续卸载（这份应当是手工拷过来的）。");
            }

            // --- 1. 结束主程序 ---
            progress(1, 5);
            KillRunningApp(installDir, log);

            // --- 2. 快捷方式 + 注册表 ---
            progress(2, 5);
            log("删除快捷方式…");
            Shortcuts.RemoveAll(installDir);

            log("清理注册表…");
            AppRegistry.RemoveInstall();

            // --- 3. 删文件 ---
            progress(3, 5);
            log("删除程序文件…");
            var self = Application.ExecutablePath;
            var removed = 0;
            var kept = 0;

            foreach (var file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
            {
                if (SameDir(file, self)) continue;                       // 自己最后删
                if (keepUserData && IsUserData(installDir, file)) { kept++; continue; }

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex)
                {
                    log("  删不掉：" + file + " —— " + ex.Message);
                }
            }
            log("删除 " + removed + " 个文件" + (kept > 0 ? "，保留 " + kept + " 个用户数据文件" : "") + "。");

            // --- 4. 清空目录 ---
            progress(4, 5);
            if (!keepUserData)
            {
                RemoveEmptyDirectories(installDir, log);
            }
            else
            {
                log("已保留 maps\\ 与 drawing.json（用户的地图与绘图数据）。");
            }

            // --- 5. 自删 ---
            progress(5, 5);
            var canRemoveDir = !keepUserData || IsDirectoryEmpty(installDir);
            log("收尾：卸载程序自身会在退出后自动删除。");
            ScheduleSelfDelete(self, canRemoveDir ? installDir : null);
        }

        /// <summary>哪些算"用户数据"：底图目录与绘图存档。</summary>
        private static bool IsUserData(string installDir, string file)
        {
            var relative = file.Substring(installDir.Length).TrimStart('\\', '/');
            if (relative.StartsWith("maps\\", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var name = Path.GetFileName(relative);
            return string.Equals(name, "drawing.json", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "app.log", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------ 公共小工具

        /// <summary>
        /// 给普通用户加"修改"权限（icacls）。
        /// 用 SID 而不是名字：`Users` 这个组名在非中文/非英文系统上会被本地化
        /// （比如德语是 `Benutzer`），写名字会直接失败；`*S-1-5-32-545` 是
        /// 全语言通用的内置 Users 组。
        /// </summary>
        private static void GrantUsersModify(string dir, Action<string> log)
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "icacls.exe");
                psi.Arguments = "\"" + dir + "\" /grant *S-1-5-32-545:(OI)(CI)M /T /C /Q";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (var process = Process.Start(psi))
                {
                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode != 0) log("  权限设置返回码 " + process.ExitCode + "（不影响程序运行，但上传底图可能失败）。");
                }
            }
            catch (Exception ex)
            {
                log("  权限设置失败：" + ex.Message);
            }
        }

        /// <summary>装/卸之前先把还在跑的实例关掉，否则文件被占用。</summary>
        public static void KillRunningApp(string installDir, Action<string> log)
        {
            try
            {
                var running = Process.GetProcessesByName(AppInfo.Name);
                foreach (var process in running)
                {
                    try
                    {
                        if (process.Id == Process.GetCurrentProcess().Id) continue;

                        // 只关"同一个安装目录里的那个"：别把用户从别处（比如 U 盘上）
                        // 开着的便携版一起杀掉。
                        var path = null as string;
                        try { path = process.MainModule.FileName; }
                        catch { }

                        if (path != null && !SameDir(Path.GetDirectoryName(path), installDir)) continue;

                        log("关闭正在运行的 TrpgMaps（PID " + process.Id + "）…");
                        if (!process.CloseMainWindow()) process.Kill();
                        process.WaitForExit(5000);
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                log("检查运行中的实例失败：" + ex.Message);
            }
        }

        private static void RemoveEmptyDirectories(string root, Action<string> log)
        {
            try
            {
                var dirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
                Array.Sort(dirs);
                Array.Reverse(dirs);     // 从最深的开始删
                foreach (var dir in dirs)
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir, false);
                    }
                    catch { }
                }

                if (IsDirectoryEmpty(root))
                {
                    try
                    {
                        Directory.Delete(root, false);
                        log("安装目录已删除。");
                    }
                    catch (Exception ex)
                    {
                        log("安装目录删不掉（通常是被别的程序占用）：" + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                log("清理空目录失败：" + ex.Message);
            }
        }

        private static bool IsDirectoryEmpty(string dir)
        {
            try
            {
                return !Directory.Exists(dir) || Directory.GetFileSystemEntries(dir).Length == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 让卸载程序把自己删掉。进程自己删不掉正在运行的 exe，标准做法是
        /// 起一个 cmd 循环重试。
        ///
        /// 生成的 .bat 里**一个中文字面量都没有**（路径通过参数传进来，
        /// cmd 收到的是 Unicode 命令行），所以不受系统代码页影响 ——
        /// 批处理里写中文路径是最容易翻车的地方，这里从结构上避开了。
        /// </summary>
        private static void ScheduleSelfDelete(string self, string dirToRemove)
        {
            try
            {
                var bat = Path.Combine(Path.GetTempPath(), "trpgmaps_cleanup_" + Environment.TickCount.ToString("x") + ".bat");

                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine(":wait");
                sb.AppendLine("del /f /q \"%~1\" >nul 2>&1");
                sb.AppendLine("if exist \"%~1\" (ping -n 3 127.0.0.1 >nul & goto wait)");
                sb.AppendLine("if not \"%~2\"==\"\" rd /s /q \"%~2\" >nul 2>&1");
                sb.AppendLine("del /f /q \"%~f0\" >nul 2>&1");

                // 显式指定 ANSI 编码：内容本来就是纯 ASCII，但显式写出来
                // 免得将来有人往里面加了中文才发现编码不对。
                File.WriteAllText(bat, sb.ToString(), Encoding.Default);

                var psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                psi.Arguments = "/c \"" + bat + "\" \"" + self + "\" " +
                                (string.IsNullOrEmpty(dirToRemove) ? "\"\"" : "\"" + dirToRemove + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                AppLog.Write("安排自删除失败", ex);
            }
        }

        private static bool IsDriveRoot(string path)
        {
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            return full.Length <= 2 || full.EndsWith(":");
        }

        private static bool SameDir(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'),
                                     Path.GetFullPath(b).TrimEnd('\\', '/'),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>安装 / 卸载向导（单个窗口，两种模式）。</summary>
    internal sealed class SetupForm : Form
    {
        private readonly bool _uninstall;

        private TextBox _dirBox;
        private Button _browse;
        private CheckBox _desktop;
        private CheckBox _startMenu;
        private CheckBox _taskbar;
        private CheckBox _keepData;
        private TextBox _log;
        private ProgressBar _progress;
        private Button _primary;
        private Button _cancel;
        private Label _summary;

        public SetupForm(bool uninstall)
        {
            _uninstall = uninstall;
            BuildUi(uninstall);
        }

        // ------------------------------------------------------------ 界面

        private void BuildUi(bool uninstall)
        {
            Text = uninstall ? ("卸载 " + AppInfo.Name) : ("安装 " + AppInfo.Name + " " + AppInfo.VersionText);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = new Font("Microsoft YaHei", 9f);
            ClientSize = new Size(640, uninstall ? 430 : 470);

            var title = MakeLabel(uninstall ? "卸载 TrpgMaps" : "安装 TrpgMaps",
                15f, FontStyle.Bold, Color.White);
            title.SetBounds(24, 18, 592, 30);
            Controls.Add(title);

            var subtitle = MakeLabel(uninstall
                    ? "将从本机移除 TrpgMaps " + AppInfo.VersionText + "，并清理注册表与快捷方式。"
                    : "跑团地图投屏工具 " + AppInfo.VersionText + " —— 32 位，兼容 Windows 7 SP1 及以上。",
                9f, FontStyle.Regular, Theme.TextDim);
            subtitle.SetBounds(24, 50, 592, 22);
            Controls.Add(subtitle);

            var y = 84;

            if (!uninstall)
            {
                var dirLabel = MakeLabel("安装目录", 9f, FontStyle.Regular, Theme.Text);
                dirLabel.SetBounds(24, y, 592, 20);
                Controls.Add(dirLabel);
                y += 22;

                _dirBox = new TextBox();
                _dirBox.Text = SetupEngine.DefaultInstallDir();
                _dirBox.SetBounds(24, y, 496, 26);
                _dirBox.BackColor = Color.FromArgb(53, 53, 53);
                _dirBox.ForeColor = Color.White;
                _dirBox.BorderStyle = BorderStyle.FixedSingle;
                Controls.Add(_dirBox);

                _browse = MakeButton("浏览…", false);
                _browse.SetBounds(528, y - 1, 88, 28);
                _browse.Click += delegate { BrowseForFolder(); };
                Controls.Add(_browse);
                y += 38;

                var defaulted = MakeHint(SetupEngine.Is64BitOs
                    ? "默认位置取自 64 位系统的 Program Files (x86)。"
                    : "默认位置取自 32 位系统的 Program Files。");
                defaulted.SetBounds(24, y, 592, 20);
                Controls.Add(defaulted);
                y += 30;

                var group = MakeLabel("附加选项", 10f, FontStyle.Bold, Color.White);
                group.SetBounds(24, y, 592, 22);
                Controls.Add(group);
                y += 26;

                _desktop = MakeCheck("在桌面上创建快捷方式", true);
                _desktop.SetBounds(30, y, 580, 22);
                Controls.Add(_desktop);
                y += 24;

                _startMenu = MakeCheck("添加到开始菜单", true);
                _startMenu.SetBounds(30, y, 580, 22);
                Controls.Add(_startMenu);
                y += 24;

                _taskbar = MakeCheck("固定到任务栏（Windows 10 1607 之后系统可能不允许，失败不影响其它功能）", true);
                _taskbar.SetBounds(30, y, 580, 22);
                Controls.Add(_taskbar);
                y += 30;
            }
            else
            {
                _summary = MakeLabel(string.Empty, 9f, FontStyle.Regular, Theme.Text);
                _summary.SetBounds(24, y, 592, 60);
                Controls.Add(_summary);
                y += 66;

                _keepData = MakeCheck("保留地图与绘图数据（maps 目录与 drawing.json）", false);
                _keepData.SetBounds(30, y, 580, 22);
                Controls.Add(_keepData);
                y += 30;
            }

            _log = new TextBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = Color.FromArgb(26, 26, 26);
            _log.ForeColor = Color.FromArgb(200, 220, 200);
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.Font = new Font("Consolas", 8.5f);
            _log.SetBounds(24, y, 592, 150);
            _log.WordWrap = false;
            Controls.Add(_log);

            _progress = new ProgressBar();
            _progress.Minimum = 0;
            _progress.Maximum = 6;
            _progress.SetBounds(24, y + 158, 592, 10);
            Controls.Add(_progress);

            _cancel = MakeButton("取消", false);
            _cancel.SetBounds(424, y + 178, 92, 32);
            _cancel.Click += delegate { Close(); };
            Controls.Add(_cancel);

            _primary = MakeButton(uninstall ? "开始卸载" : "开始安装", true);
            _primary.SetBounds(524, y + 178, 92, 32);
            _primary.Click += delegate { Run(); };
            Controls.Add(_primary);

            AcceptButton = _primary;
            CancelButton = _cancel;

            if (uninstall) RefreshUninstallSummary();
        }

        private void RefreshUninstallSummary()
        {
            var info = AppRegistry.Read();
            var text = "安装位置：" + AppEnv.Root + "\r\n";
            text += info.Installed
                ? "注册表中登记的版本：" + info.Version + "　　快捷方式：" + info.ShortcutText()
                : "注册表中没有找到本程序的安装记录（可能已经被清理过）。";
            _summary.Text = text;
        }

        private void BrowseForFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择安装目录";
                dialog.ShowNewFolderButton = true;
                if (Directory.Exists(_dirBox.Text)) dialog.SelectedPath = _dirBox.Text;

                if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dialog.SelectedPath))
                {
                    _dirBox.Text = Path.Combine(dialog.SelectedPath, AppInfo.Name);
                }
            }
        }

        // ------------------------------------------------------------ 执行

        private void Run()
        {
            _primary.Enabled = false;
            _cancel.Enabled = false;

            try
            {
                if (_uninstall)
                {
                    if (MessageBox.Show(
                            "确定要卸载 TrpgMaps 吗？\r\n\r\n" +
                            (_keepData.Checked ? "地图与绘图数据会保留。" : "所有文件都会被删除。"),
                            "确认卸载", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                    {
                        return;
                    }

                    SetupEngine.Uninstall(_keepData.Checked, Write, SetProgress);
                    Finish("卸载完成，窗口可以关闭了。");
                }
                else
                {
                    var dir = _dirBox.Text;
                    SetupEngine.Install(dir, _desktop.Checked, _startMenu.Checked, _taskbar.Checked,
                        Write, SetProgress);
                    Finish("安装完成。");
                }
            }
            catch (Exception ex)
            {
                Write("");
                Write("出错了：" + ex.Message);
                AppLog.Write("安装/卸载出错", ex);
                MessageBox.Show(ex.Message, "TrpgMaps", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _cancel.Enabled = true;
                _cancel.Text = "关闭";
                _primary.Visible = false;
                _cancel.Focus();
            }
        }

        private void Finish(string message)
        {
            Write("");
            Write(message);
            _progress.Value = _progress.Maximum;

            MessageBox.Show(message, "TrpgMaps", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Write(string line)
        {
            _log.AppendText(line + "\r\n");
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
            Application.DoEvents();     // 安装是同步的，让日志实时刷出来
        }

        private void SetProgress(int value, int total)
        {
            if (value < _progress.Minimum) value = _progress.Minimum;
            if (value > _progress.Maximum) value = _progress.Maximum;
            _progress.Value = value;
            Application.DoEvents();
        }

        // ------------------------------------------------------------ 静默模式

        public static int RunInstallQuiet()
        {
            SetupEngine.Install(SetupEngine.DefaultInstallDir(), false, false, false,
                delegate { }, delegate { });
            return 0;
        }

        public static int RunUninstallQuiet()
        {
            SetupEngine.Uninstall(false, delegate { }, delegate { });
            return 0;
        }

        // ------------------------------------------------------------ 小工具

        private static Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            var label = new Label();
            label.Text = text;
            label.Font = new Font("Microsoft YaHei", size, style);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.AutoSize = false;
            return label;
        }

        private Label MakeHint(string text)
        {
            return MakeLabel(text, 8.5f, FontStyle.Regular, Theme.TextDim);
        }

        private static CheckBox MakeCheck(string text, bool @checked)
        {
            var box = new CheckBox();
            box.Text = text;
            box.Checked = @checked;
            box.ForeColor = Theme.Text;
            box.BackColor = Color.Transparent;
            box.FlatStyle = FlatStyle.Flat;
            box.Font = new Font("Microsoft YaHei", 9f);
            return box;
        }

        private static Button MakeButton(string text, bool accent)
        {
            var button = new Button();
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = accent ? Theme.Accent : Theme.Button;
            button.ForeColor = Color.White;
            button.Font = new Font("Microsoft YaHei", 9.5f);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            return button;
        }
    }
}
