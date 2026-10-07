using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;

namespace TrpgMaps
{
    /// <summary>从注册表读回来的安装信息。</summary>
    internal sealed class InstallInfo
    {
        public bool Installed;
        public string InstallDir = string.Empty;
        public string Version = string.Empty;
        public string UninstallString = string.Empty;
        public bool ShortcutDesktop;
        public bool ShortcutStartMenu;
        public bool ShortcutTaskbar;

        /// <summary>快捷方式勾选情况的可读描述，卸载时显示用。</summary>
        public string ShortcutText()
        {
            var parts = new List<string>();
            if (ShortcutDesktop) parts.Add("桌面");
            if (ShortcutStartMenu) parts.Add("开始菜单");
            if (ShortcutTaskbar) parts.Add("任务栏");
            return parts.Count == 0 ? "无" : string.Join("、", parts.ToArray());
        }
    }

    /// <summary>
    /// 安装信息落盘处（注册表）与快捷方式。
    ///
    /// 三处注册表位置，各有各的用途：
    ///  * `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TrpgMaps`
    ///    —— **"控制面板 / 设置 → 应用"里那条记录**就靠它。没有这条就卸载不了、
    ///    也不会出现在已安装列表里。安装版的身份判定也以它为准。
    ///  * `HKLM\SOFTWARE\TrpgMaps` —— 安装时用户勾了哪些快捷方式。卸载、
    ///    "修复注册信息"（升级包里那个脚本）都要回来读它。
    ///  * `HKCU\SOFTWARE\TrpgMaps\Update` —— "不再提示此版本"。放 HKCU 是因为
    ///    写 HKLM 要管理员权限，而"别再提醒我"是用户随手点的一个动作。
    ///
    /// ⚠️ 本程序是 **32 位**的，所以在 64 位系统上访问
    /// `HKLM\SOFTWARE\...` 会被 WOW64 自动重定向到 `HKLM\SOFTWARE\Wow6432Node\...`。
    /// 这不是 bug：32 位程序的安装记录本来就该写在 Wow6432Node 下，
    /// 控制面板两边都会读。**安装程序、主程序、卸载程序三者的位数一致**，
    /// 所以读到的永远是同一份 —— 不要为了"看得见"去手工写非重定向的路径。
    /// </summary>
    internal static class AppRegistry
    {
        private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
        private const string UninstallKey = UninstallRoot + AppInfo.RegistryKey;
        private const string InstallKey = @"SOFTWARE\" + AppInfo.RegistryKey;
        private const string UpdateKey = @"SOFTWARE\" + AppInfo.RegistryKey + @"\Update";

        /// <summary>卸载程序在安装目录里的文件名（就是安装包自己拷过去的副本）。</summary>
        public const string UninstallerName = "TrpgMapsUninstall.exe";

        // ---------------------------------------------------------------- 读

        /// <summary>
        /// 读安装信息。**读不到不算错**（便携版本来就没有），所以是 bool + out 而不是抛异常。
        /// </summary>
        public static InstallInfo Read()
        {
            var info = new InstallInfo();

            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(UninstallKey, false))
                {
                    if (key != null)
                    {
                        info.Installed = true;
                        info.InstallDir = Convert.ToString(key.GetValue("InstallLocation")) ?? string.Empty;
                        info.Version = Convert.ToString(key.GetValue("DisplayVersion")) ?? string.Empty;
                        info.UninstallString = Convert.ToString(key.GetValue("UninstallString")) ?? string.Empty;
                    }
                }

                using (var key = Registry.LocalMachine.OpenSubKey(InstallKey, false))
                {
                    if (key != null)
                    {
                        info.ShortcutDesktop = Convert.ToInt32(key.GetValue("ShortcutDesktop", 0)) != 0;
                        info.ShortcutStartMenu = Convert.ToInt32(key.GetValue("ShortcutStartMenu", 0)) != 0;
                        info.ShortcutTaskbar = Convert.ToInt32(key.GetValue("ShortcutTaskbar", 0)) != 0;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("读取安装信息失败（按便携版处理）", ex);
            }

            if (string.IsNullOrEmpty(info.InstallDir)) info.Installed = false;
            return info;
        }

