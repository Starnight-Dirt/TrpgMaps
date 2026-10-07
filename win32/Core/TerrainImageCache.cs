using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace TrpgMaps
{
    /// <summary>
    /// 素材贴图缓存：把素材预先缩放成**正好一格大小**，并把素材自身的不透明度烘进去。
    ///
    /// 为什么要预缩放：素材原图是 1254×1254、单张 1.3–3.3MB。一格才 90 多像素，
    /// 如果每帧都对原图做一次高质量重采样，画几十格就会把老机器拖垮。
    /// 预缩放一次之后，画一格退化成一次裸的位块传送。
    ///
    /// 缓存 key 是「分类/文件@格子像素」，所以拖动「网格尺度」滑块会换一整套缓存 ——
    /// 这是必须的：格子大小变了，贴图也得跟着重采样。
    /// 条目数设有上限，超了就把最久没用过的清掉，避免滑块一路拖过去撑爆内存。
    /// </summary>
    internal sealed class TerrainImageCache : IDisposable
    {
        private const int MaxEntries = 96;

        private sealed class Entry
        {
            public Bitmap Image;
            public long UsedAt;
        }

        private readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private readonly TerrainCatalog _catalog;
        private long _tick;

        public TerrainImageCache(TerrainCatalog catalog)
        {
            _catalog = catalog;
        }

        /// <summary>
        /// 取一张「一格大小」的贴图；素材不存在或读不出来时返回 null。
        ///
        /// **返回的是独立副本，调用方拥有它、用完必须 Dispose。**
        /// 早先这里直接把缓存里的位图交给了调用方，而 HTTP 那侧写的是
        /// <c>using (var bmp = cache.Get(...))</c> —— 结果第一次请求正常（刚建好），
        /// 之后每次取到的都是同一个**已被释放**的位图，写 PNG 时抛异常、统一变成 404。
        /// 改成一律复制一份就断掉了这条共享路径，缓存条目永远不会被外部释放，
        /// 也顺手消掉了"渲染线程正在画、手机请求刚好把它挤出 LRU 释放掉"的竞态。
        /// 复制的代价可以忽略：场景位图只在绘图/网格变化时重建一次，不是每帧。
        /// </summary>
        public Bitmap Get(string kind, string file, int pixels, double opacity)
        {
            if (string.IsNullOrEmpty(file) || pixels <= 0) return null;
            if (opacity <= 0.01) return null;

            var key = DrawKind.Normalize(kind) + "/" + file + "@" + pixels +
                      "@" + opacity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

            lock (_gate)
            {
                Entry entry;
                if (_cache.TryGetValue(key, out entry))
                {
                    entry.UsedAt = ++_tick;
                    return Clone(entry.Image);
                }
            }

            var built = Build(kind, file, pixels, opacity);
            if (built == null) return null;

            lock (_gate)
            {
                Entry existing;
                if (_cache.TryGetValue(key, out existing))     // 并发时别人先做好了
                {
                    built.Dispose();
                    existing.UsedAt = ++_tick;
                    return Clone(existing.Image);
                }

                var entry = new Entry();
                entry.Image = built;
                entry.UsedAt = ++_tick;
                _cache[key] = entry;
                TrimLocked();
                return Clone(built);
            }
        }

        /// <summary>复制一份位图给外部使用；源图已失效时返回 null。</summary>
        private static Bitmap Clone(Bitmap source)
        {
            if (source == null) return null;
            try { return new Bitmap(source); }
            catch (Exception ex)
            {
                AppLog.Write("复制素材贴图失败", ex);
                return null;
            }
        }

        private void TrimLocked()
        {
            while (_cache.Count > MaxEntries)
            {
                string oldestKey = null;
                var oldest = long.MaxValue;
                foreach (var pair in _cache)
                {
                    if (pair.Value.UsedAt < oldest)
                    {
                        oldest = pair.Value.UsedAt;
                        oldestKey = pair.Key;
                    }
                }
                if (oldestKey == null) break;

                var victim = _cache[oldestKey];
                _cache.Remove(oldestKey);
                if (victim.Image != null) victim.Image.Dispose();
            }
        }

        private Bitmap Build(string kind, string file, int pixels, double opacity)
        {
            var asset = _catalog == null ? null : _catalog.Resolve(kind, file);
            if (asset == null) return null;

            try
            {
                // 走统一入口：素材图也可能被别人换成 webp（GDI+ 不认）
                using (var source = ImageLoader.Load(asset.Path))
                {
                    var target = new Bitmap(pixels, pixels, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(target))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;

                        if (opacity >= 0.999)
                        {
                            g.DrawImage(source, new Rectangle(0, 0, pixels, pixels));
                        }
                        else
                        {
                            // 把素材的不透明度直接用颜色矩阵烘进位图，
                            // 之后每格画的时候就不必再套一层 ImageAttributes。
                            using (var attrs = new ImageAttributes())
                            {
                                var matrix = new ColorMatrix();
                                matrix.Matrix33 = (float)opacity;
                                attrs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                                attrs.SetWrapMode(WrapMode.TileFlipXY);
                                g.DrawImage(source,
                                    new Rectangle(0, 0, pixels, pixels),
                                    0, 0, source.Width, source.Height,
                                    GraphicsUnit.Pixel, attrs);
                            }
                        }
                    }
                    return target;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("生成素材贴图失败：" + asset.Path, ex);
                return null;
            }
        }

        /// <summary>素材目录重新扫描之后调用（文件被增删改）。</summary>
        public void Clear()
        {
            lock (_gate)
            {
                foreach (var entry in _cache.Values)
                {
                    if (entry.Image != null) entry.Image.Dispose();
                }
                _cache.Clear();
            }
        }

        public int Count
        {
            get { lock (_gate) { return _cache.Count; } }
        }

        public void Dispose()
        {
            Clear();
        }
    }
}
