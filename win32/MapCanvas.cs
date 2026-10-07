using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace TrpgMaps
{
    /// <summary>
    /// 地图绘制控件：底层是底图，上层是网格，最上层是"面板打开时的灰色遮罩"。
    ///
    /// 关键约定：**网格的格子大小只由「cm/格」决定，与底图缩放无关。**
    /// 也就是说进度条上写 2.5cm/格，拿尺子量屏幕就该是 2.5cm，
    /// 放不放底图都一样。底图只是背景，不参与网格尺度换算。
    ///
    /// 底图可以顺时针旋转 90/180/270（见 <see cref="SetRotation"/>），
    /// **网格不跟着转** —— 它是桌面上的实体战斗网格，屏幕转了它也不该转。
    ///
    /// 性能考虑（老机器），三层缓存：
    ///  - _mapLayer：已经把底图重采样到控件尺寸的位图。重采样（双三次）非常贵，
    ///    只在换底图 / 改窗口大小 / 改显示模式时重做一次；
    ///  - _scene：底图 + 网格 的合成结果，平常每帧只做一次位块传送；
    ///  - 网格线合并成一条 GraphicsPath 一次描边，不像原来那样逐条 DrawLine。
    ///
    /// 之所以把底图层和网格分开缓存：拖进度条时只有网格在变，
    /// 如果每次都把 3840x2160 的底图重新双三次缩放一遍，老机器上会直接卡死。
    /// </summary>
    internal sealed class MapCanvas : Control
    {
        private Bitmap _scene;        // 底图 + 网格 合成
        private Bitmap _mapLayer;     // 预先缩放好的底图
        private Bitmap _mapImage;     // 原始底图

        private int _mapPixelWidth;
        private int _mapPixelHeight;
        private bool _fill = true;
        private double _cmPerCell = 2.5;
        private int _rotation;                 // 底图顺时针旋转角：0/90/180/270
        private string _gridColor = "white";   // 网格线颜色：white（纯白）/ black（纯黑）

        private bool _mapLayerDirty = true;   // 底图层需要重做
        private bool _sceneDirty = true;      // 合成结果需要重做

        private double _maskOpacity;
        private Rectangle _panelRect = Rectangle.Empty;

        // ---- 绘图层 ----
        private DrawingStore _drawings;
        private TerrainImageCache _drawCache;
        private TerrainCatalog _catalog;
        private int _drawnVersion = -1;        // 合成进 _scene 的绘图版本号
        private DrawPreview _preview;

        // 「网格 ↔ 底图」的几何绑定快照（给手机端复原用，见 GridAnchor）。
        // 整体换引用、不原地改字段，所以 HTTP 线程直接读也不会读到半新半旧的值。
        private GridAnchor _anchor;

        /// <summary>
        /// 绘图输入开关。绘图面板打开时置 true：
        ///  - 鼠标按下/移动/松开改为抛 <see cref="DrawMouseDown"/> 等事件（给绘图面板用）；
        ///  - 不再抛 <see cref="BlankClicked"/>（点画布是"画画"，不是"关面板"）。
        /// </summary>
        public bool DrawInput;

        /// <summary>遮罩可见时点击空白处（用于关闭面板）。</summary>
        public event Action BlankClicked;

        /// <summary>绘图面板打开时的原始鼠标事件。</summary>
        public event Action<MouseEventArgs> DrawMouseDown;
        public event Action<MouseEventArgs> DrawMouseMove;
        public event Action<MouseEventArgs> DrawMouseUp;

        /// <summary>鼠标离开画布（画距离提示用）。</summary>
        public event Action DrawMouseLeave;

        public MapCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            BackColor = Theme.Background;
            UpdateAnchor();
        }

        /// <summary>
        /// 当前「网格 ↔ 底图」的几何绑定快照（可能为 null / Valid=false）。
        ///
        /// 底图、窗口尺寸、网格尺度、底图旋转任一变化都会重算一份新的。
        /// 读取方（HTTP 线程生成下发载荷）拿到的始终是完整的一份，不会读到撕裂的中间态。
        ///
        /// 名字刻意**不叫 Anchor**：<see cref="System.Windows.Forms.Control.Anchor"/> 已经占了
        /// 这个名字（AnchorStyles 枚举），同名会把基类成员藏起来（CS0114），
        /// 既容易踩坑也过不了零警告要求。
        /// </summary>
        public GridAnchor AnchorSnapshot
        {
            get { return _anchor; }
        }

        /// <summary>
        /// 重算 <see cref="AnchorSnapshot"/>。
        ///
        /// 底图在画布上总是**居中**摆放（见 <see cref="ComputeImageRect"/>），
        /// 而旋转是绕画布中心做的，所以"底图中心"永远落在画布中心 ——
        /// 这正是能把绑定锚在底图中心的原因。
        /// </summary>
        private void UpdateAnchor()
        {
            var cell = CellPixels;
            if (cell <= 0.5 || Width <= 0 || Height <= 0)
            {
                _anchor = new GridAnchor(0, 0, 0, 0);
                return;
            }

            var rect = ComputeImageRect();
            _anchor = new GridAnchor(
                rect.Width / cell,
                rect.Height / cell,
                Width / (2.0 * cell),
                Height / (2.0 * cell));
        }

        /// <summary>网格尺度（cm/格）。</summary>
        public double CmPerCell
        {
            get { return _cmPerCell; }
        }

        /// <summary>当前一格在屏幕上占多少像素（等于 cm/格 × 37.8）。</summary>
        public double CellPixels
        {
            get { return _cmPerCell * AppEnv.PixelsPerCm; }
        }

        /// <summary>遮罩不透明度 0-1。</summary>
        public double MaskOpacity
        {
            get { return _maskOpacity; }
            set
            {
                var clamped = Math.Max(0, Math.Min(1, value));
                if (Math.Abs(clamped - _maskOpacity) < 0.002) return;
                _maskOpacity = clamped;
                Invalidate();
            }
        }

        /// <summary>浮空面板的矩形（用来在面板下面画一层柔和阴影）。</summary>
        public Rectangle PanelRect
        {
            get { return _panelRect; }
            set
            {
                if (_panelRect == value) return;
                _panelRect = value;
                Invalidate();
            }
        }

        /// <summary>设置底图。filePath 为 null 表示只显示网格。</summary>
        public void SetMap(string filePath, int pixelWidth, int pixelHeight, string mode)
        {
            _mapPixelWidth = pixelWidth;
            _mapPixelHeight = pixelHeight;
            _fill = !string.Equals(mode, "contain", StringComparison.OrdinalIgnoreCase);

            if (_mapImage != null)
            {
                _mapImage.Dispose();
                _mapImage = null;
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                try
                {
                    // 走统一入口：WebP 交给自研解码器，其它格式仍旧是 GDI+。
                    // 直接 Image.FromStream 的话 .webp 一律抛异常 → 只剩黑底。
                    using (var image = ImageLoader.Load(filePath))
                    {
                        _mapImage = new Bitmap(image);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("加载底图失败：" + filePath, ex);
                    _mapImage = null;
                    _mapPixelWidth = 0;
                    _mapPixelHeight = 0;
                }
            }
            else
            {
                _mapPixelWidth = 0;
                _mapPixelHeight = 0;
            }

            _mapLayerDirty = true;
            _sceneDirty = true;
            UpdateAnchor();
            Invalidate();
        }

        /// <summary>
        /// 底图顺时针旋转角（0/90/180/270）。
        ///
        /// 旋转只作用于**底图**：网格永远是屏幕轴对齐的方形格 —— 它是桌面上的
        /// 实体战斗网格，屏幕转了它也不该跟着转，否则量出来的 2.5cm 就不是 2.5cm 了。
        ///
        /// 实现上不是"把图片拧过去硬裁"，而是先算一个**转置过的逻辑视口**：
        /// 转 90/270 时逻辑视口是 (高 × 宽)，在逻辑坐标系里用同一套拉伸/填充规则
        /// 把底图摆好，最后整体旋转贴到屏幕上。这样"旋转后的底图"仍然服从
        /// 拉伸（铺满）/ 填充（保持比例）的语义，不会出现转完露黑边或莫名被切掉一半。
        /// </summary>
        public void SetRotation(int degrees)
        {
            var normalized = MapStore.NormalizeRotation(degrees);
            if (normalized == _rotation) return;
            _rotation = normalized;
            _mapLayerDirty = true;            // 底图层是在旋转后的坐标系里重采样的
            _sceneDirty = true;
            UpdateAnchor();                   // 转 90/270 时逻辑视口宽高互换，格数跟着变
            Invalidate();
        }

        /// <summary>旋转后视口的逻辑宽度（转 90/270 时与控件宽度互换）。</summary>
        private float LogicalViewWidth
        {
            get { return IsQuarterTurn ? Height : Width; }
        }

        /// <summary>旋转后视口的逻辑高度。</summary>
        private float LogicalViewHeight
        {
            get { return IsQuarterTurn ? Width : Height; }
        }

        private bool IsQuarterTurn
        {
            get { return _rotation == 90 || _rotation == 270; }
        }

        /// <summary>网格尺度（cm/格）。只影响网格，底图层不动。</summary>
        public void SetGridSize(double cmPerCell)
        {
            var clamped = Math.Max(0.1, cmPerCell);
            if (Math.Abs(clamped - _cmPerCell) < 0.0001) return;
            _cmPerCell = clamped;
            _sceneDirty = true;
            UpdateAnchor();
            Invalidate();
        }

        /// <summary>
        /// 网格线颜色：<c>white</c>（纯白 #FFFFFF）或 <c>black</c>（纯黑 #000000）。
        ///
        /// 只认这两个值 —— 网格线要么纯白要么纯黑，中间灰在亮底图上不够亮、
        /// 在暗底图上又不够暗，两头都不讨好。到底该用哪一个由 MapStore 决定
        /// （auto 时按底图平均亮度判定），这里只负责画。
        /// </summary>
        public void SetGridColor(string color)
        {
            var normalized = string.Equals(color, "black", StringComparison.OrdinalIgnoreCase) ? "black" : "white";
            if (normalized == _gridColor) return;
            _gridColor = normalized;
            _sceneDirty = true;
            Invalidate();
        }

        /// <summary>当前网格线颜色（white / black）。</summary>
        public string GridColor
        {
            get { return _gridColor; }
        }

        /// <summary>接上绘图数据与素材缓存（绘图面板打开时才真的有内容）。</summary>
        public void SetDrawings(DrawingStore drawings, TerrainImageCache cache, TerrainCatalog catalog)
        {
            _drawings = drawings;
            _drawCache = cache;
            _catalog = catalog;
            _drawnVersion = -1;
            _sceneDirty = true;
            Invalidate();
        }

        /// <summary>当前选区预览（画在场景位图之上，不烘进缓存）。</summary>
        public DrawPreview Preview
        {
            get { return _preview; }
            set
            {
                _preview = value;
                Invalidate();
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            _mapLayerDirty = true;
            _sceneDirty = true;
            UpdateAnchor();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;

            // 绘图内容变了（可能来自 HTTP 线程的按钮操作）也要让场景重做一次
            if (_drawings != null && _drawings.Version != _drawnVersion) _sceneDirty = true;

            if (_sceneDirty) RebuildScene();

            if (_scene != null)
            {
                g.DrawImageUnscaled(_scene, 0, 0);
            }
            else
            {
                using (var brush = new SolidBrush(Theme.Background))
                {
                    g.FillRectangle(brush, ClientRectangle);
                }
            }

            // 当前选区提示画在**所有已落笔内容之上**（需求："当前选区提示应该在图层最上层"），
            // 但仍在浮空面板和遮罩之下 —— 那两者是兄弟控件，天然在这个控件之上。
            if (_preview != null)
            {
                DrawOverlayRenderer.DrawPreview(g, _preview, CellPixels);
            }

            // 面板阴影（画在地图之上、面板之下，因为面板是本控件的兄弟控件）
            if (!_panelRect.IsEmpty && _panelRect.Width > 0)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                for (var i = 6; i >= 1; i--)
                {
                    var alpha = 10 + (7 - i) * 6;
                    var rect = new Rectangle(
                        _panelRect.X + i, _panelRect.Y + i / 2,
                        _panelRect.Width + i, _panelRect.Height - i);
                    using (var path = Theme.RightRoundedRect(rect, 16f))
                    using (var pen = new Pen(Color.FromArgb(alpha, 0, 0, 0), 2f))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }

            // 灰色半透明遮罩
            if (_maskOpacity > 0.001)
            {
                var alpha = (int)(153 * _maskOpacity); // 0.6 * 255
                if (alpha > 0)
                {
                    using (var brush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0)))
                    {
                        g.FillRectangle(brush, ClientRectangle);
                    }
                }
            }
        }

        /// <summary>把原始底图重采样成和控件等大的一张位图（贵，只在必要时做）。</summary>
        private void RebuildMapLayer()
        {
            _mapLayerDirty = false;

            if (_mapLayer != null)
            {
                _mapLayer.Dispose();
                _mapLayer = null;
            }

            if (_mapImage == null || _mapPixelWidth <= 0 || _mapPixelHeight <= 0) return;
            if (Width <= 0 || Height <= 0) return;

            try
            {
                _mapLayer = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            }
            catch (Exception ex)
            {
                AppLog.Write("创建底图层失败", ex);
                return;
            }

            using (var g = Graphics.FromImage(_mapLayer))
            {
                g.Clear(Theme.Background);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // 把绘图坐标系先摆成"逻辑视口"（旋转 90/270 时是转置过的），
                // 底图按逻辑坐标摆好之后整体转到屏幕上。
                ApplyRotationTransform(g);

                // GDI+ 的双三次滤波采样越过源图边界时按"透明黑"处理，底图那一条边
                // 就会出现 1～3 像素的暗边。fill（铺满）模式下必然有一条边正好压在
                // 画布边缘上，于是暗边会露在屏幕最外侧；contain 模式下底图不贴边，
                // 看不出来。TileFlipXY = 越界采样时镜像边缘像素，暗边就没了。
                using (var attrs = new ImageAttributes())
                {
                    attrs.SetWrapMode(WrapMode.TileFlipXY);
                    // 带 ImageAttributes 的重载只有「Rectangle 目标 + int 源矩形」这一种，
                    // 目标矩形四舍五入到整像素（对一张缩放后的照片来说，半个像素无所谓）。
                    g.DrawImage(_mapImage, Rectangle.Round(ComputeImageRect()),
                        0, 0, _mapImage.Width, _mapImage.Height,
                        GraphicsUnit.Pixel, attrs);
                }
            }
        }

        /// <summary>
        /// 让 <paramref name="g"/> 的坐标系变成"逻辑视口"：
        /// 逻辑原点 (0,0) 对应屏幕上旋转之后该在的那个角。
        /// 不旋转时不动坐标系（省掉一次矩阵运算）。
        /// </summary>
        private void ApplyRotationTransform(Graphics g)
        {
            if (_rotation == 0 || Width <= 0 || Height <= 0) return;

            g.TranslateTransform(Width / 2f, Height / 2f);
            g.RotateTransform(_rotation);          // GDI+ 正角 = 屏幕上的顺时针
            g.TranslateTransform(-LogicalViewWidth / 2f, -LogicalViewHeight / 2f);
        }

        /// <summary>
        /// 把"底图 + 网格"合成为一张位图。
        ///
        /// 不依赖窗口句柄、不需要消息循环，所以可以在完全无人值守的情况下跑
        /// （见 SelfCheck）：这正好用来验证"一格到底多少像素"，
        /// 而且比抓屏幕截图靠谱 —— 不用起窗口，也就不会触发任何弹窗。
        /// </summary>
        public Bitmap RenderScene()
        {
            _sceneDirty = true;
            _mapLayerDirty = true;
            RebuildScene();
            if (_scene == null) return null;
            if (_preview == null) return new Bitmap(_scene);

            // 预览不在 _scene 里，离屏自检要一起看到它，所以这里再合成一次
            var result = new Bitmap(_scene);
            using (var g = Graphics.FromImage(result))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                DrawOverlayRenderer.DrawPreview(g, _preview, CellPixels);
            }
            return result;
        }

        private void RebuildScene()
        {
            _sceneDirty = false;

            var width = Width;
            var height = Height;
            if (width <= 0 || height <= 0) return;

            if (_mapLayerDirty) RebuildMapLayer();

            if (_scene != null)
            {
                _scene.Dispose();
                _scene = null;
            }

            Bitmap scene;
            try
            {
                scene = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            }
            catch (Exception ex)
            {
                AppLog.Write("创建离屏位图失败", ex);
                return;
            }

            using (var g = Graphics.FromImage(scene))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Theme.Background);

                // 底图：已经预先缩放好，这里只是一次位块传送
                if (_mapLayer != null) g.DrawImageUnscaled(_mapLayer, 0, 0);

                DrawGrid(g, width, height);

                // 绘图内容：地形在下、实体/物品在上（顺序由 DrawingStore 保证）
                if (_drawings != null)
                {
                    DrawOverlayRenderer.DrawSprites(g, _drawings.Snapshot(), _drawCache, CellPixels, _catalog);
                    _drawnVersion = _drawings.Version;
                }
            }

            _scene = scene;
        }

        /// <summary>
        /// 画网格。格子边长 = cm/格 × 37.8 像素/厘米，**不再乘底图的缩放比例**。
        /// 原点固定在视口左上角，这样加载 / 切换底图时网格不会整体漂移。
        ///
        /// 颜色只有纯白 / 纯黑两种（<see cref="SetGridColor"/>）。不透明度取 0.78：
        /// 再高就把底图盖住了，再低在投影仪上就发灰、远处的玩家看不清。
        /// </summary>
        private void DrawGrid(Graphics g, int width, int height)
        {
            var cell = (float)CellPixels;
            if (cell < 4f) cell = 4f;

            using (var path = new GraphicsPath())
            {
                for (var x = 0f; x <= width; x += cell)
                {
                    path.StartFigure();
                    path.AddLine(x, 0f, x, height);
                }

                for (var y = 0f; y <= height; y += cell)
                {
                    path.StartFigure();
                    path.AddLine(0f, y, width, y);
                }

                var line = _gridColor == "black"
                    ? Color.FromArgb(200, 0, 0, 0)
                    : Color.FromArgb(200, 255, 255, 255);

                using (var pen = new Pen(line, 1f))
                {
                    g.DrawPath(pen, path);
                }
            }
        }

        /// <summary>
        /// 底图在**逻辑坐标系**里该占的矩形。拉伸/填充的语义和以前一样，
        /// 只是把参照的视口换成了逻辑视口（旋转 90/270 时宽高互换）。
        /// </summary>
        private RectangleF ComputeImageRect()
        {
            var viewW = Math.Max(1f, LogicalViewWidth);
            var viewH = Math.Max(1f, LogicalViewHeight);

            if (_mapImage == null || _mapPixelWidth <= 0 || _mapPixelHeight <= 0)
                return new RectangleF(0, 0, viewW, viewH);

            var scaleX = viewW / _mapPixelWidth;
            var scaleY = viewH / _mapPixelHeight;
            var scale = _fill ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);

            var drawW = _mapPixelWidth * scale;
            var drawH = _mapPixelHeight * scale;
            return new RectangleF((viewW - drawW) / 2f, (viewH - drawH) / 2f, drawW, drawH);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!DrawInput) return;
            if (e.Button != MouseButtons.Left) return;

            // 抓住鼠标：拖到画布外面（甚至拖到侧栏上）也能继续画，
            // 松手之前这一笔不会断。
            Capture = true;
            if (DrawMouseDown != null) DrawMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!DrawInput) return;
            if (DrawMouseMove != null) DrawMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (DrawInput)
            {
                if (e.Button != MouseButtons.Left) return;
                Capture = false;
                if (DrawMouseUp != null) DrawMouseUp(e);
                return;
            }

            if (e.Button == MouseButtons.Left && BlankClicked != null) BlankClicked();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!DrawInput) return;
            if (DrawMouseLeave != null) DrawMouseLeave();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_scene != null) _scene.Dispose();
                if (_mapLayer != null) _mapLayer.Dispose();
                if (_mapImage != null) _mapImage.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
