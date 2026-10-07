using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace TrpgMaps
{
    /// <summary>一次色调采样的结果。</summary>
    internal struct ToneSample
    {
        /// <summary>采样是否成功（文件不存在 / 解码失败时为 false）。</summary>
        public bool Ok;

        /// <summary>平均相对亮度，0（纯黑）～1（纯白）。</summary>
        public double Mean;

        /// <summary>明显偏暗（亮度 &lt; 0.35）的像素占比。</summary>
        public double DarkFraction;

        /// <summary>明显偏亮（亮度 &gt; 0.65）的像素占比。</summary>
        public double LightFraction;

        /// <summary>本次解析耗时（毫秒）。</summary>
        public long ElapsedMs;

        /// <summary>这个结果是不是直接从缓存里拿的（true 时 <see cref="ElapsedMs"/> 是首次那次的耗时）。</summary>
        public bool FromCache;
    }

    /// <summary>
    /// 底图色调分析。它只回答一个问题：**这块底图上的网格线该用纯白还是纯黑。**
    ///
    /// 做法是把底图缩成 32×32（1024 个采样点）后求平均亮度：
    ///
    ///   * 平均亮度 ≥ <see cref="Threshold"/>（底图整体偏亮）→ 用**纯黑**网格线；
    ///   * 平均亮度 &lt;  <see cref="Threshold"/>（底图整体偏暗）→ 用**纯白**网格线。
    ///
    /// 一句话概括：**让网格线和底图的主要色调反着来**，这样才看得清。
    ///
    /// 为什么不用"逐像素自适应"（哪块亮就用黑、哪块暗就用白）：
    /// 那样网格线会变成断断续续的斑马线，反而看不出格子的连续性 —— 网格是给
    /// 桌面量距离用的，**连贯**比单点对比度更重要。所以只做一次全局判定，
    /// 真遇到判定不合适的图，面板上有手动开关（白 / 黑）兜底。
    ///
    /// 结果按（文件长度 + 最后写入时间）缓存：切底图时不会每次都重新解码。
    /// </summary>
    internal static class MapTone
    {
        /// <summary>采样网格边长。32×32 = 1024 个点足够代表整图色调。</summary>
        private const int SampleSize = 32;

        /// <summary>平均亮度分界：≥ 此值用黑线，&lt; 此值用白线。</summary>
        public const double Threshold = 0.5;

        /// <summary>判定"明显偏暗"的亮度上限。</summary>
        private const double DarkLevel = 0.35;

        /// <summary>判定"明显偏亮"的亮度下限。</summary>
        private const double LightLevel = 0.65;

        private sealed class Entry
        {
            public long Length;
            public long Ticks;
            public ToneSample Sample;
        }

        private static readonly Dictionary<string, Entry> Cache =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new object();

        /// <summary>采样底图色调（带缓存）。失败时返回 <c>Ok = false</c> 的结果。</summary>
        public static ToneSample Analyze(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return default(ToneSample);

            long length;
            long ticks;
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists) return default(ToneSample);
                length = info.Length;
                ticks = info.LastWriteTimeUtc.Ticks;
            }
            catch (Exception)
            {
                return default(ToneSample);
            }

            lock (Gate)
            {
                Entry hit;
                if (Cache.TryGetValue(filePath, out hit) && hit.Length == length && hit.Ticks == ticks)
                {
                    var cached = hit.Sample;
                    cached.FromCache = true;
                    return cached;
                }
            }

            var sample = Measure(filePath);

            lock (Gate)
            {
                Cache[filePath] = new Entry { Length = length, Ticks = ticks, Sample = sample };
            }
            return sample;
        }

        /// <summary>清空缓存（自检用）。</summary>
        public static void ClearCache()
        {
            lock (Gate) { Cache.Clear(); }
        }

        /// <summary>给定色调，网格线是否该用白色。</summary>
        public static bool PrefersWhite(ToneSample sample)
        {
            return sample.Ok && sample.Mean < Threshold;
        }

        /// <summary>真正的解码 + 采样。解码失败的图当"偏暗"处理（深底白线更保险）。</summary>
        private static ToneSample Measure(string filePath)
        {
            var watch = Stopwatch.StartNew();
            var result = new ToneSample();

            try
            {
                // 走统一入口（webp 用自研解码器；GDI+ 不认 webp，直接用 FromStream 只会让判定回落到默认值）
                using (var image = ImageLoader.Load(filePath))
                using (var small = new Bitmap(SampleSize, SampleSize, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        // 透明像素按画布底色（深灰）合成 —— 带透明通道的 PNG 底图
                        // 实际看到的是"图 + 深灰背景"，判定时也得按这个来。
                        g.Clear(Color.Transparent);
                        g.DrawImage(image, new Rectangle(0, 0, SampleSize, SampleSize));
                    }

                    var background = Theme.Background;
                    var sum = 0.0;
                    var dark = 0;
                    var light = 0;

                    for (var y = 0; y < SampleSize; y++)
                    {
                        for (var x = 0; x < SampleSize; x++)
                        {
                            var c = small.GetPixel(x, y);
                            var a = c.A / 255.0;
                            var r = c.R * a + background.R * (1 - a);
                            var gg = c.G * a + background.G * (1 - a);
                            var b = c.B * a + background.B * (1 - a);

                            // 人眼权重（Rec.601）。sRGB 直接加权就够了 —— 这里只需要
                            // 分出"亮/暗"，不需要做线性空间的精确亮度。
                            var luma = (0.299 * r + 0.587 * gg + 0.114 * b) / 255.0;

                            sum += luma;
                            if (luma < DarkLevel) dark++;
                            if (luma > LightLevel) light++;
                        }
                    }

                    var total = (double)(SampleSize * SampleSize);
                    result.Ok = true;
                    result.Mean = sum / total;
                    result.DarkFraction = dark / total;
                    result.LightFraction = light / total;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("底图色调分析失败：" + filePath, ex);
                return default(ToneSample);
            }

            watch.Stop();
            result.ElapsedMs = watch.ElapsedMilliseconds;
            return result;
        }
    }
}
