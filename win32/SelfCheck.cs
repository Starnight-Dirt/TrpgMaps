using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace TrpgMaps
{
    /// <summary>
    /// 无人值守自检：**不起窗口、不起服务器、不弹任何对话框**，直接把画面渲染成位图，
    /// 并把关键几何写进日志。
    ///
    ///     TrpgMaps.exe --selfcheck [输出目录]
    ///
    /// 为什么要有这个：--ui 截图自检必须真的开一个窗口，而开窗口在无人值守时
    /// 很可能触发安全软件的"是否允许运行"提示，没人点就卡住了。这里走的是
    /// Graphics.FromImage 的离屏渲染，完全不碰窗口系统，所以可以放心自动跑。
    ///
    /// 它回答两个纯视觉、HTTP 接口测不出来的问题：
    ///   1. 一格到底多少像素？放了底图之后有没有变小？（不该变小）
    ///   2. 侧栏收起/展开时，按钮的左右留白和图标位置是否合理？
    /// </summary>
    internal static class SelfCheck
    {
        private const int CanvasWidth = 1200;
        private const int CanvasHeight = 800;

        private sealed class RenderCase
        {
            public readonly string Name;
            public readonly string MapPath;
            public readonly string Mode;
            public readonly double Cm;

            public RenderCase(string name, string mapPath, string mode, double cm)
            {
                Name = name;
                MapPath = mapPath;
                Mode = mode;
                Cm = cm;
            }
        }

        public static int Run(string outDir)
        {
            if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(AppEnv.Root, "selfcheck");

            var log = new List<string>();
            var measured = new Dictionary<string, double>();
            var ok = true;

            try
            {
                Directory.CreateDirectory(outDir);
            }
            catch (Exception ex)
            {
                AppLog.Write("创建自检输出目录失败：" + outDir, ex);
                return 2;
            }

            log.Add("TrpgMaps selftest (headless)");
            log.Add("time   : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            log.Add("outdir : " + outDir);
            log.Add("canvas : " + CanvasWidth + "x" + CanvasHeight);
            log.Add("");

            // ---------------- 底图 ----------------

            var mapFile = PickBiggestMap();
            var mapW = 0;
            var mapH = 0;

            log.Add("--- basemap ---");
            if (mapFile == null)
            {
                log.Add("  none found in " + AppEnv.MapsFolder);
            }
            else
            {
                var size = ImageSizeReader.Read(mapFile);
                if (size != null) { mapW = size.Width; mapH = size.Height; }
                log.Add("  file   : " + Path.GetFileName(mapFile));
                log.Add("  pixels : " + mapW + "x" + mapH);

                if (mapW > 0 && mapH > 0)
                {
                    var fit = Math.Max((double)CanvasWidth / mapW, (double)CanvasHeight / mapH);
                    log.Add(string.Format(
                        "  fit scale on a {0}x{1} canvas = {2:0.###}",
                        CanvasWidth, CanvasHeight, fit));
                    log.Add(string.Format(
                        "  the OLD formula multiplied the grid by this scale, so 2.5cm/cell " +
                        "rendered as {0:0.0}px instead of {1:0.0}px (that is why a nominal " +
                        "2cm cell measured under 1cm on screen)",
                        2.5 * AppEnv.PixelsPerCm * fit, 2.5 * AppEnv.PixelsPerCm));
                }
            }
            log.Add("");

            // ---------------- 渲染 ----------------

            var cases = new List<RenderCase>
            {
                new RenderCase("01-grid-only-2.5cm", null, "fill", 2.5),
                new RenderCase("02-grid-map-2.5cm-fill", mapFile, "fill", 2.5),
                new RenderCase("03-grid-map-2.5cm-contain", mapFile, "contain", 2.5),
                new RenderCase("04-grid-only-3.0cm", null, "fill", 3.0),
                new RenderCase("05-grid-map-3.0cm-fill", mapFile, "fill", 3.0),
                new RenderCase("06-grid-only-1.0cm", null, "fill", 1.0)
            };

            log.Add("--- rendered scenes (cell = cm/cell * " +
                    AppEnv.PixelsPerCm.ToString("0.0") + " px/cm) ---");

            foreach (var c in cases)
            {
                var cell = Render(outDir, c, log);
                if (cell <= 0) { ok = false; continue; }
                measured[c.Name] = cell;

                log.Add(string.Format("  {0,-28} cm={1,4:0.0}  basemap={2,-3}  cell={3,6:0.0} px",
                    c.Name, c.Cm, c.MapPath == null ? "no" : "yes", cell));
            }
            log.Add("");

            // ---------------- 一致性核对 ----------------
            // 同一个 cm 值，有没有底图、拉伸还是保持比例，格子像素数都必须完全一样。

            log.Add("--- consistency: the cell size must not react to the basemap ---");
            ok &= Expect(log, measured, "2.5cm, no basemap", "01-grid-only-2.5cm", 2.5);
            ok &= Expect(log, measured, "2.5cm, basemap fill", "02-grid-map-2.5cm-fill", 2.5);
            ok &= Expect(log, measured, "2.5cm, basemap contain", "03-grid-map-2.5cm-contain", 2.5);
            ok &= Expect(log, measured, "3.0cm, no basemap", "04-grid-only-3.0cm", 3.0);
            ok &= Expect(log, measured, "3.0cm, basemap fill", "05-grid-map-3.0cm-fill", 3.0);
            ok &= Expect(log, measured, "1.0cm, no basemap", "06-grid-only-1.0cm", 1.0);

            if (mapFile == null)
            {
                log.Add("  note: no basemap available, the map cases were skipped");
            }
            log.Add("");

            // ---------------- 底图旋转 ----------------

            log.Add("--- basemap rotation (clockwise) ---");
            ok &= CheckRotation(log, outDir);
            log.Add("");

            // ---------------- 网格锚点（手机端位置对齐） ----------------

            log.Add("--- grid anchor: the 4 numbers the phone uses to place the grid ---");
            ok &= CheckGridAnchor(log, outDir);
            log.Add("");

            // ---------------- 网格线颜色 ----------------

            log.Add("--- grid line colour (auto / white / black) ---");
            ok &= CheckGridColor(log, outDir);
            log.Add("");

            // ---------------- WebP（自研 VP8 解码器） ----------------

            log.Add("--- webp decoding (built-in VP8 decoder) ---");
            ok &= CheckWebpDecode(log, outDir);
            log.Add("");

            // ---------------- 素材预览缩略图 ----------------

            log.Add("--- asset preview thumbnails (must not fall back to a flat fill) ---");
            ok &= CheckAssetThumbnails(log, outDir);
            log.Add("");

            // ---------------- 绘图 ----------------

            log.Add("--- drawing: snap / distance / shapes ---");
            ok &= CheckDrawing(log, outDir);
            log.Add("");

            // ---------------- 侧栏几何 ----------------

            log.Add("--- sidebar geometry (button inset / icon position) ---");
            // 标签数要和 MainForm 里真正传进去的一致（最后一个是"关于"，
            // 它会被 BottomSlots 贴到底部）。少了这一项，底部按钮的检查就测不到东西。
            var sidebar = new SidebarPanel(
                new[] { "expand", "map", "network", "draw", "fullscreen", "about" },
                new string[0]);
            try
            {
                log.Add("  collapsed: " + sidebar.DescribeGeometry(SidebarPanel.CollapsedWidth));
                log.Add("  expanded : " + sidebar.DescribeGeometry(SidebarPanel.ExpandedWidth));

                // 底部那颗（"关于"）必须真的贴在底边上：
                // 底边距 == TopPadding，而且和上方按钮之间还留着至少一个 ButtonGap
                // （中间空一大截是正常的：它是从底边往上数的，不跟着上面那一列走）。
                const int probeHeight = 761;
                var bottom = sidebar.DescribeBottomGeometry(SidebarPanel.ExpandedWidth, probeHeight);
                log.Add("  bottom   : " + bottom);

                var anchored = bottom.Contains("底边距=12") && !bottom.Contains("与上方按钮间隙=-1");
                var gapOk = false;
                var gapIndex = bottom.IndexOf("与上方按钮间隙=", StringComparison.Ordinal);
                if (gapIndex > 0)
                {
                    int gap;
                    var text = bottom.Substring(gapIndex + "与上方按钮间隙=".Length);
                    if (int.TryParse(text, out gap)) gapOk = gap >= 12;
                }

                log.Add("  底部按钮贴底、且不与上方按钮重叠（底边距 12 / 间隙 >= 12）    " +
                        ((anchored && gapOk) ? "-> OK" : "-> FAIL"));
                ok &= anchored && gapOk;
            }
            finally
            {
                sidebar.Dispose();
            }
            log.Add("");

            log.Add(ok ? "SELF CHECK PASSED" : "SELF CHECK FOUND PROBLEMS");

            foreach (var line in log) AppLog.Write(line);

            try
            {
                File.WriteAllLines(Path.Combine(outDir, "selfcheck.txt"), log.ToArray());
            }
            catch (Exception ex)
            {
                AppLog.Write("写入自检报告失败", ex);
                return 2;
            }

            return ok ? 0 : 1;
        }

        /// <summary>
        /// 用一个"四象限彩图"验证底图旋转的**方向**和角度。
        ///
        /// 探针图：左上红、右上绿、左下蓝、右下黄。顺时针转 90° 之后，
        /// 红块必须出现在画面的**右上角** —— 这一条就能同时验证
        /// "转的是不是顺时针"和"转的是不是 90° 而不是 270°"。
        ///
        /// 走的是完整链路：真的 new MapCanvas → SetMap → SetRotation →
        /// RenderScene → 再从位图上把像素读回来判断颜色，
        /// 而不是去看某个内部字段有没有被赋值。
        /// </summary>
        private static bool CheckRotation(List<string> log, string outDir)
        {
            const int probeW = 400;
            const int probeH = 200;
            var probePath = Path.Combine(outDir, "_rotation_probe.png");

            // 4 个采样点（画布 1200x800 的四个象限中部），按 左上 / 右上 / 左下 / 右下 排列。
            // 探针图用"拉伸铺满"，它的象限分界线正好落在画布中线上，
            // 所以这四个点分别落在底图的四个象限里，离边界都很远。
            var probes = new[]
            {
                new Point(300, 200), new Point(900, 200),
                new Point(300, 600), new Point(900, 600)
            };

            // 顺时针转 N 度之后，画布上的 左上/右上/左下/右下 应该分别是原图的哪个角
            var expected = new Dictionary<int, string[]>
            {
                { 0,   new[] { "红", "绿", "蓝", "黄" } },
                { 90,  new[] { "蓝", "红", "黄", "绿" } },
                { 180, new[] { "黄", "蓝", "绿", "红" } },
                { 270, new[] { "绿", "黄", "红", "蓝" } }
            };

            try
            {
                using (var probe = new Bitmap(probeW, probeH))
                {
                    using (var g = Graphics.FromImage(probe))
                    {
                        g.Clear(Color.Red);                                                      // 左上
                        g.FillRectangle(Brushes.Lime, probeW / 2f, 0, probeW / 2f, probeH / 2f); // 右上
                        g.FillRectangle(Brushes.Blue, 0, probeH / 2f, probeW / 2f, probeH / 2f); // 左下
                        g.FillRectangle(Brushes.Yellow,
                            probeW / 2f, probeH / 2f, probeW / 2f, probeH / 2f);                  // 右下
                    }
                    probe.Save(probePath, ImageFormat.Png);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("生成旋转探针图失败", ex);
                log.Add("  FAILED to build the probe image: " + ex.Message);
                return false;
            }

            var ok = true;

            foreach (var rotation in new[] { 0, 90, 180, 270 })
            {
                string[] want = expected[rotation];
                var got = new string[4];

                try
                {
                    using (var canvas = new MapCanvas())
                    {
                        canvas.Size = new Size(CanvasWidth, CanvasHeight);
                        // 格子开到 50cm：画面上就只剩贴边的两条线，采样点不会撞上网格线
                        canvas.SetGridSize(50);
                        canvas.SetMap(probePath, probeW, probeH, "fill");
                        canvas.SetRotation(rotation);

                        using (var scene = canvas.RenderScene())
                        {
                            // 网格不许跟着转：这一条顺带验证了"转动前后格子像素数不变"
                            var cell = canvas.CellPixels;
                            if (Math.Abs(cell - 50 * AppEnv.PixelsPerCm) > 0.01)
                            {
                                log.Add(string.Format("  rot {0,3}° : grid cell changed to {1:0.0}px", rotation, cell));
                                ok = false;
                            }

                            if (scene == null)
                            {
                                log.Add(string.Format("  rot {0,3}° : FAILED (no scene)", rotation));
                                ok = false;
                                continue;
                            }

                            for (var i = 0; i < probes.Length; i++)
                            {
                                var p = probes[i];
                                if (p.X >= scene.Width || p.Y >= scene.Height) { got[i] = "越界"; continue; }
                                got[i] = ClassifyColor(scene.GetPixel(p.X, p.Y));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("旋转自检渲染失败：" + rotation, ex);
                    log.Add(string.Format("  rot {0,3}° : FAILED ({1})", rotation, ex.Message));
                    ok = false;
                    continue;
                }

                var pass = true;
                for (var i = 0; i < 4; i++) if (got[i] != want[i]) pass = false;
                if (!pass) ok = false;

                log.Add(string.Format(
                    "  rot {0,3}° 顺时针 : 左上={1} 右上={2} 左下={3} 右下={4}  期望 左上={5} 右上={6} 左下={7} 右下={8}  -> {9}",
                    rotation, got[0], got[1], got[2], got[3], want[0], want[1], want[2], want[3],
                    pass ? "OK" : "MISMATCH"));
            }

            log.Add("  探针图：" + Path.GetFileName(probePath) + "（左上红 / 右上绿 / 左下蓝 / 右下黄）");
            return ok;
        }

        // ============================================================
        //  「网格 ↔ 底图」绑定（手机端位置对得上的根据）
        // ============================================================

        /// <summary>
        /// 验证随 map_change 一起下发的那 4 个锚点数字，真能把电脑端的网格搬到手机上。
        ///
        /// 分两段验，合起来才闭环：
        ///
        ///  1. **规格**：cellsX/cellsY 必须等于「底图在当前显示模式下的宽/高 ÷ 一格像素」，
        ///     halfW/halfH 必须等于「画布半宽/半高 ÷ 一格像素」。这里按上面那句文档
        ///     **独立**算一遍，而不是去调 MapCanvas 里已经算好的字段 —— 否则就是自己
        ///     证明自己。halfW/halfH 用画布尺寸、且**不随旋转互换**（它们是屏幕空间的量），
        ///     cellsX/cellsY 用**逻辑**视口（转 90/270 时宽高互换）—— 这两点最容易搞反。
        ///
        ///  2. **搬运**：照抄手机端 wwwroot/js/player.js 里 computeGridFrame 的那套公式，
        ///     把「某张图块贴在底图的哪个位置」从锚点反推出来，与电脑端直接算出来的
        ///     位置比。判据用**底图上的比例**（0 = 底图左边，1 = 底图右边），
        ///     因为两端画布尺寸差一个数量级，比例是唯一可比的量。
        ///     相位差一格、或者把 cellW/cellH 用错，这里都会露馅。
        ///
        /// 「电脑端把 (X,Y) 格的图块画在屏幕 (X*cell, Y*cell)」这个前提本身由
        /// <see cref="CheckDrawing"/> 真的渲染出来回读像素验证，此处不重复。
        /// </summary>
        private static bool CheckGridAnchor(List<string> log, string outDir)
        {
            const int cw = CanvasWidth;      // 1200
            const int ch = CanvasHeight;     // 800
            const int mw = 500;              // 底图刻意取一个和画布不同的比例
            const int mh = 320;              // 这样 fill / contain 的差别才看得出来
            const double cm = 2.5;

            // 手机端画布。它只影响"一格多少像素"，不影响比例判据，随便取个常见的竖屏尺寸。
            const double phoneW = 390;
            const double phoneH = 844;

            var probePath = Path.Combine(outDir, "_anchor_probe.png");
            try
            {
                using (var bmp = new Bitmap(mw, mh))
                {
                    using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(255, 40, 90, 160));
                    bmp.Save(probePath, ImageFormat.Png);
                }
            }
            catch (Exception ex)
            {
                log.Add("  锚点探针底图生成失败：" + ex.Message);
                return false;
            }

            var ok = true;
            var modes = new[] { "fill", "contain" };
            var rotations = new[] { 0, 90, 180, 270 };

            // 抽查的格子：原点那一格、偏远处一格、负坐标一格（网格往左上延伸）
            var probes = new[]
            {
                new[] { 0, 0 }, new[] { 4, 3 }, new[] { 12, 6 }, new[] { -2, -1 }
            };

            const double eps = 0.004;        // 格数上的容差，约等于百分之一毫米

            foreach (var mode in modes)
            {
                foreach (var rotation in rotations)
                {
                    GridAnchor anchor;
                    double cellPx;
                    try
                    {
                        using (var canvas = new MapCanvas())
                        {
                            canvas.Size = new Size(cw, ch);
                            canvas.SetGridSize(cm);
                            canvas.SetMap(probePath, mw, mh, mode);
                            canvas.SetRotation(rotation);
                            anchor = canvas.AnchorSnapshot;
                            cellPx = canvas.CellPixels;
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Add(string.Format("  {0,-7} rot {1,3}° 抛异常：{2}", mode, rotation, ex.Message));
                        ok = false;
                        continue;
                    }

                    var tag = string.Format("{0,-7} rot {1,3}°", mode, rotation);

                    if (anchor == null || !anchor.Valid)
                    {
                        log.Add(string.Format("  {0}  锚点无效", tag));
                        ok = false;
                        continue;
                    }

                    // ---- 1) 规格 ----
                    var quarter = rotation == 90 || rotation == 270;
                    var viewW = quarter ? (double)ch : cw;   // 逻辑视口，转 90/270 时宽高互换
                    var viewH = quarter ? (double)cw : ch;
                    var scale = mode == "contain"
                        ? Math.Min(viewW / mw, viewH / mh)
                        : Math.Max(viewW / mw, viewH / mh);

                    var expCellsX = mw * scale / cellPx;
                    var expCellsY = mh * scale / cellPx;
                    var expHalfW = cw / (2.0 * cellPx);      // 屏幕空间：永远用画布自己宽高
                    var expHalfH = ch / (2.0 * cellPx);

                    var specOk =
                        Math.Abs(anchor.CellsX - expCellsX) < eps &&
                        Math.Abs(anchor.CellsY - expCellsY) < eps &&
                        Math.Abs(anchor.HalfCellsW - expHalfW) < eps &&
                        Math.Abs(anchor.HalfCellsH - expHalfH) < eps;
                    ok &= Assert(log, tag + " 锚点符合规格", specOk, anchor.Describe());
                    if (!specOk)
                    {
                        log.Add(string.Format(
                            "        期望 底图 {0:0.###}x{1:0.###} 格  画布半宽 {2:0.###} 半高 {3:0.###}",
                            expCellsX, expCellsY, expHalfW, expHalfH));
                        continue;
                    }

                    // ---- 2) 搬运：按手机端 player.js 的公式反推 ----
                    // 手机端"底图在 wrapper 里实际占的矩形"。注意电脑端 fill 对应 CSS cover
                    // （等比放大到盖满），contain 对应 contain —— 都保持比例。
                    var phoneQuarter = ((rotation + 90) % 360) == 90 || ((rotation + 90) % 360) == 270;
                    var boxW = phoneQuarter ? phoneH : phoneW;
                    var boxH = phoneQuarter ? phoneW : phoneH;
                    var ps = mode == "contain"
                        ? Math.Min(boxW / mw, boxH / mh)
                        : Math.Max(boxW / mw, boxH / mh);
                    var fitW = mw * ps;
                    var fitH = mh * ps;

                    var phoneCellW = fitW / anchor.CellsX;
                    var phoneCellH = fitH / anchor.CellsY;
                    var ox = -anchor.HalfCellsW * phoneCellW;
                    var oy = -anchor.HalfCellsH * phoneCellH;

                    // 手机端 ctx.rotate(-mapRotation)：canvas 的 rotate 是
                    // (x,y) -> (x*cos - y*sin, x*sin + y*cos)，把角度取负即可。
                    var nr = -rotation * Math.PI / 180.0;
                    var cn = Math.Cos(nr);
                    var sn = Math.Sin(nr);

                    var worstU = 0.0;
                    var worstV = 0.0;
                    foreach (var p in probes)
                    {
                        var cxCell = p[0];
                        var cyCell = p[1];

                        // 电脑端：图块左上角在屏幕 (X*cell, Y*cell)，换算成
                        // "相对画布中心、再转过 -rotation"的逻辑坐标。
                        var dx = cxCell * cellPx - cw / 2.0;
                        var dy = cyCell * cellPx - ch / 2.0;
                        // 电脑端 GDI+ 是 RotateTransform(rotation)，逆变换就是转 -rotation
                        var rr = rotation * Math.PI / 180.0;
                        var cr = Math.Cos(rr);
                        var sr = Math.Sin(rr);
                        var pcLX = dx * cr + dy * sr;      // 逻辑坐标（相对逻辑画布中心）
                        var pcLY = -dx * sr + dy * cr;

                        // 电脑端底图上的比例：底图在逻辑视口里居中，所以
                        // 比例 = 逻辑偏移 / 底图显示尺寸 + 0.5
                        var pcU = pcLX / (anchor.CellsX * cellPx) + 0.5;
                        var pcV = pcLY / (anchor.CellsY * cellPx) + 0.5;

                        // 手机端：同一点在旋转后坐标系 F 里的位置
                        var ux = ox + cxCell * phoneCellW;
                        var uy = oy + cyCell * phoneCellH;
                        var fx = ux * cn - uy * sn;
                        var fy = ux * sn + uy * cn;

                        // F 的原点是底图中心，底图占 [-fitW/2, fitW/2]
                        var phU = fx / fitW + 0.5;
                        var phV = fy / fitH + 0.5;

                        worstU = Math.Max(worstU, Math.Abs(pcU - phU));
                        worstV = Math.Max(worstV, Math.Abs(pcV - phV));
                    }

                    var moveOk = worstU < 0.002 && worstV < 0.002;
                    ok &= Assert(log, tag + " 手机端位置可复原", moveOk,
                        string.Format("底图上最大偏差 {0:0.#####} / {1:0.#####}（占底图宽/高的比例）",
                            worstU, worstV));
                }
            }

            log.Add("  说明：偏差 = 电脑端算出的位置与手机端按锚点复原出的位置，在底图上的比例之差。");
            return ok;
        }

        // ============================================================
        //  网格线颜色
        // ============================================================

        /// <summary>5.0 cm/格 → 189.0 px，正好是整数像素，回读时不会踩半像素抗锯齿。</summary>
        private const double GridColorProbeCm = 5.0;

        /// <summary>探针画布的采样行。避开横向网格线（y = 0 / 189 / 378 / 567 / 756）。</summary>
        private const int GridColorProbeRow = 400;

        /// <summary>
        /// 验证"纯白 / 纯黑网格线"这件事从头到尾都成立：
        ///
        ///   1. <see cref="MapTone"/> 对亮底图必须判成黑线、对暗底图必须判成白线；
        ///   2. 真的渲染出来，把像素读回来 —— 黑线在**亮底图**上必须留下深色像素，
        ///      白线在**暗底图**上必须留下亮色像素；
        ///   3. 反过来的组合（白线铺亮底图、黑线铺暗底图）必须**几乎看不出线** ——
        ///      这一条是"颜色真的换了"，而不是"怎么画都有线"。
        ///
        /// 探针底图用纯色，所以"线上像素"和"线外像素"的差别只可能来自网格线本身。
        /// </summary>
        private static bool CheckGridColor(List<string> log, string outDir)
        {
            const int probeSize = 200;
            var lightPath = Path.Combine(outDir, "_grid_probe_light.png");
            var darkPath = Path.Combine(outDir, "_grid_probe_dark.png");

            try
            {
                SaveSolid(lightPath, probeSize, Color.FromArgb(240, 236, 224)); // 米白，像亮色地板
                SaveSolid(darkPath, probeSize, Color.FromArgb(30, 30, 34));     // 近黑，像暗洞窟
            }
            catch (Exception ex)
            {
                AppLog.Write("生成网格颜色探针图失败", ex);
                log.Add("  FAILED to build the probe images: " + ex.Message);
                return false;
            }

            var ok = true;

            // ---- 1. 自动判定 ----
            MapTone.ClearCache();
            var lightTone = MapTone.Analyze(lightPath);
            var darkTone = MapTone.Analyze(darkPath);

            if (!lightTone.Ok || !darkTone.Ok)
            {
                log.Add("  自动判定失败：探针图读不出来");
                return false;
            }

            var lightSaysWhite = MapTone.PrefersWhite(lightTone);
            var darkSaysWhite = MapTone.PrefersWhite(darkTone);

            // 亮底图该配黑线（PrefersWhite = false），暗底图该配白线（PrefersWhite = true）
            var lightAutoOk = !lightSaysWhite;
            var darkAutoOk = darkSaysWhite;
            if (!lightAutoOk || !darkAutoOk) ok = false;

            log.Add(string.Format("  自动判定  亮底图 mean={0:0.00} → {1}  期望 黑线  -> {2}   (解析 {3} ms)",
                lightTone.Mean, lightSaysWhite ? "白线" : "黑线", lightAutoOk ? "OK" : "MISMATCH",
                lightTone.ElapsedMs));
            log.Add(string.Format("  自动判定  暗底图 mean={0:0.00} → {1}  期望 白线  -> {2}",
                darkTone.Mean, darkSaysWhite ? "白线" : "黑线", darkAutoOk ? "OK" : "MISMATCH"));

            // 第二次必须命中缓存（切底图时不至于每次都重新解码一遍）
            var cached = MapTone.Analyze(lightPath);
            var cacheOk = cached.FromCache;
            if (!cacheOk) ok = false;
            log.Add(string.Format("  缓存命中  同一张图第二次分析 → {0}  -> {1}",
                cached.FromCache ? "命中缓存" : "又解码了一次", cacheOk ? "OK" : "MISMATCH"));

            // ---- 2. 真的渲染出来，再把像素读回来 ----
            // 判据是"采样行上有没有出现明显偏离底色的像素"：
            //   亮底图上只可能"变暗"（黑线），暗底图上只可能"变亮"（白线）。
            // 渲染结果一并存盘，方便出问题时直接看像素。
            ok &= CheckGridRender(log, outDir, "black-on-light", "黑线 × 亮底图（应该看得见）", lightPath, "black", true);
            ok &= CheckGridRender(log, outDir, "white-on-light", "白线 × 亮底图（应该看不见）", lightPath, "white", true);
            ok &= CheckGridRender(log, outDir, "white-on-dark", "白线 × 暗底图（应该看得见）", darkPath, "white", false);
            ok &= CheckGridRender(log, outDir, "black-on-dark", "黑线 × 暗底图（应该看不见）", darkPath, "black", false);

            // ---- 3. 本机真实底图逐张判定（只报告，不参与对错：审美因人而异）----
            log.Add("  本机底图的自动判定（只报告）：");
            ReportMapTones(log, outDir);

            return ok;
        }

        /// <summary>
        /// 渲染一格网格并回读采样行，检查"线"到底有没有从底色里显出来。
        ///
        /// 判据是**相对底色**的：采样行的底色取这一行亮度的**中位数**
        /// （网格线只占 1200 个像素里的十来个，中位数必然是底色），
        /// 然后数"比底色暗 0.10 以上"和"比底色亮 0.10 以上"的像素各有几个。
        ///
        /// *** 为什么要用中位数而不是固定阈值 ***
        /// 固定阈值根本区分不了"看得见"和"看不见"：
        ///   * 白线铺在 0.93 的亮底图上 → 线像素 0.985，比底色只亮 0.06 —— 就是**看不见**，
        ///     但固定阈值 0.35 会把它算成"提亮像素"（其实整行 1200 个像素都 > 0.35）。
        /// 用"相对中位数偏离 0.10"就自然多了：
        ///   * 黑线 × 亮底图：线像素 0.56，比底色暗 0.36 → 算得出来；
        ///   * 白线 × 亮底图：偏差 0.06 → 算不出来（这正是"看不见"的定义）。
        ///
        /// 最外侧 2 列不采样：底图缩放层在边界上有 1 像素的双三次滤波接缝，
        /// 那点压暗跟网格线无关，会污染"亮底图上不该有暗像素"这一条。
        /// </summary>
        private static bool CheckGridRender(List<string> log, string outDir, string fileName,
                                            string label, string probePath,
                                            string color, bool probeIsLight)
        {
            const int probeSize = 200;
            const int edge = 2;             // 跳过最外侧这么多列
            const double deviation = 0.10;  // 比底色偏离多少才算"这条线看得见"

            try
            {
                using (var canvas = new MapCanvas())
                {
                    canvas.Size = new Size(CanvasWidth, CanvasHeight);
                    canvas.SetGridSize(GridColorProbeCm);
                    canvas.SetMap(probePath, probeSize, probeSize, "fill");
                    canvas.SetGridColor(color);

                    using (var scene = canvas.RenderScene())
                    {
                        if (scene == null)
                        {
                            log.Add("  " + label + " : FAILED (no scene)");
                            return false;
                        }

                        var lo = edge;
                        var hi = scene.Width - edge;
                        var colors = new Color[scene.Width];
                        var lumas = new double[scene.Width];
                        var sorted = new List<double>(hi - lo);

                        for (var x = lo; x < hi; x++)
                        {
                            colors[x] = scene.GetPixel(x, GridColorProbeRow);
                            lumas[x] = Luma(colors[x]);
                            sorted.Add(lumas[x]);
                        }

                        sorted.Sort();
                        var background = sorted[sorted.Count / 2];

                        try { scene.Save(Path.Combine(outDir, "_grid_" + fileName + ".png"), ImageFormat.Png); }
                        catch (Exception saveEx) { AppLog.Write("保存网格颜色探针渲染失败", saveEx); }

                        var darker = 0;
                        var brighter = 0;
                        var darkest = 2.0;
                        var brightest = -1.0;
                        var darkestX = -1;
                        var brightestX = -1;
                        var neutral = true;

                        for (var x = lo; x < hi; x++)
                        {
                            var v = lumas[x];
                            var isDarker = v < background - deviation;
                            var isBrighter = v > background + deviation;

                            if (isDarker)
                            {
                                darker++;
                                if (v < darkest) { darkest = v; darkestX = x; }
                            }
                            if (isBrighter)
                            {
                                brighter++;
                                if (v > brightest) { brightest = v; brightestX = x; }
                            }

                            // "纯白 / 纯黑"要求线上像素是中性的（R≈G≈B），不能带色偏
                            if ((isDarker || isBrighter) &&
                                (Math.Abs(colors[x].R - colors[x].G) > 12 ||
                                 Math.Abs(colors[x].G - colors[x].B) > 12))
                                neutral = false;
                        }

                        // 亮底图上只可能"变暗"（黑线），暗底图上只可能"变亮"（白线）
                        var contrast = probeIsLight ? darker : brighter;
                        var expectContrast = probeIsLight
                            ? color == "black"      // 亮底图 + 黑线 → 必须看得见
                            : color == "white";     // 暗底图 + 白线 → 必须看得见

                        var pass = neutral && (expectContrast ? contrast > 0 : contrast == 0);

                        log.Add(string.Format(
                            "  {0,-24} 底色={1:0.00}  更暗={2,3}(最深 {3:0.00}@x={4})  更亮={5,3}(最亮 {6:0.00}@x={7})  中性={8}  -> {9}",
                            label, background, darker, darkest, darkestX,
                            brighter, brightest, brightestX,
                            neutral ? "是" : "否", pass ? "OK" : "MISMATCH"));

                        return pass;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("网格颜色渲染自检失败：" + label, ex);
                log.Add("  " + label + " : FAILED (" + ex.Message + ")");
                return false;
            }
        }

        /// <summary>逐张报告本机底图会被判成什么颜色（最多列 <paramref name="limit"/> 张）。</summary>
        /// <summary>
        /// 列出 maps 目录里每张底图的平均亮度（只报告、不断言），方便对着数字看
        /// "某张图为什么被判成白线"。
        ///
        /// *** 按亮度从高到低排，而不是按文件名 ***
        /// 这里截断到 limit 行。原来按文件名（也就是目录枚举）顺序取前 20 张，
        /// 结果**最亮的那张、也是最可能被判成黑线的那张被截掉了** ——
        /// 报告里只剩一片"白线"，最该看到的那一行反而看不到。
        /// 现在按 mean 降序排，被截掉的永远是"最暗、结论最不意外"的那几张。
        ///
        /// 另外把"贴阈值"的图标出来（|mean - 0.5| <= 0.05）：这些是自动判定最可能
        /// 判得不合心意、值得手动改的候选。
        /// </summary>
        private static void ReportMapTones(List<string> log, string outDir)
        {
            const int limit = 40;
            const double borderline = 0.05;

            List<MapInfo> maps;
            try
            {
                maps = new MapStore().List();
            }
            catch (Exception ex)
            {
                AppLog.Write("扫描底图色调失败", ex);
                log.Add("    （扫描底图失败）");
                return;
            }

            if (maps.Count == 0)
            {
                log.Add("    （maps 目录里没有底图）");
                return;
            }

            var rows = new List<KeyValuePair<MapInfo, ToneSample>>();
            var white = 0;
            var black = 0;
            foreach (var m in maps)
            {
                var tone = MapTone.Analyze(m.Path);
                rows.Add(new KeyValuePair<MapInfo, ToneSample>(m, tone));
                if (MapTone.PrefersWhite(tone)) white++; else black++;
            }

            rows.Sort(delegate (KeyValuePair<MapInfo, ToneSample> a, KeyValuePair<MapInfo, ToneSample> b)
            {
                // 降序：最亮的排最前面
                return b.Value.Mean.CompareTo(a.Value.Mean);
            });

            // 文件名里有中文，命令行窗口按 ANSI 显示会乱码 —— 所以结论主要看
            // 写进 selfcheck.txt 的那一份，这里同时写日志。
            var shown = 0;
            foreach (var row in rows)
            {
                if (shown >= limit) break;
                var tone = row.Value;
                var mark = string.Empty;
                if (!tone.Ok) mark = "  [读取失败]";
                else if (Math.Abs(tone.Mean - MapTone.Threshold) <= borderline) mark = "  [贴阈值,可手动改]";

                log.Add(string.Format("    {0,-26} mean={1:0.00}  暗={2:0.00} 亮={3:0.00}  → {4}{5}",
                    row.Key.Name, tone.Mean, tone.DarkFraction, tone.LightFraction,
                    MapTone.PrefersWhite(tone) ? "白线" : "黑线", mark));
                shown++;
            }

            if (rows.Count > shown)
                log.Add("    …另有 " + (rows.Count - shown) + " 张更暗的未列出（不影响结论）");

            log.Add(string.Format("    合计 {0} 张：白线 {1} 张 / 黑线 {2} 张（阈值 mean >= {3:0.00} 判黑线）",
                rows.Count, white, black, MapTone.Threshold));
            log.Add("    （表按亮度从高到低排；标 [贴阈值] 的图是自动判定最可能不合心意、值得手动改的）");
        }

        // ============================================================
        //  WebP（自研 VP8 解码器）
        // ============================================================

        /// <summary>
        /// 底图换成 webp 之后 GDI+ 直接罢工（<c>Image.FromStream</c> 对 .webp 一律抛异常），
        /// 表现出来就是"预览和设置地图都是黑的"。这一段钉死"自研解码器真的解得出图"：
        ///
        ///   1. **尺寸对得上** —— 解码出来的宽高必须和只读文件头量到的宽高一致；
        ///   2. **不是一片糊** —— 亮度标准差与不同亮度级数要够多。纯黑、纯色、
        ///      或者预测/反变换写错导致的"能出图但整块死板"都会让这两项塌下去。
        ///      只断言"没抛异常"是抓不出后者的。
        ///   3. **和别的格式走同一条路** —— <see cref="ImageLoader"/> 是底图 / 缩略图 /
        ///      色调分析共用的入口，这里断言它也能打开 webp。
        ///
        /// 另外把面积最大的那张 webp 的解码结果导出成 PNG 放到自检目录，
        /// 方便拿外部解码器逐像素对拍（见脚本里的对拍流程）。
        /// </summary>
        private static bool CheckWebpDecode(List<string> log, string outDir)
        {
            var ok = true;

            List<MapInfo> maps;
            try
            {
                maps = new MapStore().List();
            }
            catch (Exception ex)
            {
                AppLog.Write("扫描 webp 底图失败", ex);
                return Assert(log, "webp 扫描", false, "扫描 maps 目录失败");
            }

            var webps = new List<MapInfo>();
            foreach (var m in maps)
            {
                if (m.Path != null && m.Path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                    webps.Add(m);
            }

            if (webps.Count == 0)
            {
                log.Add("    （maps 目录里没有 webp，跳过；换成 webp 之后再跑一次）");
                return true;
            }

            var dumpPath = Path.Combine(outDir, "_webp_decode.png");
            var dumpWidth = 0;
            var dumpHeight = 0;
            var dumpFile = string.Empty;
            var totalMs = 0L;

            foreach (var m in webps)
            {
                var header = ImageSizeReader.Read(m.Path);
                Image image = null;
                string error = null;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var loaded = ImageLoader.TryLoad(m.Path, out image, out error);
                watch.Stop();
                totalMs += watch.ElapsedMilliseconds;

                if (!loaded)
                {
                    ok &= Assert(log, "webp 解码 " + m.Name, false, "解码失败：" + error);
                    continue;
                }

                using (image)
                {
                    var sizeOk = header != null
                                 && image.Width == header.Width
                                 && image.Height == header.Height;

                    double mean, stdDev;
                    int levels;
                    MeasurePixels(image, out mean, out stdDev, out levels);

                    // 一张真实底图：亮度标准差不会低于 0.02，亮度级数不会少于 24 级。
                    // 这两个门槛都留在"明显不对"的量级上，不去卡正常图的波动。
                    var contentOk = stdDev >= 0.02 && levels >= 24;
                    var pass = sizeOk && contentOk;

                    ok &= Assert(log, "webp 解码 " + m.Name, pass,
                        string.Format("{0}x{1} 头={2}x{3} 亮度均={4:0.000} 标准差={5:0.000} 级数={6} {7}ms",
                            image.Width, image.Height,
                            header == null ? -1 : header.Width, header == null ? -1 : header.Height,
                            mean, stdDev, levels, watch.ElapsedMilliseconds));

                    if (image.Width * image.Height > dumpWidth * dumpHeight)
                    {
                        dumpWidth = image.Width;
                        dumpHeight = image.Height;
                        dumpFile = m.Name;
                        try
                        {
                            image.Save(dumpPath, ImageFormat.Png);
                            DumpPlanes(m.Path, outDir);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Write("导出 webp 解码结果失败", ex);
                        }
                    }
                }
            }

            log.Add(string.Format("    共 {0} 张 webp，合计解码 {1} ms（平均 {2} ms/张）",
                webps.Count, totalMs, webps.Count == 0 ? 0 : totalMs / webps.Count));
            if (dumpWidth > 0)
            {
                log.Add("    对拍样本：" + dumpFile + " → " + Path.GetFileName(dumpPath) +
                        " + _webp_y/u/v.raw（拿外部解码器解同一张 webp 逐像素比）");
            }

            // 统一入口对 webp 的判定：文件头嗅探必须认出来（扩展名改了也一样认）
            var probe = File.ReadAllBytes(webps[0].Path);
            ok &= Assert(log, "webp 文件头嗅探", ImageLoader.IsWebpData(probe) && ImageLoader.HasWebpExtension(webps[0].Path),
                "IsWebpData=" + ImageLoader.IsWebpData(probe));

            return ok;
        }

        /// <summary>
        /// 素材（绘图贴图）预览缩略图自检。
        ///
        /// 第十七轮踩的坑：底图换成 webp 之后，**底图缩略图改了、素材缩略图漏了**。
        /// DrawPanel 里还留着一份自己写的 <c>Image.FromStream</c> —— GDI+ 不认 webp，
        /// 每张都抛异常 → 缩略图变 null → 素材列表里 14 项全退化成一块深色背景。
        /// 用户的原话很准：「地图上画得出来，素材预览里全是黑的」。
        /// （底图那条路 MapCanvas / TerrainImageCache 走了统一入口，所以地图上是对的。）
        ///
        /// 这一条钉死两件事，任一塌了都说明缩略图又绕开了 <see cref="ImageLoader"/>：
        ///   1. 三个素材目录里**每一张**都出得来缩略图（不抛异常、宽高比正确）；
        ///   2. 缩略图**不是一块死色** —— 亮度标准差与亮度级数要够。
        ///      纯底色正是"加载失败退化"的样子，而它**不会**抛异常，
        ///      所以只断言"没报错"是抓不出来的，必须回读像素。
        ///
        /// 顺手把全部缩略图拼成一张 _asset_thumbs.png 存到自检目录，
        /// 肉眼也能一眼确认"预览里有图"。
        /// </summary>
        private static bool CheckAssetThumbnails(List<string> log, string outDir)
        {
            TerrainCatalog catalog;
            try
            {
                catalog = new TerrainCatalog();
            }
            catch (Exception ex)
            {
                AppLog.Write("扫描素材目录失败", ex);
                return Assert(log, "素材目录扫描", false, "扫描失败：" + ex.Message);
            }

            var items = new List<TerrainAsset>();
            foreach (var kind in DrawKind.All)
            {
                foreach (var asset in catalog.Of(kind)) items.Add(asset);
            }

            if (items.Count == 0)
            {
                log.Add("    （terrain / entity / item 三个目录里都没有素材，跳过）");
                return true;
            }

            var ok = true;
            var drawn = 0;
            var flat = 0;

            // 拼版图用 2 倍放大：缩略图本身只有 36px，原大小贴出来看不清
            const int thumbBox = 36;
            const int tile = thumbBox * 2;
            const int caption = 15;
            const int cols = 5;
            const int pad = 10;
            const int gap = 8;
            const int titleHeight = 20;

            var thumbs = new Image[items.Count];

            try
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var asset = items[i];

                    try
                    {
                        thumbs[i] = ImageLoader.LoadThumbnail(asset.Path, thumbBox, Theme.Background);
                        drawn++;
                    }
                    catch (Exception ex)
                    {
                        ok &= Assert(log, "素材缩略图 " + asset.File, false, "解码失败：" + ex.Message);
                        continue;
                    }

                    double mean, stdDev;
                    int levels;
                    MeasurePixels(thumbs[i], out mean, out stdDev, out levels);

                    // 门槛卡在"明显就是一块死色（= 底色）"的量级，不去苛求低对比度的素材。
                    var pass = stdDev >= ThumbMinStdDev && levels >= ThumbMinLevels;
                    if (!pass) flat++;

                    ok &= Assert(log, "素材缩略图 " + asset.Kind + "/" + asset.File, pass,
                        string.Format("{0}x{1} 亮度均={2:0.000} 标准差={3:0.000} 级数={4}",
                            thumbs[i].Width, thumbs[i].Height, mean, stdDev, levels));
                }

                log.Add(string.Format("    共 {0} 张素材：出图 {1} 张，疑似死色 {2} 张",
                    items.Count, drawn, flat));

                // ---- 反证：GDI+ 直读同一个文件会怎样 ----
                // 把"为什么必须有 ImageLoader"钉成一条可复现的结论。
                // 刻意**不做成断言**：万一将来某个 Windows 的 GDI+ 真支持了 webp，
                // 那也不该让自检变红 —— 只是上面对比门槛可以放松而已。
                var naiveText = "未知";
                try
                {
                    using (var fs = File.OpenRead(items[0].Path))
                    using (var naive = Image.FromStream(fs))
                    {
                        naiveText = "成功（" + naive.Width + "x" + naive.Height +
                                    "）—— 这台机器的 GDI+ 已认识 webp";
                    }
                }
                catch (Exception ex)
                {
                    naiveText = "抛异常（" + ex.GetType().Name + "）—— 绕开 ImageLoader 的读图点都会变黑";
                }
                log.Add("    反证：GDI+ 的 Image.FromStream 直读 " + items[0].File + " → " + naiveText);

                // ---- 拼版图（纯证据，画不出来不影响结论） ----
                var sheetPath = Path.Combine(outDir, "_asset_thumbs.png");
                try
                {
                    var rows = (items.Count + cols - 1) / cols;
                    var width = pad * 2 + cols * tile + (cols - 1) * gap;
                    var height = pad * 2 + titleHeight + rows * (tile + caption) + (rows - 1) * gap;

                    using (var sheet = new Bitmap(width, height))
                    {
                        using (var g = Graphics.FromImage(sheet))
                        {
                            g.Clear(Theme.Background);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                            using (var titleBrush = new SolidBrush(Theme.Text))
                            {
                                g.DrawString(
                                    "素材预览缩略图（" + items.Count + " 张，全部经 ImageLoader.LoadThumbnail）",
                                    Ui.Font(9f, FontStyle.Bold), titleBrush, pad, pad - 3);
                            }

                            using (var textBrush = new SolidBrush(Theme.TextDim))
                            using (var tileBrush = new SolidBrush(Theme.Card))
                            {
                                for (var i = 0; i < items.Count; i++)
                                {
                                    var col = i % cols;
                                    var row = i / cols;
                                    var x = pad + col * (tile + gap);
                                    var y = pad + titleHeight + row * (tile + caption + gap);

                                    g.FillRectangle(tileBrush, x, y, tile, tile);
                                    if (thumbs[i] != null) g.DrawImage(thumbs[i], x, y, tile, tile);

                                    g.DrawString(items[i].Name, Ui.Font(7.5f), textBrush,
                                        new RectangleF(x, y + tile + 1, tile, caption));
                                }
                            }
                        }
                        sheet.Save(sheetPath, ImageFormat.Png);
                    }

                    log.Add("    拼版图：" + Path.GetFileName(sheetPath) + "（" +
                            items.Count + " 张缩略图，肉眼复核）");
                }
                catch (Exception ex)
                {
                    AppLog.Write("导出素材缩略图拼版失败", ex);
                    log.Add("    （拼版图导出失败，见 app.log）");
                }
            }
            finally
            {
                foreach (var image in thumbs)
                {
                    if (image != null) image.Dispose();
                }
            }

            return ok;
        }

        /// <summary>
        /// 素材缩略图"不是一块死色"的门槛。
        ///
        /// 纯 <see cref="Theme.Background"/> 底色（= 加载失败退化的样子）标准差 ≈ 0、
        /// 亮度级数 = 1；而任何一张真实贴图缩到 36px 都远高于这两个数。
        /// 门槛刻意留在"一眼就是死色"的量级上，不去卡正常的低对比度素材。
        /// </summary>
        private const double ThumbMinStdDev = 0.02;
        private const int ThumbMinLevels = 8;

        /// <summary>
        /// 把对拍样本的 YUV 平面原样导出。外部解码器（libwebp / Pillow）把同一张图转成
        /// YCbCr 就能逐平面比 —— 出问题时一眼能看出是亮度错、色度错，还是都对（那就是
        /// YUV→RGB 的系数差异，属正常舍入）。
        /// </summary>
        private static void DumpPlanes(string path, string outDir)
        {
            int w, h;
            Vp8Planes planes;
            Vp8FrameDecoder.DecodeWebp(File.ReadAllBytes(path), out w, out h, out planes);

            WriteRawPlane(Path.Combine(outDir, "_webp_y.raw"), planes.Y, planes.YBase, planes.YStride, w, h);

            var cw = (w + 1) / 2;
            var ch = (h + 1) / 2;
            WriteRawPlane(Path.Combine(outDir, "_webp_u.raw"), planes.U, planes.UvBase, planes.UvStride, cw, ch);
            WriteRawPlane(Path.Combine(outDir, "_webp_v.raw"), planes.V, planes.UvBase, planes.UvStride, cw, ch);
        }

        private static void WriteRawPlane(string path, byte[] plane, int baseIdx, int stride, int w, int h)
        {
            using (var fs = new FileStream(path, FileMode.Create))
            {
                var row = new byte[w];
                for (var y = 0; y < h; y++)
                {
                    Buffer.BlockCopy(plane, baseIdx + y * stride, row, 0, w);
                    fs.Write(row, 0, w);
                }
            }
        }

        /// <summary>
        /// 量一张位图的亮度均值 / 标准差 / 出现过的亮度级数（0..255 共 256 级）。
        /// 走 LockBits 一次拷进托管数组，不用 GetPixel —— 后者每像素约 1 微秒，
        /// 一张 1672x941 要 1.5 秒，二十张就是半分钟。
        /// </summary>
        private static void MeasurePixels(Image image, out double mean, out double stdDev, out int levels)
        {
            var bmp = image as Bitmap;
            if (bmp == null) bmp = new Bitmap(image);

            var w = bmp.Width;
            var h = bmp.Height;
            var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            var seen = new bool[256];
            var sum = 0.0;
            var sumSq = 0.0;
            var count = 0;

            try
            {
                var rowBytes = w * 4;
                var row = new byte[rowBytes];
                for (var y = 0; y < h; y++)
                {
                    var src = (IntPtr)(bits.Scan0.ToInt64() + (long)y * bits.Stride);
                    Marshal.Copy(src, row, 0, rowBytes);

                    for (var x = 0; x < w; x++)
                    {
                        var i = x * 4;
                        // Rec.601，和 MapTone / 网格颜色判定用的是同一套权重
                        var luma = (int)Math.Round(0.299 * row[i + 2] + 0.587 * row[i + 1] + 0.114 * row[i]);
                        if (luma < 0) luma = 0;
                        else if (luma > 255) luma = 255;

                        seen[luma] = true;
                        sum += luma;
                        sumSq += (double)luma * luma;
                        count++;
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bits);
            }

            if (count == 0)
            {
                mean = 0;
                stdDev = 0;
                levels = 0;
            }
            else
            {
                var avg = sum / count;
                var variance = sumSq / count - avg * avg;
                mean = avg / 255.0;
                stdDev = Math.Sqrt(variance < 0 ? 0 : variance) / 255.0;
                levels = 0;
                for (var i = 0; i < 256; i++) if (seen[i]) levels++;
            }

            if (!ReferenceEquals(bmp, image)) bmp.Dispose();
        }

        // ============================================================
        //  绘图（地形 / 实体 / 物品 + 笔刷形状）
        // ============================================================

        /// <summary>绘图自检用的格子边长：5.0cm → 189.0px，整像素，回读时不会踩半像素。</summary>
        private const double DrawProbeCm = 5.0;

        /// <summary>
        /// 绘图功能的自检。分四段：
        ///
        ///   1. **吸附与形状**（纯数学）：半格吸附、距离换算、四种形状各覆盖哪些格，
        ///      以及**严格 / 宽泛两种选区方式**的格数。这些都是纯函数，可以在完全没有窗口的
        ///      情况下断言到个位数。
        ///   2. **图层语义**：地形/物品一格一层、实体不限层、地形永远排在实体与物品之前、
        ///      橡皮与清空、以及"存盘再读回"的往返一致。
        ///   2b.**按素材清空**：面板上的「清空本类」（行为=按素材）只摘掉点名的那一张素材，
        ///      同一类里的其它素材、别的分类都要原样保留（旧版这里是按分类清，等于"清空全部"）。
        ///   3. **真的画出来**：用一张**真实的**素材图落到指定格子上，再把像素读回来，
        ///      验证贴图确实落在那一格、相邻格是干净的、选区预览是半透明红 ——
        ///      光断言"内部字段被赋值了"是看不出错位的。
        /// </summary>
        private static bool CheckDrawing(List<string> log, string outDir)
        {
            var ok = true;
            const double cell = DrawProbeCm * AppEnv.PixelsPerCm;   // 189.0

            // ---- 1. 吸附与距离 ----
            try
            {
                // 格心：格 (1,1) 的中心在像素 (1.5*cell, 1.5*cell)，半格坐标应该是 (3,3)
                var center = DrawGeometry.Snap(1.5 * cell, 1.5 * cell, cell);
                ok &= Assert(log, "格心吸附", center.HX == 3 && center.HY == 3 && center.IsCellCenter,
                    center.HX + "," + center.HY + " 格心=" + center.IsCellCenter);

                // 网格线交点：像素 (2*cell, 3*cell) → 半格 (4,6)，必是偶数、不是格心
                var cross = DrawGeometry.Snap(2 * cell, 3 * cell, cell);
                ok &= Assert(log, "交点吸附", cross.HX == 4 && cross.HY == 6 && !cross.IsCellCenter,
                    cross.HX + "," + cross.HY + " 格心=" + cross.IsCellCenter);

                // 格边中点：像素 (2*cell, 2.5*cell) → 半格 (4,5)
                var edge = DrawGeometry.Snap(2 * cell, 2.5 * cell, cell);
                ok &= Assert(log, "边中点吸附", edge.HX == 4 && edge.HY == 5 && !edge.IsCellCenter,
                    edge.HX + "," + edge.HY);

                // 轴向：格心 (1,1) → 格心 (4,1) 是 3 格 = 15 尺
                var a = new SnapPoint(3, 3);
                var b = new SnapPoint(9, 3);
                var feet = DrawGeometry.FeetBetween(a, b);
                ok &= Assert(log, "轴向距离 3 格", Math.Abs(feet - 15.0) < 0.001, feet + " 尺");

                // 斜向：半格差 (6,8) → 10 半格 = 25 尺（3-4-5 的 6-8-10 版）
                var diagonal = DrawGeometry.FeetBetween(new SnapPoint(0, 0), new SnapPoint(6, 8));
                ok &= Assert(log, "斜向距离 (6,8) 半格", Math.Abs(diagonal - 25.0) < 0.001, diagonal + " 尺");

                // 线形：同一行相邻 4 格的格心之间应该正好扫到 4 格
                var line = DrawGeometry.LineCells(new SnapPoint(1, 1), new SnapPoint(7, 1), cell);
                ok &= Assert(log, "线形扫格 4 格", line.Count == 4, line.Count + " 格");

                // 球形：半径 3 半格（7.5 尺）从格心出发 → 3x3 = 9 格
                var sphere3 = DrawGeometry.Region(BrushShape.Sphere, new SnapPoint(7, 7),
                    new SnapPoint(13, 7), 3, 0, 90, cell);
                ok &= Assert(log, "球形 r=3 半格 → 9 格", sphere3.Count == 9, sphere3.Count + " 格");

                // 半径 4 半格 → 13 格（5x5 去掉四个角）
                var sphere4 = DrawGeometry.Region(BrushShape.Sphere, new SnapPoint(7, 7),
                    new SnapPoint(15, 7), 4, 0, 90, cell);
                ok &= Assert(log, "球形 r=4 半格 → 13 格", sphere4.Count == 13, sphere4.Count + " 格");

                // 立方：半边长 4 半格（2 格）→ 5x5 = 25 格
                var cube = DrawGeometry.Region(BrushShape.Cube, new SnapPoint(7, 7),
                    new SnapPoint(15, 7), 4, 0, 90, cell);
                ok &= Assert(log, "立方 半边长 4 半格 → 25 格", cube.Count == 25, cube.Count + " 格");

                // 锥形：朝 +x、张角 90°，正前方的格在内、背后的格在外
                var cone = DrawGeometry.Region(BrushShape.Cone, new SnapPoint(7, 7),
                    new SnapPoint(15, 7), 4, 0, 90, cell);
                var hasFront = false;
                var hasBehind = false;
                foreach (var c in cone)
                {
                    if (c.X > 3 && c.Y == 3) hasFront = true;
                    if (c.X < 3 && c.Y == 3) hasBehind = true;
                }
                ok &= Assert(log, "锥形 90° 朝 +x 只向前", hasFront && !hasBehind,
                    "前=" + hasFront + " 后=" + hasBehind + " 共" + cone.Count + " 格");

                // 张角 360° 时扇形应该等于同半径的整圆 —— 顺带把"背后的格"也覆盖到了。
                // （180° 的扇形的半张角正好是 90°，正后方的格在边界上，本来就**不该**选中。）
                var full = DrawGeometry.Region(BrushShape.Cone, new SnapPoint(7, 7),
                    new SnapPoint(15, 7), 4, 0, 360, cell);
                var behind360 = false;
                foreach (var c in full) if (c.X < 3 && c.Y == 3) behind360 = true;
                ok &= Assert(log, "锥形 360° = 整圆（含背后）",
                    behind360 && full.Count == sphere4.Count,
                    "后=" + behind360 + " 共" + full.Count + " 格（整圆 " + sphere4.Count + " 格）");

                // 角度差
                ok &= Assert(log, "角度差 350°↔10° = 20°",
                    Math.Abs(DrawGeometry.AngleDiff(350, 10) - 20) < 0.001,
                    DrawGeometry.AngleDiff(350, 10).ToString("0.0"));

                // ---- 选区方式：严格 vs 宽泛 ----
                //
                // 期望值是手工推算出来的（格 (x,y) 的矩形 = [2x,2x+2]×[2y,2y+2]，原点取格心 (7,7)）：
                //   球形 r=4 半格：严格 = a²+b²<=4 → 13 格；
                //                  宽泛 = 圆心到格矩形最近点距离 <4 → a=0 时 b∈-2..2、a=±1 同、
                //                  a=±2 时 b∈-1..1 → 5+10+6 = 21 格（最近点 1.5 / 1.58，都真的压在圆里）
                //   球形 r=3 半格：严格 9；宽泛**也是 9** ——
                //                  a=±2 / b=±2 那 4 格的最近点距离正好 =3，属于"圆与格边相切"，
                //                  一格面积都没盖到，按用户的反馈**不算选中**（原先多算成 13）。
                //   立方 r=4 半格（2 格）：正方形边界落在格**心**线上 → 两种方式都是 5×5 = 25 格
                //   立方 r=3 半格（1.5 格）：正方形边界正好压在格**线**上 → 外面那一圈相切、不算，
                //                  两种方式都是 3×3 = 9 格（原先宽泛多算成 25）
                //   锥形 r=4 / 朝 +x / 张角 90°：严格 5；
                //                  宽泛 7 —— 90° 的两条边界射线正好是 ±45°，会压着格角走，
                //                  只从格角蹭过去的格同样不算（原先多算成 9）
                var o7 = new SnapPoint(7, 7);
                var t15 = new SnapPoint(15, 7);

                var sphereStrict = DrawGeometry.Region(BrushShape.Sphere, o7, t15, 4, 0, 90, cell, false);
                var sphereLoose = DrawGeometry.Region(BrushShape.Sphere, o7, t15, 4, 0, 90, cell, true);
                ok &= Assert(log, "球形 r=4：严格 13 格 / 宽泛 21 格",
                    sphereStrict.Count == 13 && sphereLoose.Count == 21,
                    "严格=" + sphereStrict.Count + " 宽泛=" + sphereLoose.Count);

                var sphere3Loose = DrawGeometry.Region(BrushShape.Sphere, o7,
                    new SnapPoint(13, 7), 3, 0, 90, cell, true);
                ok &= Assert(log, "球形 r=3：严格 9 格 / 宽泛 9 格（相切的 4 格不算）",
                    sphere3Loose.Count == 9 && sphere3.Count == 9,
                    "严格=" + sphere3.Count + " 宽泛=" + sphere3Loose.Count);

                // 专门盯住用户反馈的那 4 格：半径 1.5 格（7.5 尺）时，正上下左右的第 2 格
                // 只是和圆**相切**，整格都在圆外，必须不在宽泛里。
                var flushStuck = 0;
                foreach (var c in sphere3Loose)
                    if ((c.X == 1 || c.X == 5) && c.Y == 3) flushStuck++;
                foreach (var c in sphere3Loose)
                    if ((c.Y == 1 || c.Y == 5) && c.X == 3) flushStuck++;
                ok &= Assert(log, "球形 r=3：轴向第 2 格（相切）不被宽泛选中", flushStuck == 0,
                    "多出来 " + flushStuck + " 格");

                var cubeLoose = DrawGeometry.Region(BrushShape.Cube, o7, t15, 4, 0, 90, cell, true);
                ok &= Assert(log, "立方 r=4：宽泛与严格同为 25 格（边界落在格心线上，没相切）",
                    cubeLoose.Count == 25 && cube.Count == 25,
                    "严格=" + cube.Count + " 宽泛=" + cubeLoose.Count);

                var cube3 = DrawGeometry.Region(BrushShape.Cube, o7,
                    new SnapPoint(13, 7), 3, 0, 90, cell, false);
                var cube3Loose = DrawGeometry.Region(BrushShape.Cube, o7,
                    new SnapPoint(13, 7), 3, 0, 90, cell, true);
                ok &= Assert(log, "立方 r=3：严格 9 格 / 宽泛 9 格（边界压在格线上，外圈相切不算）",
                    cube3Loose.Count == 9 && cube3.Count == 9,
                    "严格=" + cube3.Count + " 宽泛=" + cube3Loose.Count);

                var coneLoose = DrawGeometry.Region(BrushShape.Cone, o7, t15, 4, 0, 90, cell, true);
                var coneLooseBehind = false;
                foreach (var c in coneLoose) if (c.X < 3) coneLooseBehind = true;
                ok &= Assert(log, "锥形 90° 朝 +x：严格 5 格 / 宽泛 7 格且不向后溢出",
                    coneLoose.Count == 7 && cone.Count == 5 && !coneLooseBehind,
                    "严格=" + cone.Count + " 宽泛=" + coneLoose.Count + " 向后=" + coneLooseBehind);

                // 扇形边界射线正好是 ±45° 时，正上/正下那两格只被射线蹭到格角 —— 不算
                var coneFlush = 0;
                foreach (var c in coneLoose) if (c.X == 3 && (c.Y == 2 || c.Y == 4)) coneFlush++;
                ok &= Assert(log, "锥形 90°：只被边界射线蹭到格角的上/下 2 格不算", coneFlush == 0,
                    "多出来 " + coneFlush + " 格");

                // 宽泛必须**包住**严格：格心在形状里 ⇒ 格矩形当然和形状有交集。
                // 这条要是断了，说明宽泛那套判定漏掉了一整类格，红色反而比严格少。
                var looseCoversStrict = true;
                foreach (var c in sphereStrict)
                {
                    var found = false;
                    foreach (var d in sphereLoose) { if (d.X == c.X && d.Y == c.Y) { found = true; break; } }
                    if (!found) { looseCoversStrict = false; break; }
                }
                ok &= Assert(log, "宽泛 ⊇ 严格（球形 r=4）", looseCoversStrict,
                    "严格的 " + sphereStrict.Count + " 格全在宽泛里");

                // 线形：它本来就是"线段扫过哪些格"，两种方式同解
                var lineLoose = DrawGeometry.Region(BrushShape.Line, new SnapPoint(1, 1),
                    new SnapPoint(7, 1), 3, 0, 90, cell, true);
                ok &= Assert(log, "线形：严格与宽泛同解", lineLoose.Count == line.Count,
                    "严格=" + line.Count + " 宽泛=" + lineLoose.Count);

                // 张角 360° 的宽泛 = 同半径的宽泛整圆（走的是"整圆"那条分支）
                var fullLoose = DrawGeometry.Region(BrushShape.Cone, o7, t15, 4, 0, 360, cell, true);
                ok &= Assert(log, "锥形 360° 宽泛 = 宽泛整圆",
                    fullLoose.Count == sphereLoose.Count,
                    "扇形=" + fullLoose.Count + " 整圆=" + sphereLoose.Count);
            }
            catch (Exception ex)
            {
                AppLog.Write("绘图几何自检失败", ex);
                log.Add("  FAILED (geometry): " + ex.Message);
                ok = false;
            }

            // ---- 2. 图层语义与持久化 ----
            var storePath = Path.Combine(outDir, "_drawing_test.json");
            try { if (File.Exists(storePath)) File.Delete(storePath); }
            catch { /* 忽略 */ }

            var store = new DrawingStore(storePath);
            var first = new List<GridCell> { new GridCell(2, 2), new GridCell(3, 2) };

            try
            {
                store.Paint(DrawKind.Terrain, first, "甲.png");
                store.Paint(DrawKind.Terrain, first, "乙.png");     // 同格再画一次 → 覆盖
                ok &= Assert(log, "地形覆盖（一格一层）", store.Count == 2, store.Count + " 格");

                store.Paint(DrawKind.Item, new List<GridCell> { new GridCell(2, 2) }, "箱.png");
                store.Paint(DrawKind.Item, new List<GridCell> { new GridCell(2, 2) }, "桶.png");
                ok &= Assert(log, "物品覆盖（一格一层）", store.Count == 3, store.Count + " 格");

                store.Paint(DrawKind.Entity, new List<GridCell> { new GridCell(2, 2) }, "哥布林.png");
                store.Paint(DrawKind.Entity, new List<GridCell> { new GridCell(2, 2) }, "哥布林.png");
                ok &= Assert(log, "实体不限层数（叠 2 层）", store.Count == 5, store.Count + " 格");

                // 快照顺序：地形必须全部排在实体/物品之前
                var snapshot = store.Snapshot();
                var sawUpper = false;
                var orderOk = true;
                foreach (var sprite in snapshot)
                {
                    if (DrawKind.Layer(sprite.Kind) > 0) sawUpper = true;
                    else if (sawUpper) orderOk = false;         // 地形跑到实体/物品后面去了
                }
                ok &= Assert(log, "图层顺序（地形在下）", orderOk && sawUpper,
                    "共 " + snapshot.Count + " 张贴图");

                // 地形那一格写的是后来那张（覆盖生效），物品那一格写的是后来那张
                string terrainFile = null;
                string itemFile = null;
                foreach (var sprite in store.Snapshot())
                {
                    if (sprite.Kind == DrawKind.Terrain && sprite.X == 2 && sprite.Y == 2) terrainFile = sprite.File;
                    if (sprite.Kind == DrawKind.Item && sprite.X == 2 && sprite.Y == 2) itemFile = sprite.File;
                }
                ok &= Assert(log, "覆盖后的内容是最新那张",
                    terrainFile == "乙.png" && itemFile == "桶.png",
                    "地形=" + terrainFile + " 物品=" + itemFile);

                // 橡皮：擦掉地形那一格
                store.Erase(DrawKind.Terrain, new List<GridCell> { new GridCell(2, 2) });
                ok &= Assert(log, "橡皮擦掉地形一格", !store.HasAny(DrawKind.Terrain, 2, 2)
                    && store.HasAny(DrawKind.Terrain, 3, 2), "剩 " + store.Count + " 格");

                // 清空实体：地形要保留
                var removed = store.ClearKind(DrawKind.Entity);
                ok &= Assert(log, "清空实体后地形保留",
                    removed == 2 && store.HasAny(DrawKind.Terrain, 3, 2) && !store.HasAny(DrawKind.Entity, 2, 2),
                    "清掉 " + removed + " 个实体，剩 " + store.Count + " 格");

                // ---- 往返 ----
                var before = DrawingSequence(store);
                ok &= Assert(log, "存盘成功", store.Save(), storePath);
                var reloaded = new DrawingStore(storePath);
                ok &= Assert(log, "读回后内容与顺序一致",
                    reloaded.Count == store.Count && DrawingSequence(reloaded) == before,
                    "读回 " + reloaded.Count + " 格（原 " + store.Count + " 格）");
            }
            catch (Exception ex)
            {
                AppLog.Write("绘图图层自检失败", ex);
                log.Add("  FAILED (layers): " + ex.Message);
                ok = false;
            }

            // ---- 2b. 按素材清空（面板上那个按钮，文字叫「清空本类」）----
            //
            // 旧版这个按钮走的是 ClearKind（按分类清）：地形类里同时铺了「高草丛」和「火焰」时，
            // 点一下两样一起没了 —— 和「清空全部」看不出区别，等于没有这个按钮。
            // 现在必须只摘掉**点名的那一张**，同类的别的素材和别的分类一格都不能动。
            var assetStorePath = Path.Combine(outDir, "_drawing_asset_test.json");
            try { if (File.Exists(assetStorePath)) File.Delete(assetStorePath); }
            catch { /* 忽略 */ }

            try
            {
                var pick = new DrawingStore(assetStorePath);
                pick.Paint(DrawKind.Terrain,
                    new List<GridCell> { new GridCell(1, 1), new GridCell(2, 1) }, "高草丛.png");
                pick.Paint(DrawKind.Terrain, new List<GridCell> { new GridCell(3, 1) }, "火焰.png");
                pick.Paint(DrawKind.Entity, new List<GridCell> { new GridCell(1, 1) }, "哥布林.png");

                ok &= Assert(log, "按素材清空的基数（草 2 格 + 火 1 格 + 实体 1 格）",
                    pick.Count == 4, pick.Count + " 格");
                ok &= Assert(log, "本素材格数统计",
                    pick.CountAsset(DrawKind.Terrain, "高草丛.png") == 2
                    && pick.CountAsset(DrawKind.Terrain, "火焰.png") == 1,
                    "草=" + pick.CountAsset(DrawKind.Terrain, "高草丛.png") +
                    " 火=" + pick.CountAsset(DrawKind.Terrain, "火焰.png"));

                var gone = pick.ClearAsset(DrawKind.Terrain, "高草丛.png");
                ok &= Assert(log, "「清空本类」只摘掉这一张",
                    gone == 2
                    && pick.CountAsset(DrawKind.Terrain, "高草丛.png") == 0
                    && pick.CountAsset(DrawKind.Terrain, "火焰.png") == 1
                    && pick.CountAsset(DrawKind.Entity, "哥布林.png") == 1,
                    "清掉 " + gone + " 格，剩 " + pick.Count + " 格（火焰与实体保留）");

                ok &= Assert(log, "「清空本类」不碰别的分类",
                    pick.HasAny(DrawKind.Terrain, 3, 1) && pick.HasAny(DrawKind.Entity, 1, 1),
                    "剩 " + pick.Count + " 格");

                // 名字对不上时必须是个纯粹的空操作 —— 以前"清空本类"就是在这里把整类清光的
                var none = pick.ClearAsset(DrawKind.Terrain, "根本没有这张.png");
                ok &= Assert(log, "清空不存在的素材 = 0 改动",
                    none == 0 && pick.Count == 2, "改动 " + none + " 格，剩 " + pick.Count + " 格");

                // 传空文件名也一样：不能变成"清空整类"的后门
                var blank = pick.ClearAsset(DrawKind.Terrain, null);
                ok &= Assert(log, "清空空文件名 = 0 改动", blank == 0 && pick.Count == 2,
                    "改动 " + blank + " 格，剩 " + pick.Count + " 格");
            }
            catch (Exception ex)
            {
                AppLog.Write("绘图按素材清空自检失败", ex);
                log.Add("  FAILED (clear asset): " + ex.Message);
                ok = false;
            }

            // ---- 3. 真的渲染出来，再读像素 ----
            var catalog = new TerrainCatalog();
            var assets = catalog.Of(DrawKind.Terrain);
            log.Add("  素材目录：terrain=" + assets.Count +
                    "  entity=" + catalog.Of(DrawKind.Entity).Count +
                    "  item=" + catalog.Of(DrawKind.Item).Count +
                    "  （目录：" + TerrainCatalog.FolderFor(DrawKind.Terrain) + "）");

            if (assets.Count == 0)
            {
                log.Add("  （terrain 目录里没有素材，跳过渲染像素检查）");
            }
            else
            {
                ok &= CheckDrawingRender(log, outDir, catalog, assets[0], cell);
            }

            // ---- 4. 笔刷"实际形状"那一层（半透明蓝） ----
            ok &= CheckBrushOutline(log, outDir);

            return ok;
        }

        /// <summary>
        /// 用真实素材渲一张画布，再逐像素回读：
        ///  - 贴图必须落在指定的那一格（相邻格必须是干净的底色）；
        ///  - **同一张图分别画在第 2 格和第 5 格时，它相对各自格子左边界的内缩量必须一样**
        ///    —— 这一条同时验证"没有整格错位"和"多格之后没有累积漂移"，
        ///    而且不要求素材本身是不透明的（AI 生成的地形图边缘大多有透明羽化，
        ///    直接断言"最左边一列就有内容"会误报）；
        ///  - 当前选区预览必须是半透明红（R 明显高于 G/B），且选区外的格子不被染色；
        ///  - 距离读数方块必须真的画出来了（在光标右上角找到那一圈亮黄描边）。
        /// </summary>
        private static bool CheckDrawingRender(List<string> log, string outDir,
            TerrainCatalog catalog, TerrainAsset asset, double cell)
        {
            const int cellX = 2;
            const int cellY = 3;
            const int farX = 5;                 // 同一行的另一格，用来做"内缩量一致"的对照

            var storePath = Path.Combine(outDir, "_drawing_render.json");
            try { if (File.Exists(storePath)) File.Delete(storePath); }
            catch { /* 忽略 */ }

            var ok = true;

            try
            {
                var store = new DrawingStore(storePath);
                store.Paint(DrawKind.Terrain, new List<GridCell>
                {
                    new GridCell(cellX, cellY),
                    new GridCell(farX, cellY)
                }, asset.File);

                using (var cache = new TerrainImageCache(catalog))
                using (var canvas = new MapCanvas())
                {
                    canvas.Size = new Size(CanvasWidth, CanvasHeight);
                    canvas.SetGridSize(DrawProbeCm);
                    canvas.SetMap(null, 0, 0, "fill");
                    canvas.SetDrawings(store, cache, catalog);

                    using (var scene = canvas.RenderScene())
                    {
                        if (scene == null)
                        {
                            log.Add("  FAILED: no scene produced");
                            return false;
                        }

                        scene.Save(Path.Combine(outDir, "_drawing_terrain.png"), ImageFormat.Png);

                        var background = Theme.Background;

                        // 采样点选在格子正中，离四条网格线都有 90 多像素
                        int cx, cy, nx, ny;
                        SampleCellCenter(scene, cell, cellX, cellY, out cx, out cy);
                        SampleCellCenter(scene, cell, cellX + 1, cellY, out nx, out ny);

                        var drawn = CountNonBackground(scene, cx, cy, 25, background);
                        var empty = CountNonBackground(scene, nx, ny, 25, background);

                        ok &= Assert(log, "贴图落在指定格（51x51 采样区有内容）", drawn > 200,
                            drawn + " / 2601 个像素不是底色");
                        ok &= Assert(log, "相邻格是干净的（同一张图没有溢出）", empty == 0,
                            empty + " / 2601 个像素不是底色");

                        // 内缩量一致性：同一张图在两个不同格子里，左边的透明留白应该一样
                        var insetNear = FirstContentInset(scene, cell, cellX, cellY, background);
                        var insetFar = FirstContentInset(scene, cell, farX, cellY, background);

                        ok &= Assert(log, "贴图左边界对齐（两格内缩量一致，±2px）",
                            insetNear >= 0 && insetFar >= 0 && Math.Abs(insetNear - insetFar) <= 2,
                            "第" + cellX + "格内缩=" + insetNear + "px  第" + farX + "格内缩=" + insetFar + "px");

                        // 选区预览：为了看清"半透明红"，先清掉贴图，只留预览。
                        //
                        // *** 采样点必须避开原点标记与候选落点 ***
                        // 原点和候选点是亮黄色的，正好压在格心上的话，回读到的就是那圈黄边
                        // （第一版就踩了这个坑：整圆半径 4 半格时"正后方"的格心恰好是候选点）。
                        //
                        // 关于像素换算务必记住：半格点 (hx,hy) → 像素 (hx*cell/2, hy*cell/2)，
                        // 因为 1 格 = 2 个半格。所以原点 (3,13) 实际在 x=141.75、y=614.25，
                        // 竖排候选点整列落在 x≈141.75，离选区（第 2、3 列，x=189..378）还有
                        // 一段距离；横排候选点在 y≈614.25，也不会碰到选区（y=94.5..189）。
                        store.ClearAll();

                        var preview = new DrawPreview();
                        preview.Cells = new List<GridCell> { new GridCell(2, 1), new GridCell(3, 1) };
                        preview.HasOrigin = true;
                        preview.Origin = new SnapPoint(3, 13);
                        preview.HasHover = true;
                        preview.Hover = new SnapPoint(3, 7);
                        preview.Guides = DrawGeometry.GuidePoints(preview.Origin, 10);
                        preview.DistanceText = "15 尺（3 格）";
                        preview.Cursor = new Point(560, 620);
                        canvas.Preview = preview;

                        using (var withPreview = canvas.RenderScene())
                        {
                            withPreview.Save(Path.Combine(outDir, "_drawing_preview.png"), ImageFormat.Png);

                            int px, py;
                            SampleCellCenter(withPreview, cell, 2, 1, out px, out py);
                            var overlay = withPreview.GetPixel(px, py);

                            // 底色是 #1A1A1A 的深灰；叠 27% 的纯红之后 R 应该明显高于 G/B
                            var reddish = overlay.R - overlay.G > 40 && overlay.R - overlay.B > 40;
                            // 同时要能透出底色 —— 如果画成了不透明红，G 会接近 0、R 接近 255
                            var translucent = overlay.G > 10 && overlay.R < 200;

                            ok &= Assert(log, "选区是半透明红（能透出底色）", reddish && translucent,
                                "格(2,1)心像素 R=" + overlay.R + " G=" + overlay.G + " B=" + overlay.B);

                            // 没被选中的格子不能被染色
                            int ux, uy;
                            SampleCellCenter(withPreview, cell, 5, 3, out ux, out uy);
                            var untouched = withPreview.GetPixel(ux, uy);
                            ok &= Assert(log, "选区外的格子没被染色",
                                IsBackground(untouched, background),
                                "格(5,3)心像素 R=" + untouched.R + " G=" + untouched.G + " B=" + untouched.B);

                            // 候选落点：原点竖线上应该能看到亮黄的小方块。
                            // 采样框必须按**半格**换算定位（见上面的说明）：(3,7) → (141.75, 330.75)。
                            var guidePixel = DrawGeometry.ToPixel(preview.Hover, cell);
                            var guideArea = new Rectangle(
                                (int)Math.Round(guidePixel.X) - 8,
                                (int)Math.Round(guidePixel.Y) - 8, 17, 17);
                            ok &= Assert(log, "横竖候选落点可见",
                                CountGuidePixels(withPreview, guideArea) > 3,
                                "在 (3,7) 半格点附近找到 " +
                                CountGuidePixels(withPreview, guideArea) + " 个候选点像素");

                            // 距离读数方块：在光标右上角那一小块里找亮黄的描边/文字
                            var hudArea = new Rectangle(preview.Cursor.X, preview.Cursor.Y - 80, 420, 80);
                            var hudPixels = CountHudPixels(withPreview, hudArea);
                            ok &= Assert(log, "距离读数方块可见", hudPixels > 20,
                                "找到 " + hudPixels + " 个读数方块像素");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("绘图渲染自检失败", ex);
                log.Add("  FAILED (render): " + ex.Message);
                ok = false;
            }
            finally
            {
                try { if (File.Exists(storePath)) File.Delete(storePath); }
                catch { /* 忽略 */ }
            }

            return ok;
        }

        /// <summary>
        /// 笔刷"实际形状"层（半透明蓝）的离屏回读验证。
        ///
        /// 这一层最怕两种做错的方式，而它们都能被下面这些断言当场抓住：
        ///   1) 把形状画成了**方框包围盒**（而不是真圆 / 扇形）——
        ///      所以扫描要沿 0°/90°/180°/270°/45° 五个方向量"蓝色最外圈到圆心的距离"，
        ///      真圆的五个距离必须一样，方框的 45° 会是 √2 倍；
        ///   2) 把形状糊到了**红色覆盖格之外**本该干净的地方，或者反过来，
        ///      形状伸到没被选中的格子里却没画 —— 一正一反两条断言卡住。
        ///
        /// 圆心刻意取单元格心（半格坐标两个都是奇数），这样"格 (a,b) 相对圆心的偏移"
        /// 就是纯粹的整数格数，写断言时不用心算半格。
        /// </summary>
        private static bool CheckBrushOutline(List<string> log, string outDir)
        {
            // 这块探针刻意用一张比 SelfCheck 常规画布更大的离屏位图：
            // 半径 2.5 格（472.5 px）的圆要完整落在画布里，四根射线才都能量到边。
            const int probeW = 1600;
            const int probeH = 1200;
            const double cm = 5.0;
            const double cell = cm * AppEnv.PixelsPerCm;          // 189.0
            const int originCellX = 4;                            // 圆心所在格
            const int originCellY = 3;
            const int radiusHalf = 5;                             // 半格 → 2.5 格
            const double radiusPx = radiusHalf * cell / 2.0;      // 472.5

            var origin = new SnapPoint(originCellX * 2 + 1, originCellY * 2 + 1);
            var center = new PointF(
                (float)((originCellX + 0.5) * cell),
                (float)((originCellY + 0.5) * cell));

            var ok = true;
            var background = Theme.Background;

            try
            {
                using (var canvas = new MapCanvas())
                {
                    canvas.Size = new Size(probeW, probeH);
                    canvas.SetGridSize(cm);
                    canvas.SetMap(null, 0, 0, "fill");
                    canvas.SetDrawings(null, null, null);

                    // ---- 球形 / 圆形：五个方向的"蓝色最外圈"距离必须一致 ----
                    // Cells 用和真实笔刷同一条代码路径算出来，这样探针图就是
                    // "红覆盖格 + 蓝实际形状"两层叠在一起的真实样子，可以睁眼看。
                    var sphere = new DrawPreview();
                    sphere.HasOrigin = true;
                    sphere.Origin = origin;
                    sphere.Shape = BrushShape.Sphere;
                    sphere.HasShapeOutline = true;
                    sphere.RadiusHalf = radiusHalf;
                    sphere.Cells = DrawGeometry.Region(BrushShape.Sphere, origin, origin,
                        radiusHalf, 0.0, 90.0, cell);
                    canvas.Preview = sphere;

                    using (var scene = canvas.RenderScene())
                    {
                        scene.Save(Path.Combine(outDir, "_brush_shape_sphere.png"), ImageFormat.Png);

                        var probes = new[] { 0.0, 90.0, 180.0, 270.0, 45.0 };
                        var worst = 0.0;
                        var measured = new List<string>();
                        foreach (var angle in probes)
                        {
                            var d = BlueRimDistance(scene, center, angle, 0.0, 700);
                            measured.Add(angle.ToString("0") + "°=" + d.ToString("0.0"));
                            if (d < 0) { worst = 999; continue; }
                            worst = Math.Max(worst, Math.Abs(d - radiusPx));
                        }

                        // 描边宽 2.5px、以圆周为中轴，所以量到的是"外沿" ≈ 半径 + 1.25，
                        // 再算上抗锯齿与取整，6px 的容差足够严 —— 方框在 45° 上会偏出 195px。
                        ok &= Assert(log, "蓝圆五个方向半径一致（真圆不是方框）", worst <= 6.0,
                            string.Join(" ", measured.ToArray()) + "  期望≈" + radiusPx.ToString("0.0"));

                        // ---- 红蓝重叠区：两层必须都在 ----
                        // 格 (5,3) 相对圆心偏 1 格，格心在圆内、也在红色覆盖里。
                        // 判据是"R 和 B 都明显高于 G"：纯红层 B == G，纯蓝层 R 很低，
                        // 只有两层都画上去才会两头都抬起来（实测 (85,61,84) 左右）。
                        int ox2, oy2;
                        SampleCellCenter(scene, cell, originCellX + 1, originCellY, out ox2, out oy2);
                        var both = scene.GetPixel(ox2, oy2);
                        ok &= Assert(log, "重叠区红蓝两层都看得见",
                            both.R - both.G > 15 && both.B - both.G > 15,
                            "格(" + (originCellX + 1) + "," + originCellY + ")心 R-G=" +
                            (both.R - both.G) + " B-G=" + (both.B - both.G));
                    }

                    // ---- 立方：轴向上是半径，45° 上必须是 √2 倍（这正是方框的特征） ----
                    var cube = new DrawPreview();
                    cube.HasOrigin = true;
                    cube.Origin = origin;
                    cube.Shape = BrushShape.Cube;
                    cube.HasShapeOutline = true;
                    cube.RadiusHalf = radiusHalf;
                    cube.Cells = DrawGeometry.Region(BrushShape.Cube, origin, origin,
                        radiusHalf, 0.0, 90.0, cell);
                    canvas.Preview = cube;

                    using (var scene = canvas.RenderScene())
                    {
                        var axis = BlueRimDistance(scene, center, 0.0, 0.0, 700);
                        var corner = BlueRimDistance(scene, center, 45.0, 0.0, 900);
                        var wantCorner = radiusPx * Math.Sqrt(2.0);

                        ok &= Assert(log, "蓝方块轴向 = 半径、对角 = √2 半径",
                            Math.Abs(axis - radiusPx) <= 6.0 && Math.Abs(corner - wantCorner) <= 12.0,
                            "轴 " + axis.ToString("0.0") + "（期望 " + radiusPx.ToString("0.0") +
                            "）  对角 " + corner.ToString("0.0") + "（期望 " + wantCorner.ToString("0.0") + "）");
                    }

                    // ---- 扇形：张角 90°、朝 +x ----
                    var cone = new DrawPreview();
                    cone.HasOrigin = true;
                    cone.Origin = origin;
                    cone.Shape = BrushShape.Cone;
                    cone.HasShapeOutline = true;
                    cone.RadiusHalf = radiusHalf;
                    cone.Direction = 0.0;
                    cone.FanAngle = 90.0;
                    cone.Cells = DrawGeometry.Region(BrushShape.Cone, origin, origin,
                        radiusHalf, 0.0, 90.0, cell);
                    canvas.Preview = cone;

                    using (var scene = canvas.RenderScene())
                    {
                        scene.Save(Path.Combine(outDir, "_brush_shape_cone.png"), ImageFormat.Png);

                        // 张角外那两条射线要**跳过顶角**再开始量：扇形的两条直边在圆心处
                        // 交汇，顶角附近两根 2.5px 的描边加上抗锯齿会糊成一小团，
                        // 从 r=0 起扫的话，50° 那条射线在 r≈22 处就会扫到那团糊（已实测）。
                        // 那属于顶角必然的视觉溢出，不是"蓝色跑到了张角外"，
                        // 所以从 80px 起算 —— 半径 472.5px，跳过 17% 不影响判断。
                        var front = BlueRimDistance(scene, center, 0.0, 0.0, 700);
                        var insideEdge = BlueRimDistance(scene, center, 40.0, 0.0, 700);
                        var outsideEdge = BlueRimDistance(scene, center, 50.0, 80.0, 700);
                        var behind = BlueRimDistance(scene, center, 180.0, 0.0, 700);

                        ok &= Assert(log, "蓝扇形在张角内到半径、张角外为空",
                            Math.Abs(front - radiusPx) <= 6.0
                            && Math.Abs(insideEdge - radiusPx) <= 6.0
                            && outsideEdge < 0 && behind < 0,
                            "正前 " + front.ToString("0.0") + "  40° " + insideEdge.ToString("0.0") +
                            "  50°(跳过顶角) " + outsideEdge.ToString("0.0") +
                            "  正后 " + behind.ToString("0.0"));
                    }

                    // ---- "只有蓝、没有红"的那块区域必须真的存在 ----
                    //
                    // 这就是加这一层的全部理由：格 (6,5) 相对圆心偏 (2,2) 格，
                    // 格心离圆心 2.83 格 > 2.5 格，所以它**不在**红色覆盖格里；
                    // 但它的左上角被圆切掉一小块，那一块只可能是蓝的。
                    // 反过来，那块区域里一个红色像素都不该有 —— 红要是糊进去，
                    // 说明红色判定被改了（用户要求"红色保持不变"）。
                    var spherePreview = new DrawPreview();
                    spherePreview.HasOrigin = true;
                    spherePreview.Origin = origin;
                    spherePreview.Shape = BrushShape.Sphere;
                    spherePreview.HasShapeOutline = true;
                    spherePreview.RadiusHalf = radiusHalf;
                    spherePreview.Cells = sphere.Cells;
                    canvas.Preview = spherePreview;

                    using (var scene = canvas.RenderScene())
                    {
                        // 格 (6,5) 内缩 8px 避开网格线，再夹进画布
                        var region = new Rectangle(
                            (int)Math.Round(6 * cell) + 8,
                            (int)Math.Round(5 * cell) + 8,
                            (int)Math.Round(cell) - 16,
                            (int)Math.Round(cell) - 16);
                        region.Intersect(new Rectangle(0, 0, scene.Width, scene.Height));

                        var blue = 0;
                        var red = 0;
                        for (var y = region.Top; y < region.Bottom; y++)
                        {
                            for (var x = region.Left; x < region.Right; x++)
                            {
                                var c = scene.GetPixel(x, y);
                                if (c.B - c.G > 15) blue++;
                                if (c.R - c.B > 15) red++;
                            }
                        }

                        ok &= Assert(log, "圆伸进未选中格的那一角只有蓝、没有红",
                            blue > 20 && red == 0,
                            "格(6,5) 蓝=" + blue + "px  红=" + red + "px（共 " +
                            (region.Width * region.Height) + "px）");
                    }

                    // ---- 线形：线段中点必须是蓝的，线外一格必须是干净底色 ----
                    var line = new DrawPreview();
                    line.HasOrigin = true;
                    line.Origin = origin;
                    line.HasHover = true;
                    line.Hover = new SnapPoint(originCellX * 2 + 7, origin.HY);   // 同排右移 3 格
                    line.Shape = BrushShape.Line;
                    line.HasShapeOutline = true;
                    line.RadiusHalf = 0;
                    line.Cells = DrawGeometry.LineCells(origin, line.Hover, cell);
                    canvas.Preview = line;

                    using (var scene = canvas.RenderScene())
                    {
                        // 线段是水平的，中点在圆心右侧 1.5 格处。
                        // 采样要沿着 x 偏半格（1.25 格），因为**整格整数倍的 x 正好压在网格线上**
                        // —— 第一版取 1.5 格 (x=1134=6×189) 就踩到了，回读到的是白网格线。
                        var midX = (int)Math.Round(center.X + 1.25 * cell);
                        var midY = (int)Math.Round(center.Y);

                        var maxBlue = -999;
                        for (var dy = -6; dy <= 6; dy++)
                        {
                            var c = scene.GetPixel(midX, midY + dy);
                            maxBlue = Math.Max(maxBlue, c.B - c.G);
                        }

                        // 线外一格：取格 (5,2) 的格心 —— 线只扫过第 3 行，
                        // 而且格心（半格坐标奇数）永远不会落在网格线上。
                        int offX, offY;
                        SampleCellCenter(scene, cell, 5, 2, out offX, out offY);
                        var offLine = scene.GetPixel(offX, offY);

                        ok &= Assert(log, "蓝线段画在中点、线外一格是干净底色",
                            maxBlue > 15 && IsBackground(offLine, background),
                            "中点 B-G=" + maxBlue + "  格(5,2)心=(" +
                            offLine.R + "," + offLine.G + "," + offLine.B + ")");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("笔刷实际形状自检失败", ex);
                log.Add("  FAILED (brush shape): " + ex.Message);
                ok = false;
            }

            return ok;
        }

        /// <summary>
        /// 从圆心沿某个方向往外扫，返回**最外圈偏蓝像素**到圆心的距离（像素）；
        /// 整条射线上都没有偏蓝像素则返回 -1。
        ///
        /// 角度约定：0 = 屏幕 +x，正角 = 屏幕上顺时针 —— 和 <c>DrawPreview.Direction</c>
        /// 以及 GDI+ 的 <c>AddArc</c> 用的是同一套（三者都以 y 轴朝下为前提）。
        ///
        /// <paramref name="fromRadius"/> 用来跳过圆心附近的一小圈：
        /// 扇形的两条直边在顶角交汇，那里两根描边的抗锯齿会糊成一小团，
        /// 想验证"张角外没有蓝"就必须从顶角之外量起。
        ///
        /// 判定用 <c>B - G &gt; 15</c> 而不是"看起来像蓝"：红色填充的 B 和 G 完全相等
        /// （SelectFill 的 G、B 分量都是 64），所以任何"偏蓝"都只可能来自这一层；
        /// 而亮黄色的原点标记 B 远小于 G，也不会误判。
        /// </summary>
        private static double BlueRimDistance(Bitmap scene, PointF center, double angleDeg,
            double fromRadius, double maxRadius)
        {
            var rad = angleDeg * Math.PI / 180.0;
            var dx = Math.Cos(rad);
            var dy = Math.Sin(rad);

            var last = -1.0;
            for (var r = fromRadius; r <= maxRadius; r += 0.5)
            {
                var x = (int)Math.Round(center.X + dx * r);
                var y = (int)Math.Round(center.Y + dy * r);
                if (x < 0 || y < 0 || x >= scene.Width || y >= scene.Height) break;

                var c = scene.GetPixel(x, y);
                if (c.B - c.G > 15) last = r;
            }
            return last;
        }

        private static void SampleCellCenter(Bitmap scene, double cell, int cellX, int cellY,
            out int x, out int y)
        {
            x = (int)Math.Round((cellX + 0.5) * cell);
            y = (int)Math.Round((cellY + 0.5) * cell);
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x >= scene.Width) x = scene.Width - 1;
            if (y >= scene.Height) y = scene.Height - 1;
        }

        /// <summary>
        /// 从某格左边界往右扫，返回第一列"有内容"的像素相对左边界的偏移；
        /// 整格都空则返回 -1。
        /// </summary>
        private static int FirstContentInset(Bitmap scene, double cell, int cellX, int cellY, Color background)
        {
            var left = (int)Math.Round(cellX * cell);
            var top = (int)Math.Round(cellY * cell) + 6;
            var bottom = (int)Math.Round((cellY + 1) * cell) - 6;

            for (var x = left; x < left + 40; x++)
            {
                if (x < 0 || x >= scene.Width) break;
                for (var y = top; y < bottom; y++)
                {
                    if (y < 0 || y >= scene.Height) continue;
                    if (!IsBackground(scene.GetPixel(x, y), background)) return x - left;
                }
            }
            return -1;
        }

        /// <summary>数一块区域里"像候选落点"的像素（亮黄的描边 / 格心小圆点）。</summary>
        private static int CountGuidePixels(Bitmap scene, Rectangle area)
        {
            var count = 0;
            var right = Math.Min(scene.Width, area.Right);
            var bottom = Math.Min(scene.Height, area.Bottom);

            for (var y = Math.Max(0, area.Top); y < bottom; y++)
            {
                for (var x = Math.Max(0, area.Left); x < right; x++)
                {
                    var c = scene.GetPixel(x, y);
                    // GuideLine = rgba(255,210,120,150)，压在深灰底上大约 (161,134,81)
                    if (c.R > 120 && c.G > 100 && c.B < 140 && c.R - c.B > 50) count++;
                }
            }
            return count;
        }

        /// <summary>数一块区域里"像读数方块"的像素（亮黄的描边或文字）。</summary>
        private static int CountHudPixels(Bitmap scene, Rectangle area)
        {
            var count = 0;
            var right = Math.Min(scene.Width, area.Right);
            var bottom = Math.Min(scene.Height, area.Bottom);

            for (var y = Math.Max(0, area.Top); y < bottom; y++)
            {
                for (var x = Math.Max(0, area.Left); x < right; x++)
                {
                    var c = scene.GetPixel(x, y);
                    // 读数方块用的亮黄 (255,235,150) / 边框 (255,220,80)
                    if (c.R > 190 && c.G > 160 && c.B < 190) count++;
                }
            }
            return count;
        }

        private static bool IsBackground(Color c, Color background)
        {
            return Math.Abs(c.R - background.R) < 12
                && Math.Abs(c.G - background.G) < 12
                && Math.Abs(c.B - background.B) < 12;
        }

        private static int CountNonBackground(Bitmap scene, int centerX, int centerY, int half, Color background)
        {
            var count = 0;
            for (var y = centerY - half; y <= centerY + half; y++)
            {
                if (y < 0 || y >= scene.Height) continue;
                for (var x = centerX - half; x <= centerX + half; x++)
                {
                    if (x < 0 || x >= scene.Width) continue;
                    if (!IsBackground(scene.GetPixel(x, y), background)) count++;
                }
            }
            return count;
        }

        /// <summary>
        /// 把绘图内容压成一条可比较的字符串（**保持渲染顺序**，不排序）：
        /// 顺序本身就是"谁盖住谁"，所以往返比对必须连顺序一起比。
        /// </summary>
        private static string DrawingSequence(DrawingStore store)
        {
            var parts = new List<string>();
            foreach (var sprite in store.Snapshot())
            {
                parts.Add(sprite.Kind + ":" + sprite.X + ":" + sprite.Y + ":" + sprite.File);
            }
            return string.Join("|", parts.ToArray());
        }

        /// <summary>一条断言：打印期望与实际，返回是否通过。</summary>
        private static bool Assert(List<string> log, string label, bool pass, string detail)
        {
            log.Add(string.Format("  {0,-30} {1,-40} -> {2}",
                label, detail, pass ? "OK" : "MISMATCH"));
            return pass;
        }

        private static void SaveSolid(string path, int size, Color color)
        {
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp)) g.Clear(color);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        /// <summary>Rec.601 加权亮度，0～1。和 MapTone 里用的是同一套权重。</summary>
        private static double Luma(Color c)
        {
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        }

        /// <summary>把像素归到四种探针色里最近的那一种。</summary>
        private static string ClassifyColor(Color c)
        {
            var names = new[] { "红", "绿", "蓝", "黄", "?" };
            var refs = new[]
            {
                new[] { 255, 0, 0 }, new[] { 0, 255, 0 }, new[] { 0, 0, 255 }, new[] { 255, 255, 0 }
            };

            var best = 4;
            var bestDistance = double.MaxValue;
            for (var i = 0; i < refs.Length; i++)
            {
                var dr = c.R - refs[i][0];
                var dg = c.G - refs[i][1];
                var db = c.B - refs[i][2];
                var distance = (double)dr * dr + (double)dg * dg + (double)db * db;
                if (distance < bestDistance) { bestDistance = distance; best = i; }
            }

            // 差得太远就说明这一格既不是底图也不是网格线，属于意料之外
            return bestDistance > 90 * 90 * 3 ? names[4] : names[best];
        }

        /// <summary>渲染一个画面并返回实测的"格子像素数"；失败返回 0 并记日志。</summary>
        private static double Render(string outDir, RenderCase c, List<string> log)
        {
            try
            {
                using (var canvas = new MapCanvas())
                {
                    canvas.Size = new Size(CanvasWidth, CanvasHeight);
                    canvas.SetGridSize(c.Cm);

                    var w = 0;
                    var h = 0;
                    if (!string.IsNullOrEmpty(c.MapPath))
                    {
                        var size = ImageSizeReader.Read(c.MapPath);
                        if (size != null) { w = size.Width; h = size.Height; }
                    }
                    canvas.SetMap(c.MapPath, w, h, c.Mode);

                    using (var scene = canvas.RenderScene())
                    {
                        if (scene == null)
                        {
                            log.Add("  " + c.Name + " : FAILED (no scene produced)");
                            return 0;
                        }

                        scene.Save(Path.Combine(outDir, c.Name + ".png"), ImageFormat.Png);
                        return canvas.CellPixels;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("渲染自检画面失败：" + c.Name, ex);
                log.Add("  " + c.Name + " : FAILED (" + ex.Message + ")");
                return 0;
            }
        }

        private static bool Expect(List<string> log, Dictionary<string, double> measured,
                                   string label, string key, double cm)
        {
            var expected = cm * AppEnv.PixelsPerCm;
            double actual;
            if (!measured.TryGetValue(key, out actual))
            {
                log.Add(string.Format("  {0,-26} expected {1,6:0.0}px  -> SKIPPED", label, expected));
                return true;
            }

            var diff = Math.Abs(actual - expected);
            var pass = diff < 0.01;

            log.Add(string.Format("  {0,-26} actual {1,6:0.0}px  expected {2,6:0.0}px  -> {3}",
                label, actual, expected, pass ? "OK" : "MISMATCH"));

            return pass;
        }

        /// <summary>挑 maps 目录里像素面积最大的那张底图（缩放最悬殊，最能暴露问题）。</summary>
        private static string PickBiggestMap()
        {
            try
            {
                if (!Directory.Exists(AppEnv.MapsFolder)) return null;

                string best = null;
                long bestArea = -1;

                foreach (var file in Directory.GetFiles(AppEnv.MapsFolder))
                {
                    if (!AppEnv.IsAllowedImage(file)) continue;
                    var size = ImageSizeReader.Read(file);
                    if (size == null || size.Width <= 0 || size.Height <= 0) continue;

                    var area = (long)size.Width * size.Height;
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = file;
                    }
                }
                return best;
            }
            catch (Exception ex)
            {
                AppLog.Write("扫描底图失败", ex);
                return null;
            }
        }
    }
}
