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
        /// <summary>默认监听端口（与原 Python 版一致）。</summary>
        public const int DefaultPort = 5000;

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