        /// <summary>
        /// 判断"当前这份 exe 是不是装在注册过的那台机器上那份"。
        ///
        /// 只看注册表里有没有记录是不够的：用户完全可能把安装目录整个拷到 U 盘上跑。
        /// 那种情况应当按**便携版**升级（不碰注册表），否则会去改一堆指向不存在目录的键。
        /// 所以还要比一次目录。
        /// </summary>
        public static bool IsInstalledAt(string exeDir)
        {
            var info = Read();
            if (!info.Installed) return false;
            return SamePath(info.InstallDir, exeDir);
        }

        public static string GetSkippedVersion()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(UpdateKey, false))
                {
                    if (key == null) return string.Empty;
                    return Convert.ToString(key.GetValue("SkippedVersion")) ?? string.Empty;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        public static void SetSkippedVersion(string version)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(UpdateKey))
                {
                    if (key == null) return;
                    if (string.IsNullOrEmpty(version)) key.DeleteValue("SkippedVersion", false);
                    else key.SetValue("SkippedVersion", version, RegistryValueKind.String);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("记录「不再提示」的版本号失败", ex);
            }
        }

        // ---------------------------------------------------------------- 写

        /// <summary>
        /// 写入全部安装信息。安装程序调用；主程序的 `--repair-registry` 也调用
        /// （升级包会把新版本号带进来，所以这里必须传参、不能读 <see cref="AppInfo.Version"/>）。
        /// </summary>
        public static void WriteInstall(string installDir, string version, bool desktop, bool startMenu, bool taskbar)
        {
            var exePath = Path.Combine(installDir, AppInfo.Name + ".exe");
            var uninstallerPath = Path.Combine(installDir, UninstallerName);

            using (var key = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                if (key != null)
                {
                    key.SetValue("DisplayName", AppInfo.Name, RegistryValueKind.String);
                    key.SetValue("DisplayVersion", version, RegistryValueKind.String);
                    key.SetValue("Publisher", AppInfo.Publisher, RegistryValueKind.String);
                    key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                    key.SetValue("DisplayIcon", exePath, RegistryValueKind.String);
                    key.SetValue("UninstallString", Quote(uninstallerPath) + " /uninstall", RegistryValueKind.String);
                    key.SetValue("QuietUninstallString", Quote(uninstallerPath) + " /uninstall /quiet", RegistryValueKind.String);
                    key.SetValue("URLInfoAbout", AppInfo.GiteeRepo, RegistryValueKind.String);
                    key.SetValue("URLUpdateInfo", AppInfo.GiteeRepo + "/releases", RegistryValueKind.String);
                    key.SetValue("HelpLink", AppInfo.GitHubRepo, RegistryValueKind.String);
                    key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

                    var major = 0;
                    var minor = 0;
                    var parts = version.Split('.');
                    if (parts.Length > 0) int.TryParse(parts[0], out major);
                    if (parts.Length > 1) int.TryParse(parts[1], out minor);
                    key.SetValue("VersionMajor", major, RegistryValueKind.DWord);
                    key.SetValue("VersionMinor", minor, RegistryValueKind.DWord);

                    var sizeKb = DirectorySizeKb(installDir);
                    if (sizeKb > 0) key.SetValue("EstimatedSize", sizeKb, RegistryValueKind.DWord);
                }
            }

            using (var key = Registry.LocalMachine.CreateSubKey(InstallKey))
            {
                if (key != null)
                {
                    key.SetValue("InstallDir", installDir, RegistryValueKind.String);
                    key.SetValue("Version", version, RegistryValueKind.String);
                    key.SetValue("Publisher", AppInfo.Publisher, RegistryValueKind.String);
                    key.SetValue("ShortcutDesktop", desktop ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("ShortcutStartMenu", startMenu ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("ShortcutTaskbar", taskbar ? 1 : 0, RegistryValueKind.DWord);
                }
            }
        }

        /// <summary>卸载时把注册表痕迹清干净。卸载记录放最后删。</summary>
        public static void RemoveInstall()
        {
            TryDeleteKey(InstallKey);
            TryDeleteKey(UninstallKey);
        }

        private static void TryDeleteKey(string path)
        {
            try
            {
                // ⚠️ .NET 3.5 的 `DeleteSubKeyTree` **只有一个参数**（`throwOnMissingSubKey`
                // 那个重载是 4.0 才加的）。这里手动判断存在性 + 吞掉异常。
                using (var parent = Registry.LocalMachine.OpenSubKey(PathOfParent(path)))
                {
                    if (parent == null) return;
                    if (parent.OpenSubKey(LeafOf(path)) == null) return;
                }

                Registry.LocalMachine.DeleteSubKeyTree(path);
            }
            catch (Exception ex)
            {
                AppLog.Write("删除注册表项失败：" + path, ex);
            }
        }

        private static string PathOfParent(string path)
        {
            var index = path.LastIndexOf('\\');
            return index <= 0 ? path : path.Substring(0, index);
        }

        private static string LeafOf(string path)
        {
            var index = path.LastIndexOf('\\');
            return index < 0 ? path : path.Substring(index + 1);
        }

        /// <summary>
        /// 按注册表里记的勾选情况重建快捷方式。
        /// 升级包里的 `修复注册信息.bat` 会调 `TrpgMaps.exe --repair-registry` 走到这里：
        /// 升级只替换文件、目录没变，所以快捷方式原本就还有效，这一步主要是
        /// "用户手贱删了快捷方式"和"版本号变了"两种情况能自愈。
        /// </summary>
        public static void RepairShortcuts(string installDir)
        {
            var info = Read();
            Shortcuts.RemoveAll(installDir);

            if (info.ShortcutDesktop) Shortcuts.CreateDesktop(installDir);
            if (info.ShortcutStartMenu) Shortcuts.CreateStartMenu(installDir);
            if (info.ShortcutTaskbar) Shortcuts.PinTaskbar(installDir);
        }

        /// <summary>
        /// 把安装信息整体刷成"当前这份程序 + <paramref name="version"/>"，然后重建快捷方式。
        ///
        /// 快捷方式的勾选情况**从注册表里读回来再原样写回去**：
        /// 升级时不应该因为"新版本"就把用户当初没勾的开始菜单快捷方式给勾上。
        /// </summary>
        public static void Repair(string installDir, string version)
        {
            var info = Read();
            WriteInstall(installDir, version, info.ShortcutDesktop, info.ShortcutStartMenu, info.ShortcutTaskbar);
            RepairShortcuts(installDir);
        }

        // ---------------------------------------------------------------- 小工具

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                var na = Path.GetFullPath(a).TrimEnd('\\', '/');
                var nb = Path.GetFullPath(b).TrimEnd('\\', '/');
                return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string Quote(string path)
        {
            return "\"" + path + "\"";
        }

        private static int DirectorySizeKb(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return 0;
                long total = 0;
                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; }
                    catch { }
                }
                return (int)Math.Min(int.MaxValue, total / 1024);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 当前进程是不是管理员。安装/卸载要写 HKLM，必须提权。
        /// 用 `WindowsPrincipal` 而不是"试着写一次 HKLM"——后者会在事件日志里留一堆
        /// 拒绝访问的噪音，而且有副作用。
        /// </summary>
        public static bool IsElevated
        {
            get
            {
                try
                {
                    var identity = WindowsIdentity.GetCurrent();
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    /// <summary>
    /// 快捷方式（桌面 / 开始菜单 / 任务栏）。
    ///
    /// 创建 `.lnk` 走的是 `WScript.Shell` 的 COM 自动化（**后期绑定**）。
    /// 不引 `Interop.IWshRuntimeLibrary`：那会多出一个互操作 DLL，
    /// 而本工程的分发包里必须一个第三方 DLL 都没有。
    /// </summary>
    internal static class Shortcuts
    {
        private const string LinkName = AppInfo.Name + ".lnk";

        public static string DesktopPath
        {
            // "所有用户"的桌面。装在 Program Files 里的程序就该给所有用户建快捷方式，
            // 而且安装程序是提权跑的，用"当前用户桌面"在"管理员替别人装"的场景下
            // 会建到管理员自己的桌面上。
            get
            {
                return KnownFolder(CSIDL_COMMON_DESKTOPDIRECTORY,
                    Path.Combine(PublicRoot, "Desktop"));
            }
        }

        public static string StartMenuFolder
        {
            get
            {
                var programs = KnownFolder(CSIDL_COMMON_PROGRAMS,
                    Path.Combine(Path.Combine(CommonAppData, "Microsoft"),
                                 @"Windows\Start Menu\Programs"));
                return Path.Combine(programs, AppInfo.Name);
            }
        }

        /// <summary>
        /// 为什么不用 `Environment.SpecialFolder.CommonDesktopDirectory`：
        /// 那两个枚举成员（CommonDesktopDirectory / CommonPrograms）在 .NET 3.5 的
        /// 参考程序集里**根本不存在**（是 4.0 才补上的），拿它编译直接 CS0117。
        ///
        /// 所以走 `SHGetFolderPath`（shell32，Win2000 起就有），这才是"所有用户桌面 /
        /// 所有用户开始菜单"的官方取值方式 —— 而且它不受 WOW64 重定向影响，
        /// 返回的是物理路径；中英文 Windows 的物理路径都是英文，
        /// 不像"桌面"这两个字那样会被本地化。
        /// </summary>
        private const int CSIDL_COMMON_PROGRAMS = 0x0017;
        private const int CSIDL_COMMON_DESKTOPDIRECTORY = 0x0019;

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SHGetFolderPath(IntPtr hwndOwner, int nFolder, IntPtr hToken,
            uint dwFlags, System.Text.StringBuilder pszPath);

        private static string KnownFolder(int csidl, string fallback)
        {
            try
            {
                var buffer = new System.Text.StringBuilder(260);
                var hr = SHGetFolderPath(IntPtr.Zero, csidl, IntPtr.Zero, 0, buffer);
                if (hr == 0 && buffer.Length > 0) return buffer.ToString();
            }
            catch (Exception ex)
            {
                AppLog.Write("SHGetFolderPath 失败，改用兜底路径", ex);
            }
            return fallback;
        }

        /// <summary>`%PUBLIC%`；理论上一定存在，取不到就按惯例退到 C:\Users\Public。</summary>
        private static string PublicRoot
        {
            get
            {
                var value = Environment.GetEnvironmentVariable("PUBLIC");
                return string.IsNullOrEmpty(value) ? @"C:\Users\Public" : value;
            }
        }

        private static string CommonAppData
        {
            get
            {
                var value = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                return string.IsNullOrEmpty(value) ? @"C:\ProgramData" : value;
            }
        }

        private static string StartMenuLnk
        {
            get { return Path.Combine(StartMenuFolder, LinkName); }
        }

        private static string DesktopLnk
        {
            get { return Path.Combine(DesktopPath, LinkName); }
        }

        /// <summary>任务栏固定项真正落盘的位置（Win7 起的 User Pinned 目录）。</summary>
        private static string UserPinnedTaskBar
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            }
        }

        public static bool CreateDesktop(string installDir)
        {
            return CreateLink(DesktopLnk, installDir);
        }

        public static bool CreateStartMenu(string installDir)
        {
            try
            {
                Directory.CreateDirectory(StartMenuFolder);
                return CreateLink(StartMenuLnk, installDir);
            }
            catch (Exception ex)
            {
                AppLog.Write("创建开始菜单快捷方式失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 固定到任务栏。
        ///
        /// ⚠️ **这是个"尽力而为"的操作，微软从来没有正式支持过脚本固定任务栏。**
        /// Win7/Win8 上 `taskbarpin` 这个 shell 动词是可用的；**Win10 1607 之后
        /// 微软把它删掉了**（防止安装程序乱钉图标），动词会静默失败。
        /// 所以这里是两步：
        ///   1. 先试 shell 动词；
        ///   2. 不行就直接把 .lnk 拷进 User Pinned\TaskBar —— 有些系统上
        ///      "重启一次资源管理器"就能看到（新建的进程会重新读这个目录）。
        /// 无论哪条路成功，都不影响其它功能；失败只记日志，不打断安装。
        /// </summary>
        public static bool PinTaskbar(string installDir)
        {
            var lnk = DesktopLnk;
            if (!File.Exists(lnk))
            {
                // 没勾桌面快捷方式时也要有个 .lnk 能拿去做固定
                var temp = Path.Combine(Path.GetTempPath(), LinkName);
                if (!CreateLink(temp, installDir)) return false;
                lnk = temp;
            }

            if (TryInvokeVerb(lnk, "taskbarpin")) return true;

            try
            {
                Directory.CreateDirectory(UserPinnedTaskBar);
                var target = Path.Combine(UserPinnedTaskBar, LinkName);
                File.Copy(lnk, target, true);
                AppLog.Write("任务栏固定：shell 动词不可用（Win10 1607+ 已移除），"
                           + "已把快捷方式放入 User Pinned\\TaskBar，需重启资源管理器后生效。");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write("任务栏固定失败（不影响其它功能）", ex);
                return false;
            }
        }

        /// <summary>用 Shell.Application 触发一个 shell 动词，例如 `taskbarpin`。</summary>
        private static bool TryInvokeVerb(string lnkPath, string verb)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application", false);
                if (shellType == null) return false;

                var shell = Activator.CreateInstance(shellType);
                try
                {
                    var dir = Path.GetDirectoryName(lnkPath);
                    var file = Path.GetFileName(lnkPath);

                    var folder = shellType.InvokeMember("NameSpace", BindingFlags.InvokeMethod,
                        null, shell, new object[] { dir });
                    if (folder == null) return false;

                    var item = folder.GetType().InvokeMember("ParseName", BindingFlags.InvokeMethod,
                        null, folder, new object[] { file });
                    if (item == null) return false;

                    item.GetType().InvokeMember("InvokeVerb", BindingFlags.InvokeMethod,
                        null, item, new object[] { verb });
                    return true;
                }
                finally
                {
                    ReleaseCom(shell);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("触发 shell 动词 " + verb + " 失败", ex);
                return false;
            }
        }

        /// <summary>把桌面 / 开始菜单 / 任务栏三处快捷方式都清掉（卸载与"重建"前调用）。</summary>
        public static void RemoveAll(string installDir)
        {
            TryDelete(DesktopLnk);
            TryDelete(StartMenuLnk);
            try
            {
                if (Directory.Exists(StartMenuFolder) &&
                    Directory.GetFileSystemEntries(StartMenuFolder).Length == 0)
                {
                    Directory.Delete(StartMenuFolder, false);
                }
            }
            catch { }

            TryDelete(Path.Combine(UserPinnedTaskBar, LinkName));
        }

        /// <summary>
        /// 造一个 .lnk。目标就是安装目录里的 TrpgMaps.exe，工作目录设成安装目录 ——
        /// `.lnk` 不设工作目录时，Win7 会用 `%WINDIR%\System32` 起进程，
        /// 一旦程序里有用到相对路径的代码就会找不到文件。
        /// </summary>
        private static bool CreateLink(string lnkPath, string installDir)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell", false);
                if (shellType == null)
                {
                    AppLog.Write("创建快捷方式失败：系统里没有 WScript.Shell。");
                    return false;
                }

                var shell = Activator.CreateInstance(shellType);
                try
                {
                    var link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                        null, shell, new object[] { lnkPath });

                    var linkType = link.GetType();
                    var exePath = Path.Combine(installDir, AppInfo.Name + ".exe");

                    linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { exePath });
                    linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { installDir });
                    linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link,
                        new object[] { exePath + ",0" });
                    linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link,
                        new object[] { "TrpgMaps " + AppInfo.VersionText + " —— 跑团地图投屏工具" });
                    linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);

                    return true;
                }
                finally
                {
                    ReleaseCom(shell);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("创建快捷方式失败：" + lnkPath, ex);
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                AppLog.Write("删除快捷方式失败：" + path, ex);
            }
        }

        private static void ReleaseCom(object obj)
        {
            try
            {
                if (obj != null && System.Runtime.InteropServices.Marshal.IsComObject(obj))
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(obj);
                }
            }
            catch { }
        }
    }
}
