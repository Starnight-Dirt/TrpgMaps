using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace TrpgMaps
{
    /// <summary>
    /// 排错用的小工具：把 <see cref="ImageLoader"/> 解出来的一张图原样存成 PNG。
    ///
    /// 为什么需要它：自研 WebP 解码器是纯托管的，出了问题（颜色不对、透明通道没出来）
    /// 光看程序里的表现很难定位。有了这个入口就能把它解出来的像素**原样导出**，
    /// 再跟 libwebp（Pillow / cwebp）的结果逐像素比 —— 差在哪一行、哪个通道，一目了然。
    ///
    /// 用法：TrpgMaps.exe --dump-webp &lt;输入.webp&gt; &lt;输出.png&gt;
    /// 退出码：0 = 成功，2 = 读/解码失败（原因打到 stderr 和 app.log）。
    /// </summary>
    internal static class DumpWebp
    {
        public static int Run(string input, string output)
        {
            try
            {
                using (var image = ImageLoader.Load(input))
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(output));
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    // 一律存成 32bppArgb PNG，alpha 原样保留（这正是要检查的东西）
                    using (var bmp = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                            g.DrawImage(image, 0, 0, image.Width, image.Height);
                        }
                        bmp.Save(output, ImageFormat.Png);
                    }
                    Console.WriteLine("OK " + image.Width + "x" + image.Height + " -> " + output);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message);
                AppLog.Write("--dump-webp 失败：" + input, ex);
                return 2;
            }
        }
    }
}
