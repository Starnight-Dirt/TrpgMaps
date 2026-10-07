using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace TrpgMaps
{
    /// <summary>
    /// 图片加载的统一入口。
    ///
    /// 为什么要这么一层：GDI+（<c>System.Drawing</c>）**不认 WebP** ——
    /// 从 Win7 到 Win11 都不认，`Image.FromStream` 遇到 .webp 直接抛异常。
    /// 底图一旦换成 webp，`MapCanvas` 就只画得出底色（看起来就是"全黑"），
    /// 缩略图是白底红叉，色调分析也回落到默认值。
    ///
    /// 所以这里按**文件头**（不是扩展名）分派：
    ///   * 是 WebP → 交给自研的 <see cref="Vp8FrameDecoder"/>（纯托管，零第三方 DLL）；
    ///   * 其它格式 → 原封不动走 GDI+，行为与以前完全一致。
    ///
    /// 返回的 <see cref="Image"/> **自成一体**：不依赖任何还开着的流，
    /// 调用方直接 <c>using</c> 包掉就行。这一条很重要，历史上有两个坑都出在这：
    /// 一是 <c>Image.FromStream</c> 的图在流关掉之后才真正去读数据，
    /// 二是 <c>Image.GetThumbnailImage</c> 的返回值依附源图（源图一 Dispose 就变白底红叉）。
    /// </summary>
    internal static class ImageLoader
    {
        /// <summary>文件头是不是 RIFF/WEBP（只要是 WebP 容器就算，具体哪种编码交给解码器去分辨）。</summary>
        public static bool IsWebpData(byte[] data)
        {
            return data != null
                && data.Length >= 16
                && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
                && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P';
        }

        /// <summary>只按扩展名判断（给"要不要走自研解码器"的日志/统计用，真正的分派看文件头）。</summary>
        public static bool HasWebpExtension(string path)
        {
            return !string.IsNullOrEmpty(path)
                && path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>加载一张图。返回的图自成一体，调用方负责 Dispose。失败抛异常。</summary>
        public static Image Load(string path)
        {
            var data = File.ReadAllBytes(path);

            if (IsWebpData(data))
                return DecodeWebp(data);

            using (var ms = new MemoryStream(data, false))
            using (var image = Image.FromStream(ms, false, false))
            {
                // 拷一份出来，这样 ms / image 出了 using 也不会把结果带走
                return new Bitmap(image);
            }
        }

        /// <summary>加载失败不抛异常，返回 false 并把原因带出来。</summary>
        public static bool TryLoad(string path, out Image image, out string error)
        {
            image = null;
            error = null;
            try
            {
                image = Load(path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>把自研 VP8 解码器的 BGRA 缓冲包成一张位图。</summary>
        private static Bitmap DecodeWebp(byte[] data)
        {
            int width, height;
            var bgra = Vp8FrameDecoder.DecodeWebp(data, out width, out height);
            return FromBgra(bgra, width, height);
        }

        /// <summary>
        /// BGRA 缓冲 → 位图。**逐行拷**而不是一次性 Marshal.Copy：
        /// <c>LockBits</c> 给的 Scan0 每行还带 padding（Stride ≥ 宽×4），
        /// 真按连续内存整块拷进去，宽不是 4 的倍数时画面会从第二行起整体错位。
        /// </summary>
        private static Bitmap FromBgra(byte[] bgra, int width, int height)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var bits = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var stride = bits.Stride;
                var rowBytes = width * 4;
                // GDI+ 的位图也可能是自下而上存的（Stride 为负），别想当然
                var rowStep = stride < 0 ? -stride : stride;
                var firstRow = stride < 0 ? height - 1 : 0;
                var delta = stride < 0 ? -1 : 1;

                var row = firstRow;
                for (var y = 0; y < height; y++)
                {
                    var dst = (IntPtr)(bits.Scan0.ToInt64() + (long)row * rowStep);
                    Marshal.Copy(bgra, y * rowBytes, dst, rowBytes);
                    row += delta;
                }
            }
            finally
            {
                bmp.UnlockBits(bits);
            }
            return bmp;
        }
    }
}
