using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace TrpgMaps
{
    /// <summary>
    /// 安装程序的配色与几何常量。
    ///
    /// **为什么不用主程序的 <see cref="Theme"/>**：主程序那套是"深灰工作台"
    /// （#1A1A1A 底 + #4A6A8A 灰蓝强调），是为长时间盯着看的 DM 界面调的；
    /// 安装程序只出现一次、要有"精致"的第一印象，所以这里单独一套：
    /// 更深的底色、更饱和的强调蓝、以及一个鲜艳的确认绿。
    ///
    /// 字体只认 <c>Microsoft YaHei</c>（Win7 起自带）。安装程序跑在裸机上，
    /// 绝不能依赖任何非系统字体 —— 缺字体时 GDI+ 会静默回退成宋体，字号一乱
    /// 所有手算的排版都会错位。
    /// </summary>
    internal static class SetupTheme
    {
        public const string FontFamily = "Microsoft YaHei";

        /// <summary>窗口底色。刻意比主程序更深：让白色文字和蓝色按钮跳出来。</summary>
        public static readonly Color Background = FromHex("#141416");

        /// <summary>内容卡片 / 输入框底。</summary>
        public static readonly Color Surface = FromHex("#1E1E21");
        public static readonly Color SurfaceHover = FromHex("#26262A");

        /// <summary>
        /// 未选中选项的"很窄的灰胶囊边框"。
        /// 亮到刚好在 #141416 的底上看得见轮廓，又不至于抢选中的风头。
        /// </summary>
        public static readonly Color CapsuleBorder = FromHex("#4A4A52");
        public static readonly Color CapsuleBorderHover = FromHex("#6E6E78");

        /// <summary>强调蓝（主按钮 / 选中胶囊）。</summary>
        public static readonly Color Accent = FromHex("#0A84FF");
        public static readonly Color AccentHover = FromHex("#3D9DFF");
        public static readonly Color AccentDown = FromHex("#0A6FD8");

        /// <summary>确认绿（选中的圆环 + 对号）。</summary>
        public static readonly Color Confirm = FromHex("#2BD46A");

        public static readonly Color Text = FromHex("#F2F2F5");
        public static readonly Color TextDim = FromHex("#8E8E96");
        public static readonly Color TextFaint = FromHex("#5C5C64");

        /// <summary>日志区。</summary>
        public static readonly Color LogBack = FromHex("#101012");
        public static readonly Color LogText = FromHex("#B8D4BC");

        /// <summary>进度条：底轨 + 已经过的那一段。</summary>
        public static readonly Color Track = FromHex("#2A2A2E");

        public static readonly Color Separator = FromHex("#2A2A2E");

        /// <summary>危险 / 卸载用的暖色。</summary>
        public static readonly Color Warn = FromHex("#E0A0A0");

        /// <summary>胶囊行的高（含内边距），也是所有圆角统一的曲率来源。</summary>
        public const int RowHeight = 46;
        public const float RowRadius = 23f;

        public static Color FromHex(string hex)
        {
            var s = hex.TrimStart('#');
            return Color.FromArgb(
                int.Parse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
                int.Parse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
                int.Parse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber));
        }
    }

    /// <summary>安装程序专用的绘制小工具（字体缓存 / 圆角路径 / 文字）。</summary>
    internal static class SetupDraw
    {
        private static readonly Dictionary<string, Font> Cache = new Dictionary<string, Font>();

        public static Font Font(float size)
        {
            return Font(size, FontStyle.Regular);
        }

        public static Font Font(float size, FontStyle style)
        {
            var key = size.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                      "|" + (int)style;

            Font font;
            if (!Cache.TryGetValue(key, out font))
            {
                font = new Font(SetupTheme.FontFamily, size, style);
                Cache[key] = font;
            }
            return font;
        }

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        /// <summary>圆角矩形路径（结果由调用方 Dispose）。</summary>
        public static GraphicsPath Capsule(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2f;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;
            if (d <= 0.1f)
            {
                path.AddRectangle(rect);
                return path;
            }

            path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        public static void FillCapsule(Graphics g, RectangleF rect, float radius, Color color)
        {
            if (rect.Width <= 0f || rect.Height <= 0f) return;
            using (var path = Capsule(rect, radius))
            using (var brush = new SolidBrush(color))
            {
                g.FillPath(brush, path);
            }
        }

        /// <summary>
        /// 描一圈圆角边。矩形各边向内收半个线宽：GDI+ 的 Pen 是**以路径为中心**
        /// 向两侧扩展的，不内缩的话边框会被控件边缘裁掉一半，看起来比实际细。
        /// </summary>
        public static void StrokeCapsule(Graphics g, RectangleF rect, float radius, Color color, float width)
        {
            if (rect.Width <= width || rect.Height <= width) return;
            var inset = width / 2f;
            var r = new RectangleF(rect.X + inset, rect.Y + inset,
                                   rect.Width - width, rect.Height - width);
            using (var path = Capsule(r, radius))
            using (var pen = new Pen(color, width))
            {
                pen.LineJoin = LineJoin.Round;
                g.DrawPath(pen, path);
            }
        }

        public static void Text(Graphics g, string text, Font font, Color color,
                                RectangleF bounds, ContentAlignment align)
        {
            if (string.IsNullOrEmpty(text)) return;

            using (var format = new StringFormat())
            {
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;

                if (align == ContentAlignment.MiddleCenter) format.Alignment = StringAlignment.Center;
                else if (align == ContentAlignment.MiddleRight) format.Alignment = StringAlignment.Far;
                else if (align == ContentAlignment.MiddleLeft) format.Alignment = StringAlignment.Near;

                format.LineAlignment = StringAlignment.Center;

                using (var brush = new SolidBrush(color))
                {
                    g.DrawString(text, font, brush, bounds, format);
                }
            }
        }

        /// <summary>
        /// 两个胶囊拼成的对号（勾）。
        ///
        /// 用户的明确要求：不要 Photoshop 里那个现成的勾形，要"两段胶囊"接起来的。
        /// 做法就是两条**圆头粗线**（<see cref="LineCap.Round"/>），
        /// 短的一撇（右下斜）+ 长的一捺（右上斜），交汇点共用同一个圆头，
        /// 于是接缝处天然是个圆角，不会有尖角。
        ///
        /// 几何按包围盒比例算，所以同一份代码画 12px 的小勾和 40px 的大勾都成比例。
        /// </summary>
        public static void DrawCheck(Graphics g, RectangleF box, Color color, float thickness)
        {
            if (box.Width <= 0f || box.Height <= 0f) return;
            if (thickness < 1f) thickness = 1f;

            // 三个控制点（比例取自常见勾形的视觉重心，短撇约占总宽 0.36）：
            //   左端偏低 → 折点（最低）→ 右端（最高）
            var left = new PointF(box.X + box.Width * 0.06f, box.Y + box.Height * 0.52f);
            var joint = new PointF(box.X + box.Width * 0.36f, box.Y + box.Height * 0.82f);
            var right = new PointF(box.X + box.Width * 0.96f, box.Y + box.Height * 0.16f);

            using (var pen = new Pen(color, thickness))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLine(pen, left, joint);
                g.DrawLine(pen, joint, right);
            }
        }

        /// <summary>
        /// 给一个矩形外圈描一道很淡的光晕，让浮在深色底上的元素有"抬起来"的感觉。
        /// GDI+ 没有真正的模糊，用几层递减透明度的描边模拟。
        /// </summary>
        public static void Glow(Graphics g, RectangleF rect, float radius, Color color, int layers)
        {
            for (var i = layers; i >= 1; i--)
            {
                var spread = i * 1.6f;
                var alpha = (int)(color.A * (0.30f / i));
                if (alpha <= 2) continue;

                var r = RectangleF.Inflate(rect, spread, spread);
                using (var path = Capsule(r, radius + spread))
                using (var pen = new Pen(Color.FromArgb(alpha, color), 1.6f))
                {
                    g.DrawPath(pen, path);
                }
            }
        }
    }

    /// <summary>
    /// 三态选项胶囊 —— 安装程序"安装选项"页的核心控件。
    ///
    /// 外观严格按用户给的四张示意图定：
    ///   未选中：几乎看不见的**很窄的灰色胶囊边框**（只是让人意识到这里有个框）
    ///   悬浮  ：胶囊变成**半透明白底 + 黑字**
    ///   选中  ：胶囊变成**蓝底 + 白字**，右侧一个**曲率相同的绿色圆环 + 对号**
    ///
    /// 对号由两条圆头粗线拼成（见 <see cref="SetupDraw.DrawCheck"/>）。
    ///
    /// 为什么不继承 CheckBox：WinForms 的 CheckBox 即使在 FlatStyle.Flat 下也会
    /// 画系统那个小方块，而且配色跟深色底打架。这里整个自绘，包括焦点框都不画。
    /// </summary>
    internal sealed class OptionRow : Control
    {
        private bool _checked;
        private bool _hover;
        private bool _pressed;

        /// <summary>托管属性，别叫 Checked：叫 Checked 会与 Control 上已有的名字撞车。</summary>
        public event EventHandler StateChanged;

        public OptionRow()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable |
                     ControlStyles.SupportsTransparentBackColor, true);

            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (value == _checked) return;
                _checked = value;
                Invalidate();
                if (StateChanged != null) StateChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;

            // 父容器是自绘的深色面板。这里手动把父底的这一块刷成背景色，
            // 因为 BackColor=Transparent 在 WinForms 里要求父控件实现
            // PaintBackground —— 自绘父控件没实现，不刷就会留下残影。
            using (var back = new SolidBrush(Parent != null ? Parent.BackColor : SetupTheme.Background))
            {
                g.FillRectangle(back, ClientRectangle);
            }

            SetupDraw.Smooth(g);

            var box = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            var radius = Height / 2f;

            if (_checked)
            {
                // ---- 选中：蓝底白字 + 绿色圆环对号 ----
                SetupDraw.Glow(g, box, radius, SetupTheme.Accent, 4);

                var fill = _pressed ? SetupTheme.AccentDown
                         : (_hover ? SetupTheme.AccentHover : SetupTheme.Accent);
                SetupDraw.FillCapsule(g, box, radius, fill);

                // 右侧的绿环。直径取行高的 0.62，垂直居中，右边距 = 半径的一半。
                var d = Height * 0.62f;
                var ring = new RectangleF(Width - d - Height * 0.19f, (Height - d) / 2f, d, d);

                using (var pen = new Pen(SetupTheme.Confirm, Math.Max(2f, d * 0.085f)))
                {
                    g.DrawEllipse(pen, ring);
                }

                // 环内的对号：只占环内大约 46% 宽，四周留白才不显得挤
                var check = RectangleF.Inflate(ring, -d * 0.27f, -d * 0.27f);
                SetupDraw.DrawCheck(g, check, SetupTheme.Confirm, Math.Max(1.8f, d * 0.10f));

                // 文字左对齐（和图中一致，居中偏左一点点更好看）
                var textBounds = new RectangleF(Height * 0.55f, 0f, ring.Left - Height * 0.55f - 6f, Height);
                SetupDraw.Text(g, Text, SetupDraw.Font(11.5f, FontStyle.Bold),
                    Color.White, textBounds, ContentAlignment.MiddleLeft);
            }
            else if (_hover || _pressed)
            {
                // ---- 悬浮：半透明白底 + 黑字 ----
                //
                // 透明度是这个状态的关键：太实（200+）会变成一块纯白胶囊，
                // 和"未选中"完全分不清，用户就失去了"我正指着这一条"的反馈；
                // 太虚（<120）在深底上又看不出来。实测 150/165 这一档最好：
                // 白雾一样铺开、底下的深色还透得出来，黑字依然清晰。
                var alpha = _pressed ? 176 : 152;
                SetupDraw.FillCapsule(g, box, radius, Color.FromArgb(alpha, 255, 255, 255));

                // 悬浮时补一圈很淡的白边，让半透明块有个边界，
                // 否则在深底上它看起来像"曝光过度"而不是一个控件。
                SetupDraw.StrokeCapsule(g, box, radius, Color.FromArgb(90, 255, 255, 255), 1f);

                var textBounds = new RectangleF(Width * 0.5f - Width * 0.42f, 0f, Width * 0.84f, Height);
                SetupDraw.Text(g, Text, SetupDraw.Font(11.5f),
                    Color.FromArgb(16, 16, 20), textBounds, ContentAlignment.MiddleCenter);
            }
            else
            {
                // ---- 未选中：非常窄的灰胶囊边框（只是个"这里有个框"的暗示） ----
                //
                // 用户要的是"让人意识到有个边框就行"，所以刻意压得很淡：
                // 单像素、颜色只比背景亮一点点。太亮会喧宾夺主，抢掉选中态。
                SetupDraw.StrokeCapsule(g, box, radius, SetupTheme.CapsuleBorder, 1f);

                var textBounds = new RectangleF(0f, 0f, Width, Height);
                SetupDraw.Text(g, Text, SetupDraw.Font(11.5f),
                    SetupTheme.Text, textBounds, ContentAlignment.MiddleCenter);
            }

            // 键盘焦点：极细的虚线不用，改成左上角一个 3px 小圆点，最不打扰
            if (Focused && !_checked)
            {
                using (var brush = new SolidBrush(Color.FromArgb(150, SetupTheme.Accent)))
                {
                    g.FillEllipse(brush, 10f, Height / 2f - 2f, 4f, 4f);
                }
            }
        }

        /// <summary>
        /// 强制摆出"悬浮"外观，不做任何鼠标操作。给 `/uipreview` 截图用 ——
        /// 无人值守时没法真的把鼠标移上去。
        /// </summary>
        public void PreviewHover(bool on)
        {
            _hover = on;
            Invalidate();
        }

        /// <summary>
        /// 锁定外观：把 `<see cref="_hover"/>` 交给截图流程独占控制，
        /// 此后真实的鼠标进出、按下都不再改变外观。
        ///
        /// 不加这道锁的话，截图会随"鼠标碰巧停在哪个屏幕位置"而变 ——
        /// 实测抓 `02-options-none` 时鼠标正好压在第 3 行上，
        /// 那张图里第 3 行是悬浮态，和它想要表达的"未选中"完全是两回事，
        /// 于是自检结论就错了（这正是"截图骗人"的典型）。
        /// </summary>
        public bool PreviewLock { get; set; }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (PreviewLock) return;
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (PreviewLock) return;
            _hover = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || PreviewLock) return;
            Focus();
            _pressed = true;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            var wasPressed = _pressed;
            _pressed = false;
            Invalidate();

            if (wasPressed && ClientRectangle.Contains(e.Location))
            {
                Checked = !Checked;
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Space) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                Checked = !Checked;
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// 自绘胶囊按钮（主按钮 = 蓝底白字，次按钮 = 透明底 + 灰边框）。
    ///
    /// 继承 Control 而不是 Button：Button 的 OnPaint 里有一堆系统默认绘制，
    /// 就算把 FlatStyle 设成 Flat 也还是会画焦点虚线框和半透明遮罩。
    /// 自己处理 MouseDown/Up 反而更干净，行为也完全可控。
    /// </summary>
    internal sealed class CapsuleButton : Control
    {
        private bool _hover;
        private bool _pressed;
        private bool _primary = true;
        private bool _danger;

        public CapsuleButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable |
                     ControlStyles.SupportsTransparentBackColor, true);

            // 🔴 必须在**处理自己实现的 OnMouseUp 之前**关掉这两个样式。
            // ControlStyles.StandardClick 默认是**开着**的：基类 WndProc 收到
            // WM_LBUTTONUP 时自己会 OnClick 一次，而我们的 OnMouseUp 里又手动
            // OnClick 一次 —— 一次物理点击就变成两次 Click。
            //
            // 后果不是"按钮响应更灵敏"，而是**向导会跳页**：安装页的「下一步」
            // 挂了 OnPrimary()，第一次 Click 走到 SwitchPage(1)，紧接着第二次
            // Click 就落到 StartWork()，用户看到的就是"点下一步直接开始装、
            // 第二页根本没露脸"。实测一次点击 CLICK_COUNT=2 就是这个原因。
            //
            // 关掉之后 Click 完全由我们自己的 OnMouseUp 决定，一次点击一次事件。
            SetStyle(ControlStyles.StandardClick, false);
            SetStyle(ControlStyles.StandardDoubleClick, false);

            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        /// <summary>主按钮（蓝底白字）还是次按钮（描边）。</summary>
        public bool Primary
        {
            get { return _primary; }
            set { _primary = value; Invalidate(); }
        }

        /// <summary>危险动作（卸载 / 取消之类），用暖红色调。</summary>
        public bool Danger
        {
            get { return _danger; }
            set { _danger = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;

            using (var back = new SolidBrush(Parent != null ? Parent.BackColor : SetupTheme.Background))
            {
                g.FillRectangle(back, ClientRectangle);
            }

            SetupDraw.Smooth(g);

            var box = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            var radius = Height / 2f;

            if (!Enabled)
            {
                // 禁用态：灰底灰字，绝不画强调色 —— 安装进行中按钮会禁用，
                // 这时候还亮着蓝会让人以为可以再点。
                SetupDraw.FillCapsule(g, box, radius, Color.FromArgb(46, 46, 50));
                SetupDraw.Text(g, Text, SetupDraw.Font(10.5f),
                    Color.FromArgb(110, 110, 118), new RectangleF(0, 0, Width, Height),
                    ContentAlignment.MiddleCenter);
                return;
            }

            if (_primary)
            {
                SetupDraw.Glow(g, box, radius, _danger ? SetupTheme.Warn : SetupTheme.Accent, 4);

                var fill = _danger
                    ? (_pressed ? Color.FromArgb(255, 120, 120) : (_hover ? Color.FromArgb(255, 150, 150) : SetupTheme.Warn))
                    : (_pressed ? SetupTheme.AccentDown : (_hover ? SetupTheme.AccentHover : SetupTheme.Accent));

                SetupDraw.FillCapsule(g, box, radius, fill);
                SetupDraw.Text(g, Text, SetupDraw.Font(10.5f, FontStyle.Bold),
                    Color.White, new RectangleF(0, 0, Width, Height), ContentAlignment.MiddleCenter);
            }
            else
            {
                var border = _hover ? Color.FromArgb(200, 200, 208) : SetupTheme.Separator;
                var textColor = _hover ? Color.White : SetupTheme.TextDim;

                if (_pressed)
                {
                    SetupDraw.FillCapsule(g, box, radius, Color.FromArgb(40, 255, 255, 255));
                }

                SetupDraw.StrokeCapsule(g, box, radius, border, 1f);
                SetupDraw.Text(g, Text, SetupDraw.Font(10.5f),
                    textColor, new RectangleF(0, 0, Width, Height), ContentAlignment.MiddleCenter);
            }

            if (Focused)
            {
                using (var pen = new Pen(Color.FromArgb(90, SetupTheme.Accent), 1f))
                {
                    pen.DashStyle = DashStyle.Dot;
                    using (var path = SetupDraw.Capsule(
                        new RectangleF(3.5f, 3.5f, Width - 7f, Height - 7f), (Height - 7f) / 2f))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            Focus();
            _pressed = true;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            var wasPressed = _pressed;
            _pressed = false;
            Invalidate();

            if (wasPressed && Enabled && ClientRectangle.Contains(e.Location))
            {
                OnClick(EventArgs.Empty);
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Space) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;

            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// 自绘圆角输入框（安装路径那一栏）。
    ///
    /// 用**内嵌一个 Borderless TextBox** 的老办法：真正的文字编辑、选区、剪贴板、
    /// IME 输入全部交给系统 TextBox（自绘一个输入框要处理这些至少要几百行，
    /// 而且中文输入法的候选窗定位很容易做错）。外面只负责画圆角底 + 描边。
    /// </summary>
    internal sealed class CapsuleTextBox : Control
    {
        private readonly TextBox _inner;
        private bool _hover;

        public CapsuleTextBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            BackColor = SetupTheme.Background;

            _inner = new TextBox();
            _inner.BorderStyle = BorderStyle.None;
            _inner.BackColor = SetupTheme.Surface;
            _inner.ForeColor = SetupTheme.Text;
            _inner.Font = SetupDraw.Font(10.5f);
            _inner.GotFocus += delegate { Invalidate(); };
            _inner.LostFocus += delegate { Invalidate(); };
            _inner.TextChanged += delegate { OnTextChanged(EventArgs.Empty); };
            Controls.Add(_inner);
        }

        public override string Text
        {
            get { return _inner.Text; }
            set { _inner.Text = value; }
        }

        /// <summary>只读模式（进度页显示最终安装目录用）。</summary>
        public bool ReadOnlyText
        {
            get { return _inner.ReadOnly; }
            set { _inner.ReadOnly = value; }
        }

        public void SelectAllText()
        {
            _inner.SelectAll();
        }

        public void FocusInput()
        {
            _inner.Focus();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            LayoutInner();
        }

        private void LayoutInner()
        {
            // 左右各留 14px 内边距，垂直居中。高度固定成字体行高 + 一点余量，
            // 让它看起来自然居中；直接给满高会让光标顶到上边缘。
            var h = _inner.PreferredHeight;
            _inner.SetBounds(14, Math.Max(1, (Height - h) / 2), Math.Max(10, Width - 28), h);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _inner.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            SetupDraw.Smooth(g);

            var box = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            var radius = Height / 2f;

            SetupDraw.FillCapsule(g, box, radius, SetupTheme.Surface);

            var focused = _inner.Focused;
            var border = focused ? SetupTheme.Accent
                       : (_hover ? SetupTheme.CapsuleBorderHover : SetupTheme.CapsuleBorder);
            SetupDraw.StrokeCapsule(g, box, radius, border, focused ? 1.6f : 1f);
        }
    }

    /// <summary>
    /// 自绘进度条：细圆角轨 + 已经过的一段，没有条纹、没有百分比文字。
    /// 进度用 0..1 的 double 表达（安装引擎回调的是"第几步/共几步"）。
    /// </summary>
    internal sealed class ThinProgress : Control
    {
        private double _value;
        private double _target;
        private readonly Timer _anim;

        public ThinProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            BackColor = SetupTheme.Background;

            // 进度不直接跳到位，插值过去 —— 安装是同步的、一次 DoEvents 就跨了好几步，
            // 直接赋值会看到进度条"闪一下就满"，插值让它看起来是滑过去的。
            _anim = new Timer();
            _anim.Interval = 16;
            _anim.Tick += delegate
            {
                var delta = _target - _value;
                if (Math.Abs(delta) < 0.004)
                {
                    _value = _target;
                    _anim.Stop();
                }
                else
                {
                    _value += delta * 0.22;
                }
                Invalidate();
            };
        }

        /// <summary>0..1。</summary>
        public double Value
        {
            get { return _value; }
            set
            {
                if (value < 0) value = 0;
                if (value > 1) value = 1;
                _target = value;
                if (!_anim.Enabled) _anim.Start();
            }
        }

        /// <summary>不做动画，立刻到位（收尾用）。</summary>
        public void SnapTo(double value)
        {
            if (value < 0) value = 0;
            if (value > 1) value = 1;
            _target = value;
            _value = value;
            _anim.Stop();
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            SetupDraw.Smooth(g);

            var h = Math.Min(6, Height);
            var y = (Height - h) / 2f;
            var radius = h / 2f;

            SetupDraw.FillCapsule(g, new RectangleF(0, y, Width, h), radius, SetupTheme.Track);

            var filled = (float)(Width * _value);
            if (filled > 1f)
            {
                SetupDraw.FillCapsule(g, new RectangleF(0, y, filled, h), radius, SetupTheme.Accent);
            }

            // 顶端一个亮点，强调"正在推进"
            if (_value > 0.005 && _value < 0.999)
            {
                var x = filled - radius;
                using (var brush = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                {
                    g.FillEllipse(brush, x - radius * 0.5f, y + h * 0.15f, h * 0.7f, h * 0.7f);
                }
            }
        }
    }

    /// <summary>
    /// 卸载页那个"要不要保留用户数据"的选项条：左侧一个小方框 + 勾，右侧文字。
    /// 与 <see cref="OptionRow"/> 分开，因为它长得不一样（不是胶囊行，
    /// 而是一条带说明的窄条），共用同一套对号绘制。
    /// </summary>
    internal sealed class CheckStrip : Control
    {
        private bool _checked;
        private bool _hover;

        public CheckStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable |
                     ControlStyles.SupportsTransparentBackColor, true);

            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (value == _checked) return;
                _checked = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;

            using (var back = new SolidBrush(Parent != null ? Parent.BackColor : SetupTheme.Background))
            {
                g.FillRectangle(back, ClientRectangle);
            }

            SetupDraw.Smooth(g);

            var box = new RectangleF(0f, 0f, 18f, 18f);
            box.Y = (Height - box.Height) / 2f;

            if (_checked)
            {
                SetupDraw.FillCapsule(g, box, 5f, SetupTheme.Accent);
                var check = new RectangleF(box.X + 3.6f, box.Y + 3.4f, box.Width - 7.2f, box.Height - 6.8f);
                SetupDraw.DrawCheck(g, check, Color.White, 2.2f);
            }
            else
            {
                var border = _hover ? SetupTheme.CapsuleBorderHover : SetupTheme.CapsuleBorder;
                SetupDraw.StrokeCapsule(g, box, 5f, border, 1.4f);
            }

            var textColor = _hover ? Color.White : SetupTheme.Text;
            SetupDraw.Text(g, Text, SetupDraw.Font(10f), textColor,
                new RectangleF(28f, 0f, Math.Max(10f, Width - 28f), Height),
                ContentAlignment.MiddleLeft);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            Checked = !Checked;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Space) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                Checked = !Checked;
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// 自绘的多行日志面板。只读、自动滚到底、带一条 4px 细滑块。
    ///
    /// 不用 TextBox.Multiline：那个在深色底上要额外应付系统绘制、而且它的
    /// 滚动条没法改成细的。这里的日志只追加、不编辑，用 List&lt;string&gt; 最省事。
    /// </summary>
    internal sealed class LogView : Control
    {
        private readonly List<string> _lines = new List<string>();
        private int _offset;
        private int _maxOffset;

        private const int Pad = 12;
        private const int LineHeight = 17;

        public LogView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            BackColor = SetupTheme.Background;
        }

        public void Append(string line)
        {
            if (line == null) line = string.Empty;
            _lines.Add(line);
            Recalc();
            Update();       // 同步重绘：安装过程里要让日志实时可见
        }

        public void ClearAll()
        {
            _lines.Clear();
            _offset = 0;
            _maxOffset = 0;
            Invalidate();
        }

        public string Dump()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var line in _lines) sb.AppendLine(line);
            return sb.ToString();
        }

        private int ContentHeight
        {
            get { return _lines.Count * LineHeight + Pad * 2; }
        }

        private void Recalc()
        {
            var view = Math.Max(1, Height - Pad * 2);
            _maxOffset = Math.Max(0, ContentHeight - Pad * 2 - view);
            _offset = _maxOffset;       // 日志永远追着最后一行
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Recalc();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            // 日志区不该抢滚轮：真正的滚动交给下面这个开关，只有鼠标在上面时才滚它
            MouseWheel += LogView_MouseWheel;
        }

        private void LogView_MouseWheel(object sender, MouseEventArgs e)
        {
            var next = _offset - e.Delta / 120 * LineHeight * 3;
            if (next < 0) next = 0;
            if (next > _maxOffset) next = _maxOffset;
            if (next == _offset) return;
            _offset = next;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;

            g.Clear(BackColor);
            SetupDraw.Smooth(g);

            using (var path = SetupDraw.Capsule(
                new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 10f))
            using (var brush = new SolidBrush(SetupTheme.LogBack))
            {
                g.FillPath(brush, path);
            }

            // 裁剪出内容区，避免文字溢出圆角
            var clip = g.Clip;
            using (var path = SetupDraw.Capsule(
                new RectangleF(1f, 1f, Width - 2f, Height - 2f), 9f))
            {
                g.SetClip(path, CombineMode.Intersect);

                var font = SetupDraw.Font(9f);
                var y = Pad - _offset;
                foreach (var line in _lines)
                {
                    if (y > -LineHeight && y < Height)
                    {
                        var color = line.StartsWith("出错了") || line.StartsWith("错误")
                            ? SetupTheme.Warn
                            : SetupTheme.LogText;
                        SetupDraw.Text(g, line, font, color,
                            new RectangleF(Pad, y, Width - Pad * 2 - 4f, LineHeight),
                            ContentAlignment.MiddleLeft);
                    }
                    y += LineHeight;
                }

                g.Clip = clip;
            }

            // 细滑块
            if (_maxOffset > 0)
            {
                var track = Height - 16f;
                var content = ContentHeight - Pad * 2;
                var thumb = Math.Max(24f, track * ((Height - Pad * 2) / (float)content));
                var progress = _maxOffset <= 0 ? 0f : _offset / (float)_maxOffset;
                var top = 8f + (track - thumb) * progress;

                using (var brush = new SolidBrush(Color.FromArgb(120, 140, 140, 150)))
                {
                    SetupDraw.FillCapsule(g,
                        new RectangleF(Width - 9f, top, 3.5f, thumb), 1.75f,
                        Color.FromArgb(120, 140, 140, 150));
                }
            }
        }
    }

    // ================================================================
    //  自绘小部件
    // ================================================================

    /// <summary>透明背景 + 自定义字号的一段文字。替代 Label（Label 在深色底上会闪白）。</summary>
    internal sealed class Caption : Control
    {
        private readonly float _size;
        private readonly FontStyle _style;

        public Caption(string text, float size, FontStyle style, Color color)
        {
            _size = size;
            _style = style;

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.SupportsTransparentBackColor, true);

            BackColor = Color.Transparent;
            ForeColor = color;
            Text = text;
            Enabled = false;        // 纯展示：不吃鼠标事件，免得挡住底下的拖动
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;

            using (var back = new SolidBrush(Parent != null ? Parent.BackColor : SetupTheme.Background))
            {
                g.FillRectangle(back, ClientRectangle);
            }

            SetupDraw.Smooth(g);
            SetupDraw.Text(g, Text, SetupDraw.Font(_size, _style), ForeColor,
                new RectangleF(0f, 0f, Width, Height), ContentAlignment.MiddleLeft);
        }
    }

    /// <summary>分隔细线。</summary>
    internal sealed class Divider : Control
    {
        public Divider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var brush = new SolidBrush(SetupTheme.Separator))
            {
                e.Graphics.FillRectangle(brush, 0, 0, Width, Math.Max(1, Height));
            }
        }
    }

    /// <summary>右上角的关闭按钮（一个细 ×，悬浮时变白）。</summary>
    internal sealed class CloseButton : Control
    {
        private bool _hover;

        public CloseButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);

            // 同上：这个控件也在 OnMouseUp 里手动 OnClick，
            // 不关掉标准点击样式就会一次点击触发两次（这里两次 Close 不致命，
            // 但行为不一致早晚会咬人）。
            SetStyle(ControlStyles.StandardClick, false);
            SetStyle(ControlStyles.StandardDoubleClick, false);

            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;

            using (var back = new SolidBrush(Parent != null ? Parent.BackColor : SetupTheme.Background))
            {
                g.FillRectangle(back, ClientRectangle);
            }

            SetupDraw.Smooth(g);

            if (_hover)
            {
                SetupDraw.FillCapsule(g, new RectangleF(0, 0, Width, Height), 6f,
                    Color.FromArgb(48, 255, 255, 255));
            }

            var color = _hover ? Color.White : SetupTheme.TextDim;
            using (var pen = new Pen(color, 1.5f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                var m = 8f;
                g.DrawLine(pen, m, m, Width - m, Height - m);
                g.DrawLine(pen, Width - m, m, m, Height - m);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
            {
                OnClick(EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// 左上角那块小图标：圆角方块底 + 白色圆头对号（复用同一套对号绘制，
    /// 所以它和选项行里的勾是同一个形状语言）。
    /// </summary>
    internal sealed class BrandMark : Control
    {
        public BrandMark()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);

            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;

            using (var back = new SolidBrush(Parent != null ? Parent.BackColor : SetupTheme.Background))
            {
                g.FillRectangle(back, ClientRectangle);
            }

            SetupDraw.Smooth(g);

            var box = new RectangleF(0, 0, Width, Height);
            SetupDraw.Glow(g, box, 10f, SetupTheme.Accent, 4);
            SetupDraw.FillCapsule(g, box, 10f, SetupTheme.Accent);

            var check = new RectangleF(Width * 0.24f, Height * 0.28f,
                                       Width * 0.52f, Height * 0.44f);
            SetupDraw.DrawCheck(g, check, Color.White, Math.Max(2f, Width * 0.09f));
        }
    }

    /// <summary>
    /// 步骤指示：两个小圆点，当前页是高亮的蓝色并拉长成小胶囊。
    /// 用一个委托去读当前页，避免为了几个像素去维护"通知所有控件"的机制。
    /// </summary>
    internal sealed class StepDots : Control
    {
        private readonly int _count;
        private readonly Func<int> _current;

        public StepDots(int count, Func<int> current)
        {
            _count = count;
            _current = current;

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);

            BackColor = Color.Transparent;
            Enabled = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;

            using (var back = new SolidBrush(Parent != null ? Parent.BackColor : SetupTheme.Background))
            {
                g.FillRectangle(back, ClientRectangle);
            }

            SetupDraw.Smooth(g);

            var active = _current();
            var x = 0f;

            for (var i = 0; i < _count; i++)
            {
                var isActive = (i == active);
                var w = isActive ? 18f : 7f;
                var color = isActive ? SetupTheme.Accent : SetupTheme.TextFaint;

                SetupDraw.FillCapsule(g, new RectangleF(x, 1f, w, 7f), 3.5f, color);
                x += w + 6f;
            }
        }
    }
}
