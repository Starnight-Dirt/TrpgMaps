using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace TrpgMaps
{
    /// <summary>
    /// 程序固定路径与运行参数。以 exe 所在目录为根，绿色部署：
    /// 整个输出目录拷到别的机器即可运行。
    /// </summary>
    internal static class AppEnv
    {
        /// <summary>
        /// 默认监听端口。
        ///
        /// 为什么不是 5000 / 8080：
        ///  - 5000 在老机器上常被别的程序占；更麻烦的是 Windows 给 Hyper-V / WSL / Docker 的
        ///    `winnat` **预留**了一大段端口（`netsh int ipv4 show excludedportrange protocol=tcp`
        ///    可查），实测这台机器就是 **4929–5028** 被圈走 —— 那种"占用"没有占用者可以杀，
        ///    端口放行也救不回来，只能换号；
        ///  - 80 / 443 / 8080 / 8443 这些是**国内未备案域名会被运营商直接阻断**的端口，
        ///    以后想把服务挂到公网上时不能用。
        ///
        /// 所以挑一个五位、非 Web 惯例的号。绑不上也不怕：启动时会顺着往后找，
        /// 直到找到一个真正绑得上的端口（见 <see cref="PortPicker"/>）。
        /// </summary>
        public const int DefaultPort = 20020;

        /// <summary>每厘米的像素基准（与原版一致：37.8 像素 = 1 厘米）。</summary>
        public const float PixelsPerCm = 37.8f;

        public static readonly string Root = ResolveRoot();
        public static readonly string MapsFolder = Path.Combine(Root, "maps");
        public static readonly string WebRoot = Path.Combine(Root, "wwwroot");

        /// <summary>允许上传的图片扩展名。</summary>
        private static readonly HashSet<string> AllowedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "png", "jpg", "jpeg", "gif", "webp", "bmp"
            };

        public static bool IsAllowedImage(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            var ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext)) return false;
            return AllowedExtensions.Contains(ext.TrimStart('.'));
        }

        private static string ResolveRoot()
        {
            try
            {
                var exe = Application.ExecutablePath;
                if (!string.IsNullOrEmpty(exe))
                {
                    var dir = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
            }
            catch
            {
                // 忽略，退回到当前目录
            }
            return Directory.GetCurrentDirectory();
        }
    }

    /// <summary>极简文件日志，排错用。</summary>
    internal static class AppLog
    {
        private static readonly object Gate = new object();

        public static string LogFile
        {
            get { return Path.Combine(AppEnv.Root, "app.log"); }
        }

        public static void Write(string message)
        {
            Write(message, null);
        }

        public static void Write(string message, Exception ex)
        {
            var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message;
            if (ex != null) line += Environment.NewLine + ex;

            Debug.WriteLine(line);
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(LogFile, line + Environment.NewLine);
                }
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
    }
}
