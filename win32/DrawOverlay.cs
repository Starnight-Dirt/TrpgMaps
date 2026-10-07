using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace TrpgMaps
{
    /// <summary>
    /// 绘图交互的"当前选区"预览。这一层**刻意不烘进场景位图**（<see cref="MapCanvas"/> 的
    /// _scene 只装底图+网格+已落笔的内容）：鼠标一动预览就要变，如果每次都把底图和网格
    /// 重新合成一遍，老机器上拖动会明显发涩。所以预览每帧现画。
    ///
    /// 需求里"当前选区提示应该在图层最上层"就是指它 —— 它画在已经落笔的地形/实体/物品
    /// 之上，但仍在浮空面板之下（面板是兄弟控件，天然在上面）。
    /// </summary>
    internal sealed class DrawPreview
    {
        /// <summary>当前笔刷覆盖到的格子（半透明红填充）。</summary>
        public List<GridCell> Cells = new List<GridCell>();

        /// <summary>有没有放下原点。</summary>
        public bool HasOrigin;

        /// <summary>原点（半格坐标）。</summary>
        public SnapPoint Origin;

        /// <summary>要在横竖两个方向标出的候选落点。</summary>
        public List<SnapPoint> Guides = new List<SnapPoint>();

        /// <summary>鼠标当前吸附到的点。</summary>
        public bool HasHover;
        public SnapPoint Hover;

        /// <summary>笔刷形状（决定原点和落点的图标）。</summary>
        public BrushShape Shape = BrushShape.Sphere;

        // ------------------------------------------------------------
        //  "实际形状"（半透明蓝）这一层需要的数据。
        //
        //  为什么要有这一层：<see cref="Cells"/> 是**按格心是否落在形状内**量出来的
        //  覆盖格，边界天然是阶梯状的 —— 它回答"会被刷上哪些格"，
        //  但不回答"真正的范围到哪"。半径 2.5 格时，圆边上那些"格心刚好在圆外、
        //  但格子被圆切掉一角"的格子就不会被选中，光看红色会以为圆比实际小。
        //  蓝色把形状本身（真圆 / 扇形 / 正方 / 线段）勾出来，两层叠着看才完整。
        // ------------------------------------------------------------

        /// <summary>要不要画蓝色实际形状（笔刷模式下放好原点就是 true）。</summary>
        public bool HasShapeOutline;

        /// <summary>半径，单位**半格**（和 <see cref="DrawGeometry.Region"/> 的入参同一个量纲）。</summary>
        public int RadiusHalf;

        /// <summary>扇形朝向（度，0 = 屏幕 +x，正角 = 屏幕上顺时针）。</summary>
        public double Direction;

        /// <summary>扇形张角（度）。</summary>
        public double FanAngle = 90.0;

        /// <summary>鼠标像素位置（用于贴着光标画距离提示）。</summary>
        public Point Cursor;

        /// <summary>贴着光标显示的文字，例如 "15 尺（3 格）"。空串表示不显示。</summary>
        public string DistanceText = string.Empty;

        /// <summary>是否处于橡皮状态（橡皮时预览用灰白色，跟落笔用的红色区分开）。</summary>
        public bool Eraser;

        /// <summary>原点是不是落在格子中心（决定提示文案里说"格心"还是"交点"）。</summary>
        public string OriginLabel = string.Empty;
    }

    /// <summary>把绘图内容与当前选区画到画布上。纯 GDI+，可以在离屏位图上跑。</summary>
    internal static class DrawOverlayRenderer
    {
        /// <summary>选区填充（默认红）。</summary>
        public static readonly Color SelectFill = Color.FromArgb(70, 255, 64, 64);
        public static readonly Color SelectLine = Color.FromArgb(230, 255, 96, 96);
        /// <summary>橡皮状态下的选区色（灰白），避免和"要画红色地形"混淆。</summary>
        public static readonly Color EraseFill = Color.FromArgb(60, 220, 220, 220);
        public static readonly Color EraseLine = Color.FromArgb(220, 240, 240, 240);

        /// <summary>
        /// 笔刷"实际形状"的填充与描边（半透明蓝）。画在红色覆盖格**之上**。
        ///
        /// 为什么填充的 alpha 压得比红色还低（62 对 70）：两层半透明叠在一起时，
        /// 重叠区的颜色是"红→蓝"两次混合的结果，蓝的 alpha 一大，
        /// 重叠区就会偏向蓝色、把红色盖掉（用户明确要求"红色保持不变"）。
        /// 所以填充只负责给一片淡蓝底色，**真正勾形状靠的是描边**（alpha 235、2.5px，
        /// 基本不透明）—— 这也是"勾勒出实际形状"的字面做法。
        /// </summary>
        public static readonly Color ShapeFill = Color.FromArgb(62, 80, 160, 255);
        public static readonly Color ShapeLine = Color.FromArgb(235, 130, 205, 255);
        /// <summary>橡皮状态下的实际形状：换成淡青，和"要画东西"的蓝区分开。</summary>
        public static readonly Color ShapeEraseFill = Color.FromArgb(58, 150, 215, 255);
        public static readonly Color ShapeEraseLine = Color.FromArgb(220, 205, 240, 255);

        public static readonly Color GuideLine = Color.FromArgb(150, 255, 210, 120);
        public static readonly Color OriginRing = Color.FromArgb(240, 255, 220, 80);
        public static readonly Color HudBack = Color.FromArgb(215, 20, 20, 20);
        public static readonly Color HudText = Color.FromArgb(255, 255, 235, 150);

        /// <summary>
        /// 画已经落笔的内容。传入的 sprite 列表必须已经按渲染顺序排好
        /// （地形在前，实体/物品按插入序在后）。
        /// </summary>
        public static void DrawSprites(Graphics g, List<CellSprite> sprites, TerrainImageCache cache,
            double cellPixels, TerrainCatalog catalog)
        {
            if (sprites == null || sprites.Count == 0 || cache == null || cellPixels <= 0.5) return;

            var pixelSize = Math.Max(1, (int)Math.Round(cellPixels));

            var oldInterpolation = g.InterpolationMode;
            var oldPixelOffset = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            try
            {
                foreach (var sprite in sprites)
                {
                    var asset = catalog == null ? null : catalog.Resolve(sprite.Kind, sprite.File);
                    var opacity = asset == null ? 1.0 : asset.Opacity;

                    // cache.Get 给的是**独立副本**，用完要还回去（见 TerrainImageCache.Get）
                    var image = cache.Get(sprite.Kind, sprite.File, pixelSize, opacity);
                    if (image == null) continue;

                    try
                    {
                        // 目标矩形用**浮点**的格子尺寸，和网格线用的是同一个 cell 值 ——
                        // 用取整后的像素尺寸会在画面右侧累积出十几像素的偏移。
                        var rect = new RectangleF(
                            (float)(sprite.X * cellPixels),
                            (float)(sprite.Y * cellPixels),
                            (float)cellPixels,
                            (float)cellPixels);

                        g.DrawImage(image, rect);
                    }
                    finally
                    {
                        image.Dispose();
                    }
                }
            }
            finally
            {
                g.InterpolationMode = oldInterpolation;
                g.PixelOffsetMode = oldPixelOffset;
            }
        }

        /// <summary>画当前选区 / 原点 / 候选点 / 贴着光标的距离提示。</summary>
        public static void DrawPreview(Graphics g, DrawPreview preview, double cellPixels)
        {
            if (preview == null || cellPixels <= 0.5) return;

            var fill = preview.Eraser ? EraseFill : SelectFill;
            var line = preview.Eraser ? EraseLine : SelectLine;

            var oldSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            try
            {
                // 1) 选区填充 + 外框
                if (preview.Cells != null && preview.Cells.Count > 0)
                {
                    using (var brush = new SolidBrush(fill))
                    using (var pen = new Pen(line, 2f))
                    using (var path = new GraphicsPath())
                    {
                        // 每个格子单独成环，用 EvenOdd 之外的方式填会很丑；
                        // 这里逐格 fill，重叠区域看起来更深一点，正好当"重叠了几层"的提示。
                        foreach (var cell in preview.Cells)
                        {
                            var rect = new RectangleF(
                                (float)(cell.X * cellPixels),
                                (float)(cell.Y * cellPixels),
                                (float)cellPixels,
                                (float)cellPixels);
                            path.AddRectangle(rect);
                        }

                        g.FillPath(brush, path);
                        g.DrawPath(pen, path);
                    }
                }

                // 2) 实际形状（半透明蓝）：盖在红色覆盖格之上、交互标记之下
                if (preview.HasShapeOutline)
                {
                    DrawShapeOutline(g, preview, cellPixels);
                }

                // 3) 候选落点（横竖方向上的格心 / 边中点 / 交点）
                if (preview.Guides != null && preview.Guides.Count > 0)
                {
                    using (var pen = new Pen(GuideLine, 2f))
                    using (var brush = new SolidBrush(GuideLine))
                    {
                        foreach (var point in preview.Guides)
                        {
                            var pixel = DrawGeometry.ToPixel(point, cellPixels);
                            if (pixel.X < -20 || pixel.Y < -20) continue;
                            if (pixel.X > float.MaxValue) continue;

                            var size = point.IsCellCenter ? 7f : 5f;
                            g.DrawRectangle(pen,
                                pixel.X - size / 2f, pixel.Y - size / 2f, size, size);
                            if (point.IsCellCenter)
                            {
                                g.FillEllipse(brush, pixel.X - 1.5f, pixel.Y - 1.5f, 3f, 3f);
                            }
                        }
                    }
                }

                // 4) 原点：双环 + 十字，比候选点显眼
                if (preview.HasOrigin)
                {
                    var origin = DrawGeometry.ToPixel(preview.Origin, cellPixels);
                    using (var pen = new Pen(OriginRing, 2.5f))
                    {
                        g.DrawEllipse(pen, origin.X - 11f, origin.Y - 11f, 22f, 22f);
                        g.DrawEllipse(pen, origin.X - 4f, origin.Y - 4f, 8f, 8f);
                        g.DrawLine(pen, origin.X - 16f, origin.Y, origin.X + 16f, origin.Y);
                        g.DrawLine(pen, origin.X, origin.Y - 16f, origin.X, origin.Y + 16f);
                    }
                }

                // 5) 鼠标吸附到的那一点：空心小方框
                if (preview.HasHover)
                {
                    var hover = DrawGeometry.ToPixel(preview.Hover, cellPixels);
                    using (var pen = new Pen(OriginRing, 2f))
                    {
                        g.DrawRectangle(pen, hover.X - 6f, hover.Y - 6f, 12f, 12f);
                    }
                }

                // 6) 贴着光标的距离提示
                if (!string.IsNullOrEmpty(preview.DistanceText))
                {
                    DrawHud(g, preview.DistanceText, preview.Cursor);
                }
            }
            finally
            {
                g.SmoothingMode = oldSmoothing;
            }
        }

        /// <summary>
        /// 画"实际形状"那一层（半透明蓝）：真圆 / 扇形 / 正方 / 线段。
        ///
        /// 单位换算只有一处要小心：<see cref="DrawPreview.RadiusHalf"/> 是**半格**，
        /// 而一格 = cellPixels 像素、一个半格 = cellPixels/2 像素。
        /// 所以半径像素 = RadiusHalf × cellPixels / 2。
        /// 这一点和 <see cref="DrawGeometry.ToPixel"/> 用的是同一个约定，
        /// 不是巧合 —— 蓝圆的圆心必须和红格的中心重合，两边算错一个系数就会差半格。
        ///
        /// 角度也用同一套约定：<see cref="DrawPreview.Direction"/> 来自
        /// <c>Math.Atan2(dy, dx)</c>（y 轴朝下），而 GDI+ 的 <c>AddArc</c>
        /// 也是"从 +x 轴出发、屏幕上顺时针为正"，两者可以直接相加，不用翻符号。
        /// </summary>
        private static void DrawShapeOutline(Graphics g, DrawPreview preview, double cellPixels)
        {
            var fill = preview.Eraser ? ShapeEraseFill : ShapeFill;
            var line = preview.Eraser ? ShapeEraseLine : ShapeLine;

            var center = DrawGeometry.ToPixel(preview.Origin, cellPixels);
            var radius = preview.RadiusHalf * cellPixels / 2.0;

            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(line, 2.5f))
            {
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;

                switch (preview.Shape)
                {
                    case BrushShape.Line:
                    {
                        // 线形没有半径概念，形状就是 origin→target 这一条线段。
                        // 目标点用 Hover（它和自己的吸附规则一致）。
                        if (!preview.HasHover) return;
                        var target = DrawGeometry.ToPixel(preview.Hover, cellPixels);
                        g.DrawLine(pen, center, target);
                        return;
                    }

                    case BrushShape.Cube:
                    {
                        // 立方：以原点为中心的正方形，半边长 = RadiusHalf 个半格。
                        // 和 DrawGeometry.Region 里 |cx| <= cubeHalf 用的是同一套基准
                        // （cx 就是相对原点的半格数），所以蓝方块的边正好压在
                        // "再往外一格就不算"的那条线上。
                        if (radius <= 0.5) return;
                        var rect = new RectangleF(
                            center.X - (float)radius, center.Y - (float)radius,
                            (float)(radius * 2), (float)(radius * 2));
                        g.FillRectangle(brush, rect);
                        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                        return;
                    }

                    case BrushShape.Cone:
                    {
                        // 扇形：圆心 + 半径 + 以 Direction 为中线、张开 FanAngle 的一段圆弧。
                        if (radius <= 0.5) return;

                        var sweep = preview.FanAngle;
                        if (sweep <= 0) return;
                        if (sweep > 360.0) sweep = 360.0;

                        var rect = new RectangleF(
                            center.X - (float)radius, center.Y - (float)radius,
                            (float)(radius * 2), (float)(radius * 2));

                        using (var path = new GraphicsPath())
                        {
                            // 张角 = 360° 时不要"先画整圆再补两条半径"——那样中间会多出
                            // 一条从圆心到边界的线。直接当成整圆处理。
                            if (sweep >= 359.99)
                            {
                                path.AddEllipse(rect);
                            }
                            else
                            {
                                path.AddArc(rect, (float)(preview.Direction - sweep / 2.0), (float)sweep);
                                path.AddLine(path.GetLastPoint(), center);
                                path.CloseFigure();
                            }

                            g.FillPath(brush, path);
                            g.DrawPath(pen, path);
                        }
                        return;
                    }

                    default:
                    {
                        // 球形 / 圆形：半径 RadiusHalf 个半格的真圆。
                        if (radius <= 0.5) return;
                        var rect = new RectangleF(
                            center.X - (float)radius, center.Y - (float)radius,
                            (float)(radius * 2), (float)(radius * 2));
                        g.FillEllipse(brush, rect);
                        g.DrawEllipse(pen, rect);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// 小的距离读数方块。需求里说"显示一个小方块提示与原点的距离"，
        /// 位置放在**光标右上角**而不是屏幕右上角：绘制面板默认就停在屏幕右上角，
        /// 读数如果也钉在那里会被面板盖住（面板是独立控件，永远在画布之上）。
        /// </summary>
        private static void DrawHud(Graphics g, string text, Point cursor)
        {
            const int padX = 10;
            const int padY = 7;

            var font = Ui.Font(10f, FontStyle.Bold);
            var size = g.MeasureString(text, font);
            var width = (int)Math.Ceiling(size.Width) + padX * 2;
            var height = (int)Math.Ceiling(size.Height) + padY * 2;

            var x = cursor.X + 18;
            var y = cursor.Y - height - 12;

            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var path = Theme.RoundedRect(new RectangleF(x, y, width, height), 6f))
            using (var brush = new SolidBrush(HudBack))
            using (var pen = new Pen(OriginRing, 1.5f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            Ui.PrepareText(g);
            Ui.DrawText(g, text, font, HudText,
                new Rectangle(x, y, width, height), ContentAlignment.MiddleCenter);
        }
    }
}
