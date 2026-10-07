using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace TrpgMaps
{
    /// <summary>一格（第 X 列、第 Y 行）。</summary>
    internal struct GridCell
    {
        public int X;
        public int Y;

        public GridCell(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>笔刷形状。</summary>
    internal enum BrushShape
    {
        Line,
        Cone,
        Cube,
        Sphere
    }

    /// <summary>画笔模式。</summary>
    internal enum BrushMode
    {
        /// <summary>手动：按住鼠标拖过哪些格就画哪些格。</summary>
        Manual,
        /// <summary>笔刷：先点原点，拖出半径，再按形状一次铺满一片。</summary>
        Brush
    }

    /// <summary>
    /// 吸附点。单位是**半格**：格心 = 奇数、网格线交点 / 格边中点 = 偶数。
    ///
    /// 之所以用半格做基准：需求里"方块中心、线中点或网格线交点"这三种落点
    /// 正好就是半格网格上的全部整数点，用一套整数坐标就能统一表达，
    /// 距离换算也不会积累浮点误差（1 个半格 = 2.5 尺）。
    /// </summary>
    internal struct SnapPoint
    {
        public int HX;
        public int HY;

        public SnapPoint(int hx, int hy)
        {
            HX = hx;
            HY = hy;
        }

        /// <summary>是不是落在格子中心（两个坐标都是奇数）。</summary>
        public bool IsCellCenter
        {
            get { return (HX & 1) == 1 && (HY & 1) == 1; }
        }
    }

    /// <summary>
    /// 网格几何换算：像素 ↔ 半格坐标、距离（尺）、以及四种笔刷形状覆盖到哪些格。
    ///
    /// 全部是纯函数、不碰窗口和位图，所以可以在无人值守自检里直接断言
    /// （见 SelfCheck 的 drawing 段）。
    /// </summary>
    internal static class DrawGeometry
    {
        /// <summary>每格边长（尺）。</summary>
        public const double FeetPerCell = 5.0;
        /// <summary>半个格边长（尺）。</summary>
        public const double FeetPerHalfCell = 2.5;

        /// <summary>把像素坐标吸附到最近的半格点（格心 / 边中点 / 交点）。</summary>
        public static SnapPoint Snap(double pixelX, double pixelY, double cellPixels)
        {
            if (cellPixels <= 0.5) cellPixels = 0.5;
            var half = cellPixels / 2.0;
            return new SnapPoint(
                (int)Math.Round(pixelX / half),
                (int)Math.Round(pixelY / half));
        }

        /// <summary>半格点 → 像素。</summary>
        public static PointF ToPixel(SnapPoint point, double cellPixels)
        {
            var half = cellPixels / 2.0;
            return new PointF((float)(point.HX * half), (float)(point.HY * half));
        }

        /// <summary>格子左上角像素。</summary>
        public static PointF CellOrigin(GridCell cell, double cellPixels)
        {
            return new PointF((float)(cell.X * cellPixels), (float)(cell.Y * cellPixels));
        }

        /// <summary>像素坐标落在哪一格。</summary>
        public static GridCell CellAt(double pixelX, double pixelY, double cellPixels)
        {
            if (cellPixels <= 0.5) cellPixels = 0.5;
            return new GridCell(
                (int)Math.Floor(pixelX / cellPixels),
                (int)Math.Floor(pixelY / cellPixels));
        }

        /// <summary>两点之间差了几个半格（欧氏，四舍五入到整数）。</summary>
        public static int HalfUnitsBetween(SnapPoint a, SnapPoint b)
        {
            var dx = a.HX - b.HX;
            var dy = a.HY - b.HY;
            return (int)Math.Round(Math.Sqrt((double)dx * dx + (double)dy * dy));
        }

        /// <summary>两点之间的直线距离（尺），已对齐到 2.5 尺的整数倍。</summary>
        public static double FeetBetween(SnapPoint a, SnapPoint b)
        {
            return HalfUnitsBetween(a, b) * FeetPerHalfCell;
        }

        public static string FormatFeet(double feet)
        {
            return feet.ToString("0.#", CultureInfo.InvariantCulture) + " 尺";
        }

        /// <summary>把半格数换算成"几格几尺"，状态行上显示用。</summary>
        public static string DescribeDistance(double feet)
        {
            var cells = feet / FeetPerCell;
            if (Math.Abs(feet) < 0.01) return "0 尺";
            return FormatFeet(feet) + "（" + cells.ToString("0.##", CultureInfo.InvariantCulture) + " 格）";
        }

        /// <summary>某个半格点上，横向/纵向到锚点距离在半径内的全部候选点。</summary>
        public static List<SnapPoint> GuidePoints(SnapPoint origin, int maxRadiusHalfUnits)
        {
            var result = new List<SnapPoint>();
            for (var d = 1; d <= maxRadiusHalfUnits; d++)
            {
                result.Add(new SnapPoint(origin.HX + d, origin.HY));
                result.Add(new SnapPoint(origin.HX - d, origin.HY));
                result.Add(new SnapPoint(origin.HX, origin.HY + d));
                result.Add(new SnapPoint(origin.HX, origin.HY - d));
            }
            return result;
        }

        /// <summary>线段扫过哪些格（supercover 近似：按 1/4 格步长采样去重）。</summary>
        public static List<GridCell> LineCells(SnapPoint origin, SnapPoint target, double cellPixels)
        {
            var cells = new List<GridCell>();
            var seen = new HashSet<long>();

            var a = ToPixel(origin, cellPixels);
            var b = ToPixel(target, cellPixels);

            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var length = Math.Sqrt((double)dx * dx + (double)dy * dy);

            // 步长取 1/4 格：既不会漏格，也不会像 1/10 格那样在长线上白跑几千次
            var step = Math.Max(1.0, cellPixels / 4.0);
            var steps = (int)Math.Ceiling(length / step);

            for (var i = 0; i <= steps; i++)
            {
                var t = steps == 0 ? 0.0 : (double)i / steps;
                var px = a.X + dx * t;
                var py = a.Y + dy * t;
                var cell = CellAt(px, py, cellPixels);
                if (seen.Add(DrawingStore.Key(cell.X, cell.Y))) cells.Add(cell);
            }
            return cells;
        }

        /// <summary>
        /// 按形状算出覆盖哪些格。
        ///
        /// **两种选区方式**（<paramref name="loose"/>）：
        ///
        ///  - **严格**（`false`，默认）：判定用「**格心**是否落在形状内」—— 这是桌面战棋的通行做法：
        ///    按格心判，边界上才不会出现半格算不算的争议，DM 和玩家数出来的结果也一致。
        ///    代价是边界天然呈阶梯状：圆边上"格心刚好在圆外、但格子被圆切掉一角"的格子不会被选中。
        ///  - **宽泛**（`true`）：只要形状**真正压到**这个格（格的矩形与形状的重叠面积为正）就算选中。
        ///    更贴合"目测蓝色形状边线压到哪几格"，代价是格数偏多。
        ///    ⚠️ **只相切不算**：形状边线正好压在格线上、只从格角蹭过去这类"零面积接触"被排除 ——
        ///    那一格整格都在形状外面，目视也该是没被压到的。
        ///
        /// 两种方式共用同一个形状定义，所以**红色覆盖格与蓝色实际形状永远是同一套参数**算出来的，
        /// 切换选区方式只改"怎么把形状落到格上"，不会让蓝层跟着变形。
        ///
        ///  - 线形：直接用 origin→target 这条线段（长度就是两点距离，角度不限）；
        ///    它本来就是"线段扫过哪些格"，两种方式同解 —— 宽泛不会多出格来。
        ///  - 立方：以原点为中心、半边长 = radius 的正方形；
        ///  - 球形：以原点为圆心、半径 = radius 的圆；
        ///  - 锥形：半径 = radius、张角 = fanAngle、朝向 = direction 的扇形。
        /// </summary>
        public static List<GridCell> Region(
            BrushShape shape, SnapPoint origin, SnapPoint target,
            int radiusHalfUnits, double directionDegrees, double fanAngleDegrees,
            double cellPixels, bool loose = false)
        {
            if (shape == BrushShape.Line)
                return LineCells(origin, target, cellPixels);

            if (loose)
                return LooseRegion(shape, origin, radiusHalfUnits, directionDegrees, fanAngleDegrees);

            var cells = new List<GridCell>();
            if (radiusHalfUnits <= 0) return cells;

            var radiusSquared = (double)radiusHalfUnits * radiusHalfUnits;
            var cubeHalf = radiusHalfUnits;

            // 先圈出可能命中的格子范围，避免逐格全画布扫描
            var minX = (int)Math.Floor((origin.HX - radiusHalfUnits - 1) / 2.0);
            var maxX = (int)Math.Ceiling((origin.HX + radiusHalfUnits + 1) / 2.0);
            var minY = (int)Math.Floor((origin.HY - radiusHalfUnits - 1) / 2.0);
            var maxY = (int)Math.Ceiling((origin.HY + radiusHalfUnits + 1) / 2.0);

            var halfAngle = fanAngleDegrees / 2.0;
            var useAngle = shape == BrushShape.Cone;

            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    // 格心在半格坐标里就是 (2x+1, 2y+1)
                    var cx = 2 * x + 1 - origin.HX;
                    var cy = 2 * y + 1 - origin.HY;

                    if (shape == BrushShape.Cube)
                    {
                        if (Math.Abs(cx) > cubeHalf || Math.Abs(cy) > cubeHalf) continue;
                    }
                    else
                    {
                        if ((double)cx * cx + (double)cy * cy > radiusSquared) continue;

                        if (useAngle)
                        {
                            var angle = Math.Atan2(cy, cx) * 180.0 / Math.PI;
                            if (AngleDiff(angle, directionDegrees) > halfAngle + 0.0001) continue;
                        }
                    }

                    cells.Add(new GridCell(x, y));
                }
            }
            return cells;
        }

        // ============================================================
        //  宽泛选区：格的矩形与形状有**正面积**的重叠才算选中
        // ============================================================

        /// <summary>
        /// 宽泛选区的实现。全程在**半格坐标**里做，格 (x,y) 的矩形就是 `[2x,2x+2] × [2y,2y+2]`。
        ///
        /// 圆与方是**精确**判定（圆心到矩形最近点 / 两个矩形求交），
        /// 不是"多撒几个采样点"的近似 —— 目测蓝色圆边压到哪一格，就该是哪一格。
        ///
        /// ⚠️ **只承认正面积的重叠，"相切"不算**：形状边线正好压在格线上（或只蹭到格角）时，
        /// 那一格一个像素的面都没盖到，目视也看得出来格子还在形状外面 ——
        /// 半格坐标下格边界与圆心全是整数、半径也是整数，所以圆的相切能精确判出来；
        /// 楔形（扇形）则靠"把格矩形内缩一个极小量再做点判据"来剔除零面积的接触。
        /// </summary>
        private static List<GridCell> LooseRegion(BrushShape shape, SnapPoint origin,
            int radiusHalfUnits, double directionDegrees, double fanAngleDegrees)
        {
            var cells = new List<GridCell>();
            if (radiusHalfUnits <= 0) return cells;

            var radius = (double)radiusHalfUnits;
            var halfAngle = fanAngleDegrees / 2.0;
            var cx = (double)origin.HX;
            var cy = (double)origin.HY;

            // 格矩形最多比"半径"再外扩一个格（格边长 2 半格），多扫一圈保险
            var minX = (int)Math.Floor((origin.HX - radiusHalfUnits) / 2.0) - 1;
            var maxX = (int)Math.Ceiling((origin.HX + radiusHalfUnits) / 2.0) + 1;
            var minY = (int)Math.Floor((origin.HY - radiusHalfUnits) / 2.0) - 1;
            var maxY = (int)Math.Ceiling((origin.HY + radiusHalfUnits) / 2.0) + 1;

            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    double left = 2 * x, top = 2 * y, right = left + 2, bottom = top + 2;

                    if (shape == BrushShape.Cube)
                    {
                        // 正方形 [cx-r, cx+r] × [cy-r, cy+r] 与格矩形求交。
                        // **只相切不算**：正方形边线正好压在格线上时，外面那一格只是被"贴"着，
                        // 一格的面积都没盖到，不该算被扫到（用户明确要求）。
                        if (cx - radius >= right || cx + radius <= left) continue;
                        if (cy - radius >= bottom || cy + radius <= top) continue;
                    }
                    else
                    {
                        if (!RectHitsCircle(left, top, right, bottom, cx, cy, radius)) continue;

                        // 锥形再叠一个"格矩形 ∩ 楔形"
                        if (shape == BrushShape.Cone &&
                            !RectHitsWedge(left, top, right, bottom, cx, cy, radius,
                                           directionDegrees, halfAngle))
                            continue;
                    }

                    cells.Add(new GridCell(x, y));
                }
            }
            return cells;
        }

        /// <summary>
        /// 格的矩形与圆（圆心 + 半径）有没有**正面积**的交集：圆心到矩形最近点的距离 **严格小于** 半径。
        ///
        /// ⚠️ 恰好相切（距离 == 半径）不算 —— 半格坐标下格边界（偶数）、圆心（origin.HX/HY）与半径
        /// 全是整数，`dx*dx+dy*dy` 与 `radius*radius` 都能精确表示，所以这里可以直接用严格小于；
        /// 减一个 1e-9 只是给将来半径可能取小数的情形留的余量（整数情形下最小差值就是 1，不会被误伤）。
        /// </summary>
        private static bool RectHitsCircle(double left, double top, double right, double bottom,
            double cx, double cy, double radius)
        {
            var nx = cx < left ? left : (cx > right ? right : cx);
            var ny = cy < top ? top : (cy > bottom ? bottom : cy);
            var dx = cx - nx;
            var dy = cy - ny;
            return dx * dx + dy * dy < radius * radius - 1e-9;
        }

        /// <summary>楔形判据里用的"内缩量"：把格矩形四边各往内缩这么多再判定，见 RectHitsWedge。</summary>
        private const double LooseEps = 1e-6;

        /// <summary>
        /// 格的矩形与"以 (cx,cy) 为顶点、朝向 dirDeg、半张角 halfAngle、半径 radius"的**扇形区域**
        /// 有没有**正面积**的交集。
        ///
        /// 三条判据（任一成立即相交）：
        ///  1. 顶点本身落在格内（顶点一定在扇形里）；
        ///  2. 格的四角 + 四边上的等分采样点有任一个落在扇形里；
        ///  3. 扇形的两条**边界射线**（截到半径以内）穿过格矩形。
        ///
        /// 第 3 条不能省：细楔形可能整条穿过一个格子却一个角都不碰（张角 53° 时尤其明显）。
        ///
        /// ⚠️ **所有点判据都拿"内缩了 `LooseEps` 的格矩形"去做**，这样"扇形只蹭到格角 / 格边"
        /// （接触面积恰为 0）会被自动排除 —— 90° 张角时两条边界射线正好是 ±45°，很爱压着格角走，
        /// 不内缩的话那一格明明整格都在扇形外面，却会因为"角点在射线上"被算进来。
        /// 内缩量取 1e-6：半格坐标下两个整数坐标的最小非零间距是 1，远大于它，不会误伤真正压到的格。
        /// </summary>
        private static bool RectHitsWedge(double left, double top, double right, double bottom,
            double cx, double cy, double radius, double directionDegrees, double halfAngle)
        {
            var l = left + LooseEps;
            var t = top + LooseEps;
            var r = right - LooseEps;
            var b = bottom - LooseEps;
            if (l >= r || t >= b) return false;

            if (cx > l && cx < r && cy > t && cy < b) return true;

            if (PointInSector(l, t, cx, cy, radius, directionDegrees, halfAngle)) return true;
            if (PointInSector(r, t, cx, cy, radius, directionDegrees, halfAngle)) return true;
            if (PointInSector(l, b, cx, cy, radius, directionDegrees, halfAngle)) return true;
            if (PointInSector(r, b, cx, cy, radius, directionDegrees, halfAngle)) return true;

            // 四条边各取 1/4、1/2、3/4 三个内点
            var dxs = new double[] { 0.25, 0.5, 0.75 };
            for (var i = 0; i < dxs.Length; i++)
            {
                var u = dxs[i];
                if (PointInSector(l + (r - l) * u, t, cx, cy, radius, directionDegrees, halfAngle)) return true;
                if (PointInSector(l + (r - l) * u, b, cx, cy, radius, directionDegrees, halfAngle)) return true;
                if (PointInSector(l, t + (b - t) * u, cx, cy, radius, directionDegrees, halfAngle)) return true;
                if (PointInSector(r, t + (b - t) * u, cx, cy, radius, directionDegrees, halfAngle)) return true;
            }

            if (RayHitsRect(cx, cy, directionDegrees - halfAngle, radius, l, t, r, b)) return true;
            if (RayHitsRect(cx, cy, directionDegrees + halfAngle, radius, l, t, r, b)) return true;
            return false;
        }

        /// <summary>
        /// 宽泛判定里"点是否落在扇形里"的**角度容差**（度）。
        ///
        /// 只用来吸收 `atan2` 的浮点噪声，所以给得极小：`RectHitsWedge` 靠"把格矩形内缩
        /// `LooseEps`"来剔除零面积接触，而内缩 1e-6 半格在角度上只偏差约 5.7e-5 度 ——
        /// 容差要是像原来那样给 1e-4 度，内缩就被容差整个吃掉了（90° 张角那两格会照样被选中）。
        /// 1e-8 度比浮点噪声（约 1e-11 度）大，比内缩带来的偏差（≥ 1e-6 度）小。
        /// </summary>
        private const double SectorAngleTol = 1e-8;

        /// <summary>点是否落在扇形（圆 ∩ 楔形）里。张角 360° 时退化成整圆。</summary>
        private static bool PointInSector(double px, double py, double cx, double cy,
            double radius, double directionDegrees, double halfAngle)
        {
            var dx = px - cx;
            var dy = py - cy;
            if (dx * dx + dy * dy > radius * radius) return false;
            if (halfAngle >= 180.0) return true;                  // 整圆
            if (dx == 0.0 && dy == 0.0) return true;              // 顶点
            var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            return AngleDiff(angle, directionDegrees) <= halfAngle + SectorAngleTol;
        }

        /// <summary>
        /// 从 (ox,oy) 沿 angleDeg 射出的射线，**只取离顶点 radius 以内的那一段**，
        /// 有没有**穿过**矩形。用标准 slab 裁剪：把矩形在 x / y 两个方向上的行区间求交。
        ///
        /// 要求交出来的是一段**有长度**的区间（`t1 > t0`）：只是从角上"蹭"过去（t1 == t0）
        /// 同样属于零面积接触，不算。
        /// </summary>
        private static bool RayHitsRect(double ox, double oy, double angleDeg, double radius,
            double left, double top, double right, double bottom)
        {
            var rad = angleDeg * Math.PI / 180.0;
            var dx = Math.Cos(rad);
            var dy = Math.Sin(rad);

            double t0 = 0.0, t1 = radius;
            if (t1 < t0) return false;
            if (!ClipSlab(ox, dx, left, right, ref t0, ref t1)) return false;
            if (!ClipSlab(oy, dy, top, bottom, ref t0, ref t1)) return false;
            return t1 > t0 + 1e-6;
        }

        /// <summary>把一个方向上的 [lo,hi] 区间并进射线参数区间 [t0,t1]；返回区间是否还有交集。</summary>
        private static bool ClipSlab(double origin, double dir, double lo, double hi,
            ref double t0, ref double t1)
        {
            if (Math.Abs(dir) < 1e-9)
                return origin >= lo && origin <= hi;              // 平行：本来就在带里才可能相交

            var a = (lo - origin) / dir;
            var b = (hi - origin) / dir;
            if (a > b) { var tmp = a; a = b; b = tmp; }
            if (a > t0) t0 = a;
            if (b < t1) t1 = b;
            return t1 >= t0;
        }

        /// <summary>两个角度之间的最小夹角（0-180）。</summary>
        public static double AngleDiff(double a, double b)
        {
            var diff = (a - b) % 360.0;
            if (diff < -180.0) diff += 360.0;
            if (diff > 180.0) diff -= 360.0;
            return Math.Abs(diff);
        }

        /// <summary>形状的中文名。</summary>
        public static string ShapeLabel(BrushShape shape)
        {
            switch (shape)
            {
                case BrushShape.Line: return "线形";
                case BrushShape.Cone: return "锥形/扇形";
                case BrushShape.Cube: return "立方";
                default: return "球形/圆形";
            }
        }

        public static string ModeLabel(BrushMode mode)
        {
            return mode == BrushMode.Manual ? "手动" : "笔刷";
        }
    }

    /// <summary>一格上的一张贴图（按渲染顺序产出）。</summary>
    internal sealed class CellSprite
    {
        public string Kind;
        public int X;
        public int Y;
        public string File;
    }

    /// <summary>
    /// 绘图数据层：**只管数据，不碰界面、不碰位图**。
    ///
    /// 图层模型（用户口述的规则）：
    ///   - 地形（terrain）：每格最多一层，画新的地形会顶掉旧的；
    ///   - 物品（item）  ：每格最多一层，同上；
    ///   - 实体（entity）：每格不限层数，一笔画上去就是新的一层；
    ///   - 渲染顺序：**地形在最下面，实体与物品同级**（谁后画谁在上面），
    ///     所以实体和物品共用一条插入序列表 `_upper`，地形单独一张表。
    ///
    /// 一笔（一次按下-拖动-松开，或笔刷的一次落笔）= 每格最多写一次，
    /// 免得鼠标在同一个格子里抖一下就叠出十几层实体。
    ///
    /// 数据是**全局**的、不跟着单张底图走 —— 和旋转角、网格线颜色一样，
    /// 这里的默认心智模型是"一张桌子"：摆好了就定了，换底图不该把地形清空。
    /// </summary>
    internal sealed class DrawingStore
    {
        private readonly object _gate = new object();

        private readonly Dictionary<long, string> _terrain = new Dictionary<long, string>();
        private readonly List<CellSprite> _upper = new List<CellSprite>();

        private int _version;
        private readonly string _filePath;

        public DrawingStore(string filePath)
        {
            _filePath = filePath;
            Load();
        }

        /// <summary>每次内容变化 +1。位图缓存与玩家端刷新都用它当 key。</summary>
        public int Version
        {
            get { lock (_gate) { return _version; } }
        }

        /// <summary>一共有多少格被画过（地形 + 实体 + 物品）。</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _terrain.Count + _upper.Count;
                }
            }
        }

        public bool IsEmpty
        {
            get
            {
                lock (_gate)
                {
                    return _terrain.Count == 0 && _upper.Count == 0;
                }
            }
        }

        /// <summary>把 (x,y) 压成一个 long 当字典键（两维都支持负数）。</summary>
        public static long Key(int x, int y)
        {
            return ((long)x << 32) ^ (uint)y;
        }

        // ============================================================
        //  写入
        // ============================================================

        /// <summary>
        /// 把一批格子刷成某个素材。一笔之内每格只写一次。
        /// 地形 / 物品各占一格一个位置（顶掉旧的），实体是追加。
        /// </summary>
        public int Paint(string kind, IList<GridCell> cells, string file)
        {
            if (cells == null || cells.Count == 0 || string.IsNullOrEmpty(file)) return 0;
            var normalized = DrawKind.Normalize(kind);
            var changed = 0;

            lock (_gate)
            {
                var once = new HashSet<long>();
                if (normalized == DrawKind.Terrain)
                {
                    foreach (var cell in cells)
                    {
                        if (!once.Add(Key(cell.X, cell.Y))) continue;
                        var key = Key(cell.X, cell.Y);
                        string old;
                        if (_terrain.TryGetValue(key, out old) && string.Equals(old, file, StringComparison.Ordinal))
                            continue;                       // 已经是这张图，不算改动
                        _terrain[key] = file;
                        changed++;
                    }
                }
                else
                {
                    foreach (var cell in cells)
                    {
                        var key = Key(cell.X, cell.Y);
                        if (!once.Add(key)) continue;

                        if (normalized == DrawKind.Item)
                        {
                            // 物品每格只留一层：先摘掉这一格上已有的物品
                            var removed = _upper.RemoveAll(delegate (CellSprite s)
                            {
                                return s.Kind == DrawKind.Item && s.X == cell.X && s.Y == cell.Y;
                            });
                            if (removed > 0) changed++;
                        }

                        var sprite = new CellSprite();
                        sprite.Kind = normalized;
                        sprite.X = cell.X;
                        sprite.Y = cell.Y;
                        sprite.File = file;
                        _upper.Add(sprite);
                        changed++;
                    }
                }

                if (changed > 0) _version++;
            }
            return changed;
        }

        /// <summary>擦掉一批格子上的某一类内容（橡皮）。</summary>
        public int Erase(string kind, IList<GridCell> cells)
        {
            if (cells == null || cells.Count == 0) return 0;
            var normalized = DrawKind.Normalize(kind);
            var changed = 0;

            lock (_gate)
            {
                var once = new HashSet<long>();
                foreach (var cell in cells)
                {
                    if (!once.Add(Key(cell.X, cell.Y))) continue;

                    if (normalized == DrawKind.Terrain)
                    {
                        if (_terrain.Remove(Key(cell.X, cell.Y))) changed++;
                    }
                    else
                    {
                        changed += _upper.RemoveAll(delegate (CellSprite s)
                        {
                            return s.Kind == normalized && s.X == cell.X && s.Y == cell.Y;
                        });
                    }
                }

                if (changed > 0) _version++;
            }
            return changed;
        }

        /// <summary>清空某一类（地形 / 实体 / 物品）。只是数据层能力，面板上的「清空本类」不走这里。</summary>
        public int ClearKind(string kind)
        {
            var normalized = DrawKind.Normalize(kind);
            var changed = 0;

            lock (_gate)
            {
                if (normalized == DrawKind.Terrain)
                {
                    changed = _terrain.Count;
                    _terrain.Clear();
                }
                else
                {
                    changed = _upper.RemoveAll(delegate (CellSprite s) { return s.Kind == normalized; });
                }

                if (changed > 0) _version++;
            }
            return changed;
        }

        /// <summary>
        /// 清空**某一类里的某一张素材**（面板上那个文字写着「清空本类」的按钮）。
        ///
        /// 和 <see cref="ClearKind"/> 的区别就是"粒度"：同一类里可能画了好几张不同的素材
        /// （terrain\ 里同时铺了「高草丛」和「火焰」），这里只摘掉点名的那一张，
        /// 别的素材、别的分类、底图、网格设置一律不动。
        /// </summary>
        public int ClearAsset(string kind, string file)
        {
            if (string.IsNullOrEmpty(file)) return 0;

            var normalized = DrawKind.Normalize(kind);
            var changed = 0;

            lock (_gate)
            {
                if (normalized == DrawKind.Terrain)
                {
                    // 不能边遍历边删，先把命中的键收下来
                    var keys = new List<long>();
                    foreach (var pair in _terrain)
                    {
                        if (string.Equals(pair.Value, file, StringComparison.Ordinal)) keys.Add(pair.Key);
                    }
                    for (var i = 0; i < keys.Count; i++)
                    {
                        if (_terrain.Remove(keys[i])) changed++;
                    }
                }
                else
                {
                    changed = _upper.RemoveAll(delegate (CellSprite s)
                    {
                        return s.Kind == normalized && string.Equals(s.File, file, StringComparison.Ordinal);
                    });
                }

                if (changed > 0) _version++;
            }
            return changed;
        }

        /// <summary>某一类里，这张素材一共画了多少格（状态行提示用）。</summary>
        public int CountAsset(string kind, string file)
        {
            if (string.IsNullOrEmpty(file)) return 0;

            var normalized = DrawKind.Normalize(kind);
            var total = 0;

            lock (_gate)
            {
                if (normalized == DrawKind.Terrain)
                {
                    foreach (var pair in _terrain)
                    {
                        if (string.Equals(pair.Value, file, StringComparison.Ordinal)) total++;
                    }
                }
                else
                {
                    foreach (var sprite in _upper)
                    {
                        if (sprite.Kind == normalized && string.Equals(sprite.File, file, StringComparison.Ordinal))
                            total++;
                    }
                }
            }
            return total;
        }

        /// <summary>清空全部绘图。</summary>
        public int ClearAll()
        {
            var changed = 0;
            lock (_gate)
            {
                changed = _terrain.Count + _upper.Count;
                _terrain.Clear();
                _upper.Clear();
                if (changed > 0) _version++;
            }
            return changed;
        }

        // ============================================================
        //  读取
        // ============================================================

        /// <summary>
        /// 按渲染顺序导出全部贴图：先地形，再实体/物品（插入序）。
        /// 返回的是快照，调用方可以放心遍历 —— 渲染发生在 UI 线程、
        /// 写入可能来自 HTTP 线程，不能把内部集合直接暴露出去。
        /// </summary>
        public List<CellSprite> Snapshot()
        {
            var result = new List<CellSprite>();
            lock (_gate)
            {
                foreach (var pair in _terrain)
                {
                    var sprite = new CellSprite();
                    sprite.Kind = DrawKind.Terrain;
                    sprite.X = (int)(pair.Key >> 32);
                    sprite.Y = (int)(uint)pair.Key;
                    sprite.File = pair.Value;
                    result.Add(sprite);
                }
                result.AddRange(_upper);
            }
            return result;
        }

        /// <summary>某一格上有没有东西（绘制/擦除的快速判断）。</summary>
        public bool HasAny(string kind, int x, int y)
        {
            var normalized = DrawKind.Normalize(kind);
            lock (_gate)
            {
                if (normalized == DrawKind.Terrain) return _terrain.ContainsKey(Key(x, y));
                foreach (var sprite in _upper)
                {
                    if (sprite.Kind == normalized && sprite.X == x && sprite.Y == y) return true;
                }
                return false;
            }
        }

        // ============================================================
        //  持久化
        // ============================================================

        /// <summary>素材目录同级的 drawing.json 路径。</summary>
        public static string DefaultPath()
        {
            return Path.Combine(AppEnv.Root, "drawing.json");
        }

        /// <summary>序列化成 JSON（同时给玩家端用）。</summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            lock (_gate)
            {
                sb.Append("{\"version\":").Append(_version);
                sb.Append(",\"cells\":[");

                var first = true;

                // 地形：字典顺序无所谓，格子互不重叠
                foreach (var pair in _terrain)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(MiniJson.WriteObject(
                        "k", DrawKind.Terrain,
                        "x", (int)(pair.Key >> 32),
                        "y", (int)(uint)pair.Key,
                        "f", pair.Value));
                }

                foreach (var sprite in _upper)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(MiniJson.WriteObject(
                        "k", sprite.Kind, "x", sprite.X, "y", sprite.Y, "f", sprite.File));
                }

                sb.Append("]}");
            }
            return sb.ToString();
        }

        /// <summary>从磁盘读回（启动时调用一次）。</summary>
        public void Load()
        {
            if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) return;

            try
            {
                var text = File.ReadAllText(_filePath, Encoding.UTF8);
                var root = MiniJson.ParseObject(text);
                if (root == null) return;

                object rawCells;
                if (!root.TryGetValue("cells", out rawCells)) return;
                var list = rawCells as List<object>;
                if (list == null) return;

                var terrain = new Dictionary<long, string>();
                var upper = new List<CellSprite>();

                foreach (var item in list)
                {
                    var obj = item as Dictionary<string, object>;
                    if (obj == null) continue;

                    var kind = DrawKind.Normalize(MiniJson.GetString(obj, "k"));
                    var file = MiniJson.GetString(obj, "f");
                    if (string.IsNullOrEmpty(file)) continue;

                    var x = MiniJson.GetInt(obj, "x", 0);
                    var y = MiniJson.GetInt(obj, "y", 0);

                    if (kind == DrawKind.Terrain)
                    {
                        terrain[Key(x, y)] = file;
                    }
                    else
                    {
                        var sprite = new CellSprite();
                        sprite.Kind = kind;
                        sprite.X = x;
                        sprite.Y = y;
                        sprite.File = file;
                        upper.Add(sprite);
                    }
                }

                lock (_gate)
                {
                    _terrain.Clear();
                    foreach (var pair in terrain) _terrain[pair.Key] = pair.Value;
                    _upper.Clear();
                    _upper.AddRange(upper);
                    _version++;
                }

                AppLog.Write("已读回绘图数据：" + _filePath +
                             "（地形 " + terrain.Count + " 格，实体/物品 " + upper.Count + " 格）");
            }
            catch (Exception ex)
            {
                AppLog.Write("读取绘图数据失败：" + _filePath, ex);
            }
        }

        /// <summary>写盘。写失败不抛异常，只记日志（画图不能因为存盘失败而中断）。</summary>
        public bool Save()
        {
            if (string.IsNullOrEmpty(_filePath)) return false;
            try
            {
                var json = ToJson();
                var temp = _filePath + ".tmp";
                File.WriteAllText(temp, json, Encoding.UTF8);
                if (File.Exists(_filePath)) File.Delete(_filePath);
                File.Move(temp, _filePath);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write("保存绘图数据失败：" + _filePath, ex);
                return false;
            }
        }
    }
}
