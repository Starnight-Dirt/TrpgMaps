using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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

    // ========================================================================
    //  界面
    // ========================================================================

    /// <summary>
    /// 安装 / 卸载向导。
    ///
    /// **全自绘**：从自绘控件库（<see cref="SetupUi.cs"/>）取胶囊按钮、选项行、
    /// 输入框、细进度条和日志视图，窗体本身也去掉了标题栏，改成自己画的圆角卡片。
    /// 这么做不只是为了好看 —— WinForms 的系统控件在 Win7(Aero) 和
    /// Win10/11(平面) 上长相完全不同，装机时看到的界面取决于目标机，
    /// 只有全部自己画才能保证"在哪台机器上都长这样"。
    ///
    /// 向导只有两页（安装位置 → 安装选项），用不着 Page 对象那一套，
    /// 直接把两页的控件都建好、按 <see cref="_page"/> 切换 Visible。
    /// </summary>
    internal sealed class SetupForm : Form
    {
        private readonly bool _uninstall;

        // ---- 窗口 / 布局常量 ----
        private const int PadX = 36;
        private const int WinWidth = 660;

        // 安装窗口要放下：标题区 + 三行选项胶囊（168→330）+ 一行脚注（342→360）
        // + 页脚（提示 / 分隔线 / 进度 / 日志 / 按钮）。500 高时页脚会被挤到
        // y=300 一带，和第三行胶囊、脚注直接叠在一起 —— 所以给到 580。
        private const int WinHeightInstall = 580;

        // 卸载窗口只有一页、内容少，矮一点更像个"确认框"；但也不能太矮：
        // 页脚是从底部倒推的，430 时 _status 会落到 y=230，正好压住说明行。
        private const int WinHeightUninstall = 450;

        // ---- 页面 ----
        private int _page;                       // 0 = 位置，1 = 选项
        private readonly List<Control>[] _pageControls = { new List<Control>(), new List<Control>() };
        private string _chosenDir;
        private bool _busy;

        // ---- 页 1：安装位置 ----
        private CapsuleTextBox _dirBox;
        private CapsuleButton _browse;

        // ---- 页 2：安装选项 ----
        private OptionRow _optStartMenu;
        private OptionRow _optDesktop;
        private OptionRow _optTaskbar;

        // ---- 卸载页 ----
        private CheckStrip _keepData;

        // ---- 进度页（两页共用）----
        private LogView _log;
        private ThinProgress _progress;
        private CapsuleButton _primary;
        private CapsuleButton _secondary;
        private CapsuleButton _back;
        private Caption _status;

        // ---- 无标题栏窗口的拖动 ----
        private bool _dragging;
        private Point _dragOrigin;

        public SetupForm(bool uninstall)
        {
            _uninstall = uninstall;
            BuildWindow();
            BuildChrome();
            if (uninstall) BuildUninstallPage();
            else { BuildLocationPage(); BuildOptionsPage(); }
            BuildFooter();
            SwitchPage(0);
        }

        // ------------------------------------------------------------ 窗口

        private void BuildWindow()
        {
            Text = _uninstall ? ("卸载 " + AppInfo.Name) : ("安装 " + AppInfo.Name + " " + AppInfo.VersionText);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(WinWidth, _uninstall ? WinHeightUninstall : WinHeightInstall);
            MinimumSize = new Size(WinWidth, _uninstall ? WinHeightUninstall : WinHeightInstall);
            MaximumSize = MinimumSize;
            FormBorderStyle = FormBorderStyle.None;      // 标题栏由我们自己画
            BackColor = SetupTheme.Background;
            ForeColor = SetupTheme.Text;
            DoubleBuffered = true;
            KeyPreview = true;

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);

            ApplyAppIcon();
            ApplyRoundedRegion();
        }

        /// <summary>构建期嵌进来的自定义图标在程序集里的逻辑名（见 csproj 的 EmbeddedResource）。</summary>
        private const string IconResource = "TrpgMaps.appicon.ico";

        /// <summary>
        /// 给窗口装上自定义图标。
        ///
        /// **为什么必须显式做这一步**：csproj 里的 `ApplicationIcon` 只把图标写进
        /// **exe 文件的 PE 资源段** —— 那只让资源管理器里的 exe 好看。运行中的窗口
        /// 用的是 `Form.Icon`，不赋值就是系统默认那个空白图标，任务栏里一眼就能看出不对。
        ///
        /// **为什么读嵌入资源而不是磁盘**：安装器是**单文件**发布的；卸载器更是安装器
        /// exe 的副本（落在安装目录里，旁边不保证有 `Resources\`）。所以图标编进
        /// 程序集，任何位置运行都取得到。主程序那套"先找 `Resources\appicon.ico`"
        /// 的做法在这里不成立。
        ///
        /// 逐级降级，任何一步失败都不影响启动：
        ///  1. 嵌入资源里的 256 那张 —— 高 DPI / Alt+Tab 不糊；
        ///  2. 同一份 .ico 的 48 那张 —— 256 往往是 PNG 压缩条目，老 GDI+ 解不开会抛；
        ///  3. `Icon.ExtractAssociatedIcon(exe)` —— PE 资源段里由 `ApplicationIcon` 编进去的；
        ///  4. 全失败就保持系统默认图标。
        /// </summary>
        private void ApplyAppIcon()
        {
            byte[] raw = null;
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(IconResource))
                {
                    if (stream != null)
                    {
                        raw = new byte[stream.Length];
                        var read = 0;
                        while (read < raw.Length)
                        {
                            var n = stream.Read(raw, read, raw.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        if (read != raw.Length) raw = null;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("读取嵌入图标失败，改用 exe 内嵌图标", ex);
            }

            if (raw != null)
            {
                foreach (var px in new[] { 256, 48, 32 })
                {
                    try
                    {
                        using (var memory = new MemoryStream(raw))
                        {
                            // Icon 会持有这个流不放；用完后 Icon 自己会 Dispose 掉它，
                            // 所以这里不能 using 包住 Icon 本身（生命周期交给窗体）。
                            Icon = new Icon(memory, new Size(px, px));
                        }
                        AppLog.Write("窗口图标取用嵌入资源的 " + px + "x" + px + " 条目");
                        return;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("嵌入图标的 " + px + " 条目解不开，继续降级", ex);
                    }
                }
            }

            try
            {
                var embedded = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (embedded != null) Icon = embedded;
            }
            catch (Exception ex)
            {
                AppLog.Write("取 exe 内嵌图标也失败，保持系统默认图标", ex);
            }
        }

        /// <summary>窗口圆角的曲率，和 <see cref="SetupDraw.Capsule"/> 用的是同一个值。</summary>
        private const float WindowRadius = 18f;

        /// <summary>
        /// 把无边框窗口裁成圆角卡片。
        ///
        /// `FormBorderStyle.None` 只是**去掉了标题栏**，窗口本身仍是矩形 ——
        /// 只靠 <see cref="OnPaint"/> 里那圈描边画个圆角轮廓，四个角外面的
        /// 深色仍然会被填满，看起来就是"一个方框里画了个圆角线"，很廉价。
        /// 真正生效要靠 `Region`：用与描边**完全相同**的路径去裁窗口，
        /// 两者重合才严丝合缝。
        ///
        /// 调用时机有两个：句柄创建后（这时才谈得上 Region），以及尺寸变化时
        /// （本窗口是固定尺寸，但 DPI 缩放会改 ClientSize，漏了就又会露出直角）。
        /// </summary>
        private void ApplyRoundedRegion()
        {
            if (!IsHandleCreated) return;

            var box = new RectangleF(0f, 0f, ClientSize.Width, ClientSize.Height);
            if (box.Width <= 1f || box.Height <= 1f) return;

            using (var path = SetupDraw.Capsule(box, WindowRadius))
            {
                var old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyRoundedRegion();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyRoundedRegion();
        }

        /// <summary>无边框窗口要自己实现拖动，否则用户拿它没办法。</summary>
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragOrigin = new Point(e.X, e.Y);
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            Location = new Point(Location.X + (e.X - _dragOrigin.X),
                                 Location.Y + (e.Y - _dragOrigin.Y));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(SetupTheme.Background);
            SetupDraw.Smooth(g);

            // 描边路径必须和 ApplyRoundedRegion() 用的**完全一致**：
            // Region 已经把窗口裁掉了圆角外的一切，所以描边要画在最外圈上
            // （0,0 起点，不内缩半个线宽），否则边缘会露出一条背景色的缝。
            var box = new RectangleF(0f, 0f, ClientSize.Width, ClientSize.Height);

            // 整窗一圈很淡的内描边：无边框窗口在深色桌面上会"糊"进背景，
            // 加这一圈就有一张卡片浮起来的感觉。
            using (var path = SetupDraw.Capsule(box, WindowRadius))
            using (var pen = new Pen(Color.FromArgb(38, 38, 44), 1f))
            {
                pen.Alignment = PenAlignment.Inset;   // 往内画，线不会溢出被裁掉
                g.DrawPath(pen, path);
            }
        }

        // ------------------------------------------------------------ 顶部

        private void BuildChrome()
        {
            // 品牌行：一个小方块图标 + 标题 + 版本
            var mark = new BrandMark();
            mark.SetBounds(PadX, 34, 34, 34);
            Controls.Add(mark);

            var title = new Caption(
                _uninstall ? "卸载 TrpgMaps" : "安装 TrpgMaps",
                16f, FontStyle.Bold, SetupTheme.Text);
            title.SetBounds(PadX + 46, 30, 400, 28);
            Controls.Add(title);

            var subtitle = new Caption(
                _uninstall
                    ? "将移除 TrpgMaps " + AppInfo.VersionText
                    : "TrpgMaps " + AppInfo.VersionText + "   ·  32 位，兼容 Windows 7 SP1 及以上",
                9.5f, FontStyle.Regular, SetupTheme.TextDim);
            subtitle.SetBounds(PadX + 46, 56, 470, 20);
            Controls.Add(subtitle);

            // 关闭按钮：右上角一个小 ×，自绘
            var close = new CloseButton();
            close.SetBounds(WinWidth - PadX - 26, 30, 26, 26);
            close.Click += delegate
            {
                if (_busy) return;
                Close();
            };
            Controls.Add(close);

            // 步骤指示：两个小圆点，位于标题下方
            var steps = new StepDots(2, delegate { return _page; });
            steps.SetBounds(PadX + 46, 84, 60, 8);
            Controls.Add(steps);
        }

        // ------------------------------------------------------------ 页 1

        private void BuildLocationPage()
        {
            var head = Section("安装位置", "程序会被释放到下面这个文件夹，可以改成别处。");
            AddToPage(0, head[0], head[1]);

            _dirBox = new CapsuleTextBox();
            _dirBox.Text = SetupEngine.DefaultInstallDir();
            _dirBox.SetBounds(PadX, 172, WinWidth - PadX * 2 - 96, 40);
            AddToPage(0, _dirBox);

            _browse = new CapsuleButton();
            _browse.Text = "浏览";
            _browse.Primary = false;
            _browse.SetBounds(WinWidth - PadX - 86, 172, 86, 40);
            _browse.Click += delegate { BrowseForFolder(); };
            AddToPage(0, _browse);

            var hint = new Caption(
                SetupEngine.Is64BitOs
                    ? "默认取 64 位系统的 Program Files (x86)。"
                    : "默认取 32 位系统的 Program Files。",
                9f, FontStyle.Regular, SetupTheme.TextFaint);
            hint.SetBounds(PadX + 2, 219, 520, 18);
            AddToPage(0, hint);
        }

        // ------------------------------------------------------------ 页 2

        private void BuildOptionsPage()
        {
            // 说明文字刻意留空：胶囊的三态（灰边 / 白底 / 蓝底带勾）本身已经说明
            // "点一下能切换"，再写一行反而啰嗦。Section 允许 desc 为空串。
            var head = Section("安装选项", "");
            AddToPage(1, head[0], head[1]);

            var y = 168;
            _optStartMenu = MakeOption("添加到开始菜单", true);
            _optStartMenu.SetBounds(PadX, y, WinWidth - PadX * 2, SetupTheme.RowHeight);
            AddToPage(1, _optStartMenu);
            y += SetupTheme.RowHeight + 12;

            _optDesktop = MakeOption("创建桌面快捷方式", true);
            _optDesktop.SetBounds(PadX, y, WinWidth - PadX * 2, SetupTheme.RowHeight);
            AddToPage(1, _optDesktop);
            y += SetupTheme.RowHeight + 12;

            _optTaskbar = MakeOption("固定到任务栏", false);
            _optTaskbar.SetBounds(PadX, y, WinWidth - PadX * 2, SetupTheme.RowHeight);
            AddToPage(1, _optTaskbar);

            var hint = new Caption(
                "任务栏固定：Windows 10 1607 之后系统不再允许安装程序固定，失败不影响其它功能。",
                8.5f, FontStyle.Regular, SetupTheme.TextFaint);
            hint.SetBounds(PadX + 2, y + SetupTheme.RowHeight + 12, WinWidth - PadX * 2, 18);
            AddToPage(1, hint);
        }

        private OptionRow MakeOption(string text, bool @checked)
        {
            var row = new OptionRow();
            row.Text = text;
            row.Checked = @checked;

            // 鼠标进出时给窗体一点"有交互"的反馈：改一下提示文字
            row.StateChanged += delegate { RefreshStatus(); };
            return row;
        }

        // ------------------------------------------------------------ 卸载页

        private void BuildUninstallPage()
        {
            var info = AppRegistry.Read();

            var head = Section("准备卸载", "确认下面的信息无误后点「开始卸载」。");
            AddToPage(0, head[0], head[1]);

            var path = new Caption(AppEnv.Root, 10f, FontStyle.Regular, SetupTheme.Text);
            path.SetBounds(PadX + 2, 156, WinWidth - PadX * 2 - 4, 20);
            AddToPage(0, path);

            var detail = new Caption(
                info.Installed
                    ? "注册表登记的版本 " + info.Version + "　·　快捷方式：" + info.ShortcutText()
                    : "注册表里没有找到本程序的安装记录（可能已经被清理过）。",
                9f, FontStyle.Regular, SetupTheme.TextDim);
            detail.SetBounds(PadX + 2, 178, WinWidth - PadX * 2 - 4, 18);
            AddToPage(0, detail);

            // ⚠️ 卸载窗口只有 430 高（安装窗口 500）。页脚从底部倒推后会占到
            // y=230 往上（_status 在 y=230），所以这一行必须停在 218 以内，
            // 否则会和"点「开始卸载」后将立即删除程序文件"那行叠在一起。
            _keepData = new CheckStrip();
            _keepData.Text = "保留地图与绘图数据（maps 目录与 drawing.json）";
            _keepData.SetBounds(PadX + 2, 208, WinWidth - PadX * 2 - 4, 26);
            AddToPage(0, _keepData);
        }

        // ------------------------------------------------------------ 步骤 / 页脚

        private void BuildFooter()
        {
            // ⚠️ 这里**必须**按当前窗口真实高度算，不能写死 WinHeightInstall。
            // 卸载窗口只有 430 高，而安装窗口 500；写死 500-62=438 的话，
            // 卸载时三个按钮（y=438, 高 40 → 底 478）整个落在 430 的可视区之外，
            // 用户看到的就是"按钮根本看不见，只有一个空窗"。
            var clientHeight = _uninstall ? WinHeightUninstall : WinHeightInstall;

            // 按钮固定贴在底部：底边留 22 的呼吸位。
            var buttonY = clientHeight - 22 - 40;

            // 进度 / 日志按"按钮上方"倒推，两个窗口自动各就各位。
            var logBottom = buttonY - 16;
            var logTop = logBottom - 88;

            var divider = new Divider();
            divider.SetBounds(PadX, logTop - 12, WinWidth - PadX * 2, 1);
            Controls.Add(divider);

            // 进度 + 日志（两页共用，点「下一步」之后才显示）
            _status = new Caption("", 9.5f, FontStyle.Regular, SetupTheme.TextDim);
            _status.SetBounds(PadX, logTop - 34, WinWidth - PadX * 2, 18);
            Controls.Add(_status);

            _progress = new ThinProgress();
            _progress.SetBounds(PadX, logTop - 12, WinWidth - PadX * 2, 6);
            _progress.Visible = false;
            Controls.Add(_progress);

            _log = new LogView();
            _log.SetBounds(PadX, logTop, WinWidth - PadX * 2, 88);
            _log.Visible = false;
            Controls.Add(_log);

            _back = new CapsuleButton();
            _back.Text = "上一步";
            _back.Primary = false;
            _back.SetBounds(PadX, buttonY, 96, 40);
            _back.Click += delegate { if (!_busy) SwitchPage(_page - 1); };
            Controls.Add(_back);

            _secondary = new CapsuleButton();
            _secondary.Text = _uninstall ? "取消" : "取消";
            _secondary.Primary = false;
            _secondary.SetBounds(WinWidth - PadX - 96 - 12 - 108, buttonY, 96, 40);
            _secondary.Click += delegate { if (!_busy) Close(); };
            Controls.Add(_secondary);

            _primary = new CapsuleButton();
            _primary.Text = _uninstall ? "开始卸载" : "下一步";
            // 卸载的主按钮也是**蓝底白字**（应用户要求）。早先给它用了 Danger 的暖红，
            // 理由是"卸载是个破坏性动作"；但实际看下来，红色在一个只有两个按钮的
            // 小窗里显得像报错，而"点下去之前还有一个确认框"已经把风险讲清楚了，
            // 按钮本身不需要再喊一遍。
            _primary.Primary = true;
            _primary.Danger = false;
            _primary.SetBounds(WinWidth - PadX - 108, buttonY, 108, 40);
            _primary.Click += delegate { OnPrimary(); };
            Controls.Add(_primary);
        }

        /// <summary>
        /// 一步步来。安装时主按钮先在页间推进、最后一页才真正开始装；
        /// 卸载只有一页，点了就直接干。
        /// </summary>
        private void OnPrimary()
        {
            if (_busy) return;

            if (_uninstall)
            {
                StartWork();
                return;
            }

            if (_page == 0)
            {
                var dir = _dirBox.Text;
                if (string.IsNullOrEmpty(dir) || dir.Trim().Length == 0)
                {
                    MessageBox.Show(this, "请先填写安装目录。", "TrpgMaps",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _chosenDir = dir;
                SwitchPage(1);
                return;
            }

            StartWork();
        }

        private void SwitchPage(int page)
        {
            if (page < 0 || page >= _pageControls.Length) return;
            _page = page;

            for (var i = 0; i < _pageControls.Length; i++)
            {
                var visible = (i == _page);
                foreach (var control in _pageControls[i])
                {
                    control.Visible = visible;
                }
            }

            // 只在多页时才显示上一步
            _back.Visible = !_uninstall && _page == 1;

            if (_uninstall) _primary.Text = "开始卸载";
            else _primary.Text = (_page == 0) ? "下一步" : "安装";

            RefreshStatus();
            Invalidate(true);
        }

        private void RefreshStatus()
        {
            if (_busy) return;

            if (_uninstall)
            {
                _status.Text = "点「开始卸载」后将立即删除程序文件。";
                return;
            }

            if (_page == 0)
            {
                _status.Text = "第 1 步 / 共 2 步　·　选择安装位置";
            }
            else
            {
                // 只留步骤提示，不再列"快捷方式：…" —— 勾了哪几条在胶囊上看得到，
                // 重复成一行文字只是噪音。
                _status.Text = "第 2 步 / 共 2 步　·　选择安装选项";
            }
        }

        // ------------------------------------------------------------ 执行

        private void StartWork()
        {
            _busy = true;
            _primary.Enabled = false;
            _back.Enabled = false;
            _secondary.Enabled = false;
            _secondary.Text = "关闭";

            // 收起页面控件，把地方让给日志
            for (var i = 0; i < _pageControls.Length; i++)
            {
                foreach (var control in _pageControls[i]) control.Visible = false;
            }

            _progress.Visible = true;
            _log.Visible = true;
            _log.ClearAll();
            _status.Text = _uninstall ? "正在卸载…" : "正在安装…";

            if (_uninstall)
            {
                if (MessageBox.Show(this,
                        "确定要卸载 TrpgMaps 吗？\r\n\r\n" +
                        (_keepData.Checked ? "地图与绘图数据会保留。" : "所有文件都会被删除。"),
                        "确认卸载", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                {
                    // 用户改主意了：把界面恢复回可操作状态
                    _busy = false;
                    _primary.Enabled = true;
                    _back.Enabled = true;
                    _secondary.Enabled = true;
                    _secondary.Text = "取消";
                    SwitchPage(_page);
                    return;
                }
            }

            try
            {
                if (_uninstall)
                {
                    SetupEngine.Uninstall(_keepData.Checked, Write, SetProgress);
                    Finish("卸载完成，窗口可以关闭了。");
                }
                else
                {
                    SetupEngine.Install(_chosenDir,
                        _optDesktop.Checked, _optStartMenu.Checked, _optTaskbar.Checked,
                        Write, SetProgress);
                    Finish("安装完成。");
                }
            }
            catch (Exception ex)
            {
                Write("");
                Write("出错了：" + ex.Message);
                AppLog.Write("安装/卸载出错", ex);

                _progress.SnapTo(1);
                _status.Text = "出错了，详情见上面的日志。";
                MessageBox.Show(this, ex.Message, "TrpgMaps",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _busy = false;
                _secondary.Enabled = true;
                _secondary.Text = "关闭";
                _primary.Visible = false;
                _secondary.Focus();
            }
        }

        private void Finish(string message)
        {
            Write("");
            Write(message);
            _progress.SnapTo(1);
            _status.Text = message;

            MessageBox.Show(this, message, "TrpgMaps",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Write(string line)
        {
            _log.Append(line);
            Application.DoEvents();     // 安装是同步的，让日志实时刷出来
        }

        private void SetProgress(int value, int total)
        {
            if (total <= 0) return;
            _progress.Value = value / (double)total;

            if (!_uninstall && value <= 6)
            {
                // 给每一步配一句人话，比"3/6"友好
                var stage = value;
                var text = stage <= 1 ? "准备安装目录…"
                         : stage == 2 ? "释放程序文件…"
                         : stage == 3 ? "设置目录权限…"
                         : stage == 4 ? "写入注册表…"
                         : stage == 5 ? "创建快捷方式…"
                         : "收尾…";
                _status.Text = text;
            }

            Application.DoEvents();
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

        // ------------------------------------------------------------ 小工具

        private void AddToPage(int page, params Control[] controls)
        {
            foreach (var control in controls)
            {
                Controls.Add(control);
                _pageControls[page].Add(control);
            }
        }

        /// <summary>页内小标题 + 一行说明。返回两个控件，交给调用方定位。</summary>
        private Caption[] Section(string title, string desc)
        {
            var head = new Caption(title, 12f, FontStyle.Bold, SetupTheme.Text);
            head.SetBounds(PadX, 112, WinWidth - PadX * 2, 24);

            var sub = new Caption(desc, 9.5f, FontStyle.Regular, SetupTheme.TextDim);
            sub.SetBounds(PadX, 138, WinWidth - PadX * 2, 20);

            return new Caption[] { head, sub };
        }

        // ------------------------------------------------------------ 界面预览（截图自检用）

        /// <summary>
        /// 摆出指定页面与选项状态，供 `/uipreview` 截图。
        ///
        /// **刻意只改视觉状态**：不碰 <c>_chosenDir</c>、不调 <c>StartWork()</c>，
        /// 所以它不可能触发任何安装动作。唯一的例外是卸载页 —— 那个页面在
        /// 构造时就会读一次注册表（只读），没有副作用。
        /// </summary>
        public void PreviewSetup(int page, int hoverRow, int checkedRow)
        {
            // 选项页的三行按固定顺序拿：0 = 开始菜单、1 = 桌面、2 = 任务栏
            var rows = new OptionRow[] { _optStartMenu, _optDesktop, _optTaskbar };

            // 三行全部上锁：外观只由本方法决定，真实鼠标的进出不再参与。
            // 否则截图会取决于抓图那一刻鼠标停在哪一行（已实测踩到）。
            for (var i = 0; i < rows.Length; i++)
            {
                if (rows[i] == null) continue;
                rows[i].PreviewLock = true;
                rows[i].PreviewHover(false);
                rows[i].Checked = false;
            }

            if (checkedRow >= 0 && checkedRow < rows.Length && rows[checkedRow] != null)
            {
                rows[checkedRow].Checked = true;
            }

            SwitchPage(page);

            if (page == 1 && hoverRow >= 0 && hoverRow < rows.Length && rows[hoverRow] != null)
            {
                rows[hoverRow].PreviewHover(true);
            }
        }

        /// <summary>
        /// 布局自检：把"控件跑出窗口"和"内容压住页脚"这两类问题变成可机读的结论。
        ///
        /// 为什么需要它：这两个问题**在截图里很难看出来** —— 控件跑出可视区时
        /// 截图只是"少了几个按钮"（看起来像故意留白），内容互相重叠在深色底上
        /// 也只是"文字有点糊"。人眼扫过去容易放过，而发出去就是用户看到
        /// "按钮根本不存在"。所以让程序自己算一遍边界并给出 PASS/FAIL。
        ///
        /// 检查两条：
        ///   1. 每个可见控件都必须完整落在客户区内（Bottom/Right 不越界）；
        ///   2. 页脚那几个共用的"运行时"控件（提示 / 进度 / 日志）不能和
        ///      当前页的内容控件在**纵向**上重叠。
        ///
        /// 返回多行文本，交给调用方写进日志。
        /// </summary>
        public string PreviewLayoutReport()
        {
            var lines = new List<string>();
            var bad = 0;
            var width = ClientSize.Width;
            var height = ClientSize.Height;

            lines.Add("  客户区 " + width + " x " + height);

            // --- 1. 越界检查 ---
            foreach (Control c in Controls)
            {
                if (!c.Visible) continue;
                var r = c.Bounds;
                if (r.Bottom > height || r.Right > width || r.Left < 0 || r.Top < 0)
                {
                    bad++;
                    lines.Add("  [越界] " + Describe(c) + "  " + Fmt(r) +
                              "  (客户区 " + width + "x" + height + ")");
                }
            }

            // --- 2. 内容 vs 页脚重叠检查 ---
            // 页脚区的起点：_status 的顶边。它往上就是页面内容的领地。
            if (_status != null && _status.Visible)
            {
                var contentBottom = -1;
                for (var i = 0; i < _pageControls.Length; i++)
                {
                    if (i != _page) continue;
                    foreach (var c in _pageControls[i])
                    {
                        if (!c.Visible) continue;
                        if (c.Bottom > contentBottom) contentBottom = c.Bottom;
                    }
                }

                if (contentBottom > _status.Top)
                {
                    bad++;
                    lines.Add("  [重叠] 第 " + (_page + 1) + " 页内容最低到 y=" + contentBottom +
                              "，压住页脚提示行 (top=" + _status.Top + ")");
                }
                else
                {
                    lines.Add("  页脚间隙 = " + (_status.Top - contentBottom) + " px");
                }
            }

            lines.Add(bad == 0 ? "  布局       = OK" : "  布局       = FAILED (" + bad + " 处)");
            return string.Join("\n", lines.ToArray());
        }

        /// <summary>
        /// 向导流程自检：证明"点一次下一步只翻页、不开始安装"。
        ///
        /// 这一条是补第十四个安装器版本踩到的坑：`Control` 默认带
        /// `ControlStyles.StandardClick`，基类 WndProc 会自己发一次 Click，
        /// 我们手动实现的那次又是第二次 —— 一次点击触发两遍 `OnPrimary()`，
        /// 于是"下一步"先翻到第 2 页、紧接着就 `StartWork()` 开装，
        /// 第 2 页完全没机会显示。
        ///
        /// 🔴 **只点一次**：第一次 `OnPrimary()` 在任何情况下都只应该翻页
        /// （第 0 页 → 第 1 页），走不到 `StartWork()`。这里不去点第二次 ——
        /// 第二次会真的开始安装，自检绝不能有装东西的副作用。
        ///
        /// 返回多行文本；出现 `FAILED` 即视为不通过。
        /// </summary>
        public string PreviewWizardFlowReport()
        {
            var lines = new List<string>();
            var bad = 0;

            // 先确认起点在第 0 页（构造函数里 SwitchPage(0) 决定了这一点）
            if (_page != 0)
            {
                bad++;
                lines.Add("  [流程] 起始页码 = " + _page + "（应为 0）");
            }

            OnPrimary();   // 等价于一次干净点击

            if (_page != 1)
            {
                bad++;
                lines.Add("  [流程] 点一次「下一步」后页码 = " + _page + "（应为 1）");
            }
            if (_busy)
            {
                bad++;
                lines.Add("  [流程] 点一次「下一步」就进入了安装状态（不该）");
            }
            if (bad == 0)
            {
                lines.Add("  点一次「下一步」   = 翻到第 2 页，未开始安装  OK");
            }

            lines.Add(bad == 0 ? "  向导流程   = OK" : "  向导流程   = FAILED (" + bad + " 处)");
            return string.Join("\n", lines.ToArray());
        }

        private static string Fmt(Rectangle r)
        {
            return "x=" + r.X + " y=" + r.Y + " w=" + r.Width + " h=" + r.Height +
                   " (right=" + r.Right + " bottom=" + r.Bottom + ")";
        }

        private static string Describe(Control c)
        {
            var name = c.GetType().Name;
            var text = c.Text;
            if (!string.IsNullOrEmpty(text))
            {
                if (text.Length > 22) text = text.Substring(0, 22) + "…";
                return name + " \"" + text + "\"";
            }
            return name;
        }

        /// <summary>
        /// 把窗口当前画面抓成 PNG。
        ///
        /// 不能用 <c>DrawToBitmap</c>：我们的界面全是自绘控件，DrawToBitmap 走的是
        /// 另一条渲染路径（它不派发 WM_PRINT），抓到的大概率是空白的背景。
        /// 用 Win32 的 PrintWindow 配合 <c>PW_RENDERFULLCONTENT</c> 才是"这块窗口
        /// 现在长什么样就抓什么样"，而且不需要窗口真的在屏幕上可见。
        /// </summary>
        public void PreviewCapture(string path)
        {
            // 先让窗口完成一次真实布局：Show 一下再立刻隐藏，句柄和绘制才会齐备。
            var wasVisible = Visible;

            // 挪到屏幕外再 Show。不这么做的话，真实鼠标可能正好落在窗口上，
            // 于是按钮进入悬浮态 —— 抓出来的图就取决于"抓图那一刻鼠标在哪"，
            // 自检结论直接失效（已实测踩到）。
            var saved = Location;
            Location = new Point(-32000, -32000);

            Show();
            Application.DoEvents();
            Update();

            var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height);
            using (var g = Graphics.FromImage(bitmap))
            {
                var hdc = g.GetHdc();
                try
                {
                    PrintWindow(Handle, hdc, PW_RENDERFULLCONTENT);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }

            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            bitmap.Dispose();

            if (!wasVisible) Hide();
            Location = saved;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        private const uint PW_RENDERFULLCONTENT = 0x00000002;

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
    }
}
