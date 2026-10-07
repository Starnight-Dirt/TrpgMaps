using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrpgMaps
{
    /// <summary>本程序在 Windows 防火墙里的入站状态。</summary>
    internal enum FirewallState
    {
        /// <summary>读不到规则表（权限/异常），无从判断。</summary>
        Unknown,

        /// <summary>有入站放行规则，手机可以连进来。</summary>
        Allowed,

        /// <summary>有入站"阻止"规则 —— 手机一定连不上，而且系统不会再弹窗问你。</summary>
        Blocked,

        /// <summary>没有任何针对本程序的规则：首次绑端口时系统会弹一次授权框。</summary>
        NoRule
    }

    internal sealed class FirewallReport
    {
        public FirewallState State = FirewallState.Unknown;
        public string ExePath = string.Empty;
        public readonly List<string> AllowRules = new List<string>();
        public readonly List<string> BlockRules = new List<string>();
        public string Error;

        /// <summary>false 表示"局域网访问没被放行"，界面上该提示用户。</summary>
        public bool IsOpen
        {
            get { return State == FirewallState.Allowed; }
        }
    }

    /// <summary>
    /// 查（并在需要时修）Windows 防火墙里针对本程序的入站规则。
    ///
    /// 为什么需要这个模块 —— 一次真实的故障：
    ///   本程序的窗口是全屏 + 置顶的（给 DM 投屏用），而 Windows 的
    ///   "是否允许此应用通过防火墙通信"授权框是个普通窗口，会被压在全屏界面底下。
    ///   用户看不到它，随手一按（Esc / 取消）就把它关掉了 ——
    ///   这时 Windows **不是**"什么都不做"，而是写下一条**永久的入站阻止规则**，
    ///   并且**以后再也不问**。表现就是：DM 端一切正常、二维码也对，
    ///   但手机打开网页永远超时。同机器上其它程序（比如 MAUI 版，当初点了"允许"）
    ///   完全不受影响，于是极易被误判成"网络问题"。
    ///
    /// 判断方式是直接读注册表里的规则表，不需要管理员权限：
    ///   HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules
    /// 每条规则是一串以 '|' 分隔的 Key=Value，其中 App= 就是程序完整路径。
    /// （不用 HNetCfg.FwPolicy2 COM：读它要额外权限，而且同样的信息注册表里就有。）
    ///
    /// 修复要改防火墙，必须管理员权限，所以走 UAC 重新拉起自己（--fix-firewall）。
    /// </summary>
    internal static class FirewallCheck
    {
        private const string RulesKey =
            @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

        /// <summary>Windows 自动为自动创建规则取的名字（和 exe 的文件描述一致）。</summary>
        private const string RuleName = "TrpgMaps";

        public static string ExePath
        {
            get
            {
                try { return Path.GetFullPath(Application.ExecutablePath); }
                catch { return Application.ExecutablePath; }
            }
        }

        // ============================================================
        //  检查
        // ============================================================

        public static FirewallReport Inspect()
        {
            var report = new FirewallReport();
            report.ExePath = ExePath;

            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(RulesKey))
                {
                    if (key == null)
                    {
                        report.Error = "打不开防火墙规则表";
                        return report;
                    }

                    var names = key.GetValueNames();
                    for (var i = 0; i < names.Length; i++)
                    {
                        var value = key.GetValue(names[i]) as string;
                        if (string.IsNullOrEmpty(value)) continue;

                        var fields = ParseRule(value);

                        string app;
                        if (!fields.TryGetValue("App", out app)) continue;
                        if (!SamePath(app, report.ExePath)) continue;
                        if (!IsTrue(fields, "Active")) continue;

                        string dir;
                        fields.TryGetValue("Dir", out dir);
                        if (!string.Equals(dir, "In", StringComparison.OrdinalIgnoreCase)) continue;

                        string action;
                        fields.TryGetValue("Action", out action);
                        if (string.Equals(action, "Block", StringComparison.OrdinalIgnoreCase))
                            report.BlockRules.Add(names[i]);
                        else if (string.Equals(action, "Allow", StringComparison.OrdinalIgnoreCase))
                            report.AllowRules.Add(names[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                report.Error = ex.Message;
                return report;
            }

            // 防火墙的判定顺序：**阻止优先于允许**。
            // 所以只要存在一条启用的入站阻止规则，就算同时有放行规则也一样连不上。
            if (report.BlockRules.Count > 0) report.State = FirewallState.Blocked;
            else if (report.AllowRules.Count > 0) report.State = FirewallState.Allowed;
            else report.State = FirewallState.NoRule;

            return report;
        }

        public static string Describe(FirewallReport report)
        {
            if (report == null) return "（未检查）";

            string state;
            switch (report.State)
            {
                case FirewallState.Allowed: state = "已放行"; break;
                case FirewallState.Blocked: state = "被阻止（手机一定连不上）"; break;
                case FirewallState.NoRule: state = "无规则（首次监听时系统会弹一次授权框）"; break;
                default: state = "无法判断"; break;
            }

            var text = state
                + " | 入站放行=" + report.AllowRules.Count
                + " 入站阻止=" + report.BlockRules.Count
                + " | 程序=" + report.ExePath;

            if (!string.IsNullOrEmpty(report.Error)) text += " | 读取失败：" + report.Error;
            return text;
        }

        private static Dictionary<string, string> ParseRule(string value)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(value)) return map;

            var parts = value.Split('|');
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.Length == 0) continue;

                var eq = part.IndexOf('=');
                if (eq <= 0) continue;                  // 开头的 "v2.10" 这类版本标记没有 '='

                var name = part.Substring(0, eq).Trim();
                var text = part.Substring(eq + 1).Trim();
                if (!map.ContainsKey(name)) map[name] = text;   // Profile 会出现两次，留第一个就够
            }
            return map;
        }

        private static bool IsTrue(Dictionary<string, string> fields, string name)
        {
            string value;
            if (!fields.TryGetValue(name, out value)) return false;
            return string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ============================================================
        //  修复（需要管理员）
        // ============================================================

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

        /// <summary>
        /// 删掉针对本程序的入站阻止规则，重新加两条入站放行（TCP + UDP）。
        /// 必须在管理员身份下调用。返回 true 表示事后复查确实是"已放行"。
        /// </summary>
        public static bool Repair(out string log)
        {
            var sb = new StringBuilder();
            sb.Append("程序：").Append(ExePath).Append(Environment.NewLine);
            sb.Append("管理员：").Append(IsElevated ? "是" : "否").Append(Environment.NewLine);

            var before = Inspect();
            sb.Append("修复前：").Append(Describe(before)).Append(Environment.NewLine);

            // 1) 先按名字删。Windows 自动写下的规则都叫这个名字，
            //    用名字删可以一并清掉"以前在别的目录跑过"留下的僵尸阻止规则
            //    （比如双目标时代的 bin\Release-net35\ 那份）。
            RunNetsh("advfirewall firewall delete rule name=\"" + RuleName + "\"", sb);

            // 2) 再按程序路径兜一遍：万一还有别的名字的规则压着我们。
            RunNetsh("advfirewall firewall delete rule name=all dir=in program=\"" + ExePath + "\"", sb);

            // 3) 加两条入站放行。
            //    TCP 是网页真正要用的；UDP 一起加是为了以后不再触发系统的"是否允许通信"询问
            //    （Windows 自己也是 TCP/UDP 成对建的）。
            //    profile 同时给 private 和 public：家里 WiFi 被标成"公用网络"的情况太常见了。
            RunNetsh("advfirewall firewall add rule name=\"" + RuleName + "\""
                + " dir=in action=allow program=\"" + ExePath + "\""
                + " enable=yes profile=private,public protocol=TCP", sb);
            RunNetsh("advfirewall firewall add rule name=\"" + RuleName + "\""
                + " dir=in action=allow program=\"" + ExePath + "\""
                + " enable=yes profile=private,public protocol=UDP", sb);

            var after = Inspect();
            sb.Append("修复后：").Append(Describe(after)).Append(Environment.NewLine);

            log = sb.ToString();
            return after.State == FirewallState.Allowed;
        }

        private static void RunNetsh(string arguments, StringBuilder sb)
        {
            sb.Append("> netsh ").Append(arguments).Append(Environment.NewLine);
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(Environment.SystemDirectory, "netsh.exe");
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;      // 只收 stdout：两个都收在 .NET 3.5 下有死锁风险

                using (var process = Process.Start(psi))
                {
                    var text = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    if (!string.IsNullOrEmpty(text)) sb.Append(text.Trim()).Append(Environment.NewLine);
                    sb.Append("  exit=").Append(process.ExitCode).Append(Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                sb.Append("  执行失败：").Append(ex.Message).Append(Environment.NewLine);
            }
        }

        /// <summary>
        /// 弹一次 UAC，用管理员身份重开自己跑 --fix-firewall，等它跑完。
        /// 返回 false 时 message 里是给用户看的原因（比如"你点了否"）。
        /// </summary>
        public static bool RunElevatedRepair(out string message)
        {
            message = string.Empty;
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.Arguments = "--fix-firewall --quiet";
                psi.UseShellExecute = true;
                psi.Verb = "runas";

                using (var process = Process.Start(psi))
                {
                    process.WaitForExit();
                    if (process.ExitCode == 0) return true;
                    message = "修复没有成功（退出码 " + process.ExitCode + "），详情见 app.log。";
                    return false;
                }
            }
            catch (Win32Exception)
            {
                // UAC 被点了"否"
                message = "已取消管理员授权，防火墙规则没有改动。";
                return false;
            }
            catch (Exception ex)
            {
                message = "无法启动管理员进程：" + ex.Message;
                return false;
            }
        }
    }
}
