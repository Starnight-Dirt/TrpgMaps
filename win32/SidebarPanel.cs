using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace TrpgMaps
{
    /// <summary>
    /// 左侧工具栏：默认只有图标，展开后显示文字。
    /// 自己绘制而不是用 4 个 Button，这样宽度动画时不会闪烁，图标与文字位置也完全可控。
    /// </summary>
    internal sealed class SidebarPanel : Control
    {
        public const int CollapsedWidth = 60;
        public const int ExpandedWidth = 180;

        // 按钮左右留白：收起态几乎贴边（60px 本来就窄），展开态留出呼吸感。
        // 展开态原来是 3px，按钮几乎顶满侧栏两侧，看着很挤，这里放宽到 14px。
        private const int PadXCollapsed = 3;
        private const int PadXExpanded = 14;

        // 图标左边缘的位置（相对侧栏左边）：
        //   收起态 = 在 60px 里居中；展开态 = 距按钮左边缘 IconGap。
        // 两态之间按宽度插值，所以展开动画期间图标是平滑右移的，不会突然跳。
        private const int IconGap = 14;
        private const int ButtonHeight = 48;
        private const int ButtonGap = 12;
        private const int TopPadding = 12;
        private const int IconSize = 28;
        private const int TextGap = 10;
        private const int TextRightPadding = 10;

        /// <summary>
        /// 贴在侧栏**底部**的按钮个数（当前只有"关于"那一颗）。
        /// 底部的按钮不参与从顶往下的流水排布，而是从底边往上数 ——
        /// 这样无论上面有几个按钮、窗口多高，它都稳稳待在左下角，
        /// 不会因为以后再加功能按钮而被推走。
        /// </summary>
        private const int BottomSlots = 1;

        private readonly string[] _labels;
        private readonly Image[] _icons;

        private int _hoverIndex = -1;
        private int _pressIndex = -1;

        /// <summary>文字不透明度 0-1，动画期间由外部逐帧设置。</summary>
        public double TextOpacity;

        /// <summary>点中了第几个按钮（0=展开 1=地图设置 2=网络 3=全屏）。</summary>
        public event Action<int> ItemClicked;

        public SidebarPanel(string[] labels, string[] iconPaths)
        {
            _labels = labels;
            _icons = new Image[labels.Length];

            for (var i = 0; i < iconPaths.Length && i < _icons.Length; i++)
            {
                try
                {
                    if (System.IO.File.Exists(iconPaths[i]))
                    {
                        // 走统一入口（ImageLoader）：万一哪天侧栏图标也换成 webp，
                        // 这里不会再单独漏一处 —— 它返回的位图自成一体、不锁文件。
                        _icons[i] = ImageLoader.Load(iconPaths[i]);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("侧栏图标加载失败：" + iconPaths[i], ex);
                    _icons[i] = null;
                }
            }

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);

            BackColor = Theme.Sidebar;
            Width = CollapsedWidth;
        }

        /// <summary>当前宽度在"收起 ↔ 展开"之间的插值系数 0-1。</summary>
        private double ExpandRatio
        {
            get
            {
                var span = ExpandedWidth - CollapsedWidth;
                if (span <= 0) return 0;
                var t = (Width - CollapsedWidth) / (double)span;
                if (t < 0) return 0;
                if (t > 1) return 1;
                return t;
            }
        }

        private static int Lerp(int from, int to, double t)
        {
            return (int)Math.Round(from + (to - from) * t);
        }

        /// <summary>按钮左右留白。</summary>
        private int CurrentPadX
        {
            get { return Lerp(PadXCollapsed, PadXExpanded, ExpandRatio); }
        }

        /// <summary>图标左边缘相对侧栏左边的偏移。</summary>
        private int CurrentIconLeft
        {
            get
            {
                var centered = PadXCollapsed + (CollapsedWidth - PadXCollapsed * 2 - IconSize) / 2;
                var aligned = PadXExpanded + IconGap;
                return Lerp(centered, aligned, ExpandRatio);
            }
        }

        private Rectangle ButtonRect(int index)
        {
            var padX = CurrentPadX;

            int y;
            if (index >= _labels.Length - BottomSlots)
            {
                // 底部那几颗：从底边往上数。`fromBottom` = 0 表示最底下那颗。
                var fromBottom = _labels.Length - 1 - index;
                y = Height - TopPadding - ButtonHeight - fromBottom * (ButtonHeight + ButtonGap);
            }
            else
            {
                y = TopPadding + index * (ButtonHeight + ButtonGap);
            }

            var width = Width - padX * 2;
            if (width < 10) width = 10;
            return new Rectangle(padX, y, width, ButtonHeight);
        }

        /// <summary>
        /// 无人值守自检用：报告"侧栏某个宽度下，按钮和图标落在哪里"。
        /// 按钮左右留白够不够、图标是不是被压在按钮边缘上，这些直接看数字就行，
        /// 不必靠肉眼看截图。只改 Width 这个纯属性，不创建窗口句柄。
        /// </summary>
        public string DescribeGeometry(int width)
        {
            var saved = Width;
            Width = width;
            try
            {
                var rect = ButtonRect(0);
                var iconLeft = CurrentIconLeft;
                var leftGap = iconLeft - rect.X;
                var buttonRightGap = width - rect.Right;

                return "宽度=" + width +
                       "  按钮左边距=" + rect.X +
                       "  按钮右边距=" + buttonRightGap +
                       "  按钮宽=" + rect.Width +
                       "  图标左边距(相对按钮)=" + leftGap +
                       "  图标尺寸=" + IconSize + "x" + IconSize;
            }
            finally
            {
                Width = saved;
            }
        }

        /// <summary>
        /// 无人值守自检用：报告**最底部那颗按钮**（"关于"）落在哪。
        ///
        /// 它不参与从顶往下的排布，而是从底边往上数的，所以"到底贴没贴底"
        /// 只能拿一个确定的侧栏高度去算。屏幕截图在非 100% DPI 下会被裁掉
        /// 右下角（见 README 第十轮），这类"在最边上"的东西恰恰是截图最容易骗人的地方，
        /// 所以这里给出可断言的数字。
        /// </summary>
        public string DescribeBottomGeometry(int width, int height)
        {
            var savedWidth = Width;
            var savedHeight = Height;
            Width = width;
            Height = height;
            try
            {
                if (_labels.Length <= BottomSlots) return "（没有底部按钮）";

                var top = ButtonRect(_labels.Length - 1);
                var above = _labels.Length - BottomSlots - 1;
                var aboveBottom = above >= 0 ? ButtonRect(above).Bottom : -1;

                return "侧栏高=" + height +
                       "  底部按钮=\"" + _labels[_labels.Length - 1] + "\"" +
                       "  顶部y=" + top.Y +
                       "  底边距=" + (height - top.Bottom) +
                       "  与上方按钮间隙=" + (aboveBottom < 0 ? -1 : top.Y - aboveBottom);
            }
            finally
            {
                Width = savedWidth;
                Height = savedHeight;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var background = new SolidBrush(Theme.Sidebar))
            {
                g.FillRectangle(background, ClientRectangle);
            }

            for (var i = 0; i < _labels.Length; i++)
            {
                var rect = ButtonRect(i);
                if (rect.Bottom < 0 || rect.Top > Height) continue;

                var color = i == _pressIndex ? Theme.ButtonHover
                          : i == _hoverIndex ? Theme.ButtonHover
                          : Theme.Button;

                using (var path = Theme.RoundedRect(rect, 12f))
                using (var brush = new SolidBrush(color))
                {
                    g.FillPath(brush, path);
                }

                var iconLeft = CurrentIconLeft;

                if (_icons[i] != null)
                {
                    g.DrawImage(_icons[i], new Rectangle(
                        iconLeft, rect.Y + (rect.Height - IconSize) / 2, IconSize, IconSize));
                }

                // 展开时才画文字，用一个和宽度同步的透明度做淡入淡出
                if (TextOpacity > 0.01 && _labels[i].Length > 0)
                {
                    var alpha = (int)Math.Max(0, Math.Min(255, TextOpacity * 255));
                    var textLeft = iconLeft + IconSize + TextGap;
                    var textRect = new Rectangle(
                        textLeft, rect.Y,
                        Math.Max(0, rect.Right - textLeft - TextRightPadding), rect.Height);

                    if (textRect.Width > 8)
                    {
                        // 用 Ui.Font 的缓存字体：侧栏在宽度动画期间会以 ~66fps 重绘，
                        // 每帧 new 四个 Font 在老机器上是白白的开销。
                        // 缓存字体**不能**用 using 包（一 Dispose 缓存就废了）。
                        using (var brush = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
                        using (var format = new StringFormat())
                        {
                            format.LineAlignment = StringAlignment.Center;
                            format.Trimming = StringTrimming.EllipsisCharacter;
                            format.FormatFlags = StringFormatFlags.NoWrap;
                            g.DrawString(_labels[i], Ui.Font(10f), brush, textRect, format);
                        }
                    }
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var index = HitTest(e.Location);
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _pressIndex = HitTest(e.Location);
            if (_pressIndex >= 0) Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var index = HitTest(e.Location);
            var pressed = _pressIndex;
            _pressIndex = -1;
            Invalidate();

            if (index >= 0 && index == pressed && ItemClicked != null) ItemClicked(index);
        }

        private int HitTest(Point point)
        {
            for (var i = 0; i < _labels.Length; i++)
            {
                if (ButtonRect(i).Contains(point)) return i;
            }
            return -1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var icon in _icons)
                {
                    if (icon != null) icon.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
