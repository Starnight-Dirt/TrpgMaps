using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace TrpgMaps
{
    /// <summary>
    /// 滚轮"谁处理"的路由标记。
    ///
    /// 背景：WinForms 里一个控件没吃掉 WM_MOUSEWHEEL 时，这条消息会**继续往上冒泡**
    /// 交给父控件。于是"鼠标悬在素材列表上滚一下"会同时滚列表**和**面板内容 ——
    /// 用户看到的就是"滚轮把面板一起带着滚了"。
    ///
    /// 标准解法是在子控件里把 <see cref="HandledMouseEventArgs.Handled"/> 置 true，
    /// 但这条链路依赖具体的消息分发实现，光靠它不够保险。所以这里再加一道
    /// 与实现无关的护栏：谁处理了这记滚轮，就盖一个时间戳，
    /// 紧接着（同一帧内冒泡上来）的父容器看到时间戳就跳过，不再重复滚一次。
    ///
    /// 30ms 这个窗口：冒泡是同一批消息派发里完成的（远小于 1ms），
    /// 而人手滚轮最快也就每秒十几格，不会误伤下一次滚动。
    /// </summary>
    internal static class WheelRouter
    {
        private const int WindowMs = 30;
        private static int _stamp;

        /// <summary>声明"这记滚轮我已经处理了"。</summary>
        public static void Mark()
        {
            _stamp = Environment.TickCount;
        }

        /// <summary>刚刚（30ms 内）是不是已经有人处理过这记滚轮了。</summary>
        public static bool TakenRecently
        {
            get { return unchecked(Environment.TickCount - _stamp) < WindowMs; }
        }
    }

    /// <summary>
    /// 界面统一的小尺寸绘制工具 / 配色 / 字体缓存。
    ///
    /// 为什么要有这一层：直接用系统控件（ComboBox / TextBox / ListBox / TrackBar）
    /// 在深色面板上会带出系统主题的浅色直角边框、白色下拉按钮和宽滚动条，
    /// 跟整体界面割裂。这里把用到的控件全部自绘，保证圆角、配色、字号一致。
    /// </summary>
    internal static class Ui
    {
        public const string FontFamily = "Microsoft YaHei";

        /// <summary>输入类控件的底色 / 悬浮底 / 描边。</summary>
        public static readonly Color Field = Theme.FromHex("#353535");
        public static readonly Color FieldHover = Theme.FromHex("#3C3C3C");
        public static readonly Color FieldBorder = Theme.FromHex("#4A4A4A");
        public static readonly Color FieldBorderHover = Theme.FromHex("#5A5A5A");

        /// <summary>细滚动条滑块（静止 / 悬浮或拖动）。</summary>
        public static readonly Color Thumb = Theme.FromHex("#5E5E5E");
        public static readonly Color ThumbActive = Theme.FromHex("#8C8C8C");

        /// <summary>弹出层底色。</summary>
        public static readonly Color Menu = Theme.FromHex("#313131");

        /// <summary>滑块底轨。</summary>
        public static readonly Color Track = Theme.FromHex("#454545");

        public const float FieldRadius = 6f;

        private static readonly Dictionary<string, Font> FontCache = new Dictionary<string, Font>();

        public static Font Font(float size)
        {
            return Font(size, FontStyle.Regular);
        }

        /// <summary>
        /// 带缓存的字体获取。界面里到处 new Font 会导致字号/字重不统一（正是之前
        /// "输入框看着比别处硬"的原因之一），这里统一走一个入口。
        /// </summary>
        public static Font Font(float size, FontStyle style)
        {
            var key = size.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                      "|" + (int)style;

            Font font;
            if (!FontCache.TryGetValue(key, out font))
            {
                font = new Font(FontFamily, size, style);
                FontCache[key] = font;
            }
            return font;
        }

        /// <summary>文字统一用 ClearType，避免小字号发糊。</summary>
        public static void PrepareText(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        }

        public static void PrepareShape(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
        }

        public static void FillRounded(Graphics g, RectangleF rect, float radius, Color color)
        {
            if (rect.Width <= 0f || rect.Height <= 0f) return;
            using (var path = Theme.RoundedRect(rect, radius))
            using (var brush = new SolidBrush(color))
            {
                g.FillPath(brush, path);
            }
        }

        public static void StrokeRounded(Graphics g, Rectangle rect, float radius, Color color)
        {
            if (rect.Width <= 1 || rect.Height <= 1) return;
            var r = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1f, rect.Height - 1f);
            using (var path = Theme.RoundedRect(r, radius))
            using (var pen = new Pen(color))
            {
                g.DrawPath(pen, path);
            }
        }

        /// <summary>把一段文字画在指定矩形里（单行、垂直居中、超长省略号）。</summary>
        public static void DrawText(Graphics g, string text, Font font, Color color,
                                    Rectangle bounds, ContentAlignment align)
        {
            if (string.IsNullOrEmpty(text)) return;

            using (var format = new StringFormat())
            {
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;

                if (align == ContentAlignment.MiddleCenter)
                {
                    format.Alignment = StringAlignment.Center;
                }
                else if (align == ContentAlignment.MiddleRight)
                {
                    format.Alignment = StringAlignment.Far;
                }
                format.LineAlignment = StringAlignment.Center;

                using (var brush = new SolidBrush(color))
                {
                    g.DrawString(text, font, brush, bounds, format);
                }
            }
        }

        /// <summary>下拉指示的小箭头：两条圆头短线，不依赖任何字体/emoji。</summary>
        public static void DrawChevron(Graphics g, Point center, Color color, bool up)
        {
            using (var pen = new Pen(color, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                var dy = up ? -2f : 2f;
                g.DrawLine(pen, center.X - 4f, center.Y - dy, center.X, center.Y + dy);
                g.DrawLine(pen, center.X, center.Y + dy, center.X + 4f, center.Y - dy);
            }
        }
    }

    /// <summary>
    /// 面板内容的滚动容器。
    ///
    /// 两个硬性要求：
    ///  1. **永远不出现横向滚动条** —— 内容比可视区宽就直接裁掉（AutoScroll 关掉，
    ///     结构上就不可能有横条）。
    ///  2. 纵向装不下时，**不显示系统滚动条**，只在右边缘画一条 4px 的圆角小短线，
    ///     没有箭头、没有轨道底色，悬浮/拖动时才变亮。<br/>
    /// 右侧内缩（16px）本身就比滑块宽，所以滑块永远不会压到内容上。
    /// </summary>
    internal sealed class ScrollViewport : Panel
    {
        private const int BarWidth = 4;
        private const int BarInset = 4;
        private const int MinThumb = 28;

        private Control _content;
        private int _inset = 16;
        private int _offset;
        private int _maxOffset;
        private bool _thumbHover;
        private bool _thumbDrag;
        private int _dragOriginY;
        private int _dragOriginOffset;

        public ScrollViewport()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }

        public bool ScrollbarVisible { get { return _maxOffset > 0; } }
        public int ScrollOffset { get { return _offset; } }
        /// <summary>内容比可视区高出多少像素（0 = 装得下，不显示滑块）。</summary>
        public int OverflowHeight { get { return _maxOffset; } }

        public void SetContent(Control content)
        {
            if (_content != null)
            {
                Controls.Remove(_content);
                _content.Dispose();
            }

            _content = content;

            if (content != null)
            {
                Controls.Add(content);
            }

            _offset = 0;
            RefreshLayout();
        }

        /// <summary>内容变高变矮（增删行、展开收起）之后重新算一遍滚动范围。</summary>
        public void RefreshLayout()
        {
            if (_content == null)
            {
                _maxOffset = 0;
                _offset = 0;
                Invalidate();
                return;
            }

            if (_content is FlowLayoutPanel) _content.PerformLayout();

            var viewport = ClientSize.Height - _inset * 2;
            var needed = MeasureContent(viewport);

            _maxOffset = needed > viewport ? needed - viewport : 0;
            if (_offset > _maxOffset) _offset = _maxOffset;
            if (_offset < 0) _offset = 0;

            _content.Location = new Point(_inset, _inset - _offset);
            Invalidate();
        }

        /// <summary>
        /// 量内容高度。FlowLayoutPanel 的 PreferredSize 在嵌套里偶尔会晚一拍，
        /// 自上而下不换行的情况下直接累加"行高 + 行距"最可靠。
        /// </summary>
        private int MeasureContent(int viewport)
        {
            var flow = _content as FlowLayoutPanel;
            if (flow == null || flow.FlowDirection != FlowDirection.TopDown)
            {
                return _content.PreferredSize.Height;
            }

            var total = flow.Padding.Vertical;
            foreach (Control child in flow.Controls)
            {
                if (!child.Visible) continue;
                total += child.Height + child.Margin.Vertical;
            }

            // 容器本身也可能有内缩
            total += _content.Margin.Vertical;
            return Math.Max(total, viewport);
        }

        public void ScrollBy(int delta)
        {
            SetOffset(_offset + delta);
        }

        public void SetOffset(int value)
        {
            if (value < 0) value = 0;
            if (value > _maxOffset) value = _maxOffset;
            if (value == _offset) return;

            _offset = value;
            if (_content != null) _content.Top = _inset - _offset;
            Invalidate();
        }

        // ---------------------------------------------------------- 滑块几何

        private Rectangle ThumbRect()
        {
            if (_maxOffset <= 0) return Rectangle.Empty;

            var trackHeight = ClientSize.Height - BarInset * 2;
            if (trackHeight <= MinThumb) return Rectangle.Empty;

            var viewport = ClientSize.Height - _inset * 2;
            var content = viewport + _maxOffset;
            if (content <= 0) return Rectangle.Empty;

            var thumb = (int)Math.Round(trackHeight * (viewport / (double)content));
            if (thumb < MinThumb) thumb = MinThumb;
            if (thumb > trackHeight) thumb = trackHeight;

            var travel = trackHeight - thumb;
            var y = BarInset + (int)Math.Round(travel * (_offset / (double)_maxOffset));

            return new Rectangle(ClientSize.Width - BarInset - BarWidth, y, BarWidth, thumb);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Panel);

            var thumb = ThumbRect();
            if (thumb.IsEmpty) return;

            Ui.PrepareShape(e.Graphics);
            Ui.FillRounded(e.Graphics, thumb, BarWidth / 2f,
                (_thumbDrag || _thumbHover) ? Ui.ThumbActive : Ui.Thumb);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RefreshLayout();
        }

        // ---------------------------------------------------------- 鼠标交互

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_thumbDrag)
            {
                var thumb = ThumbRect();
                var travel = ClientSize.Height - BarInset * 2 - thumb.Height;
                if (travel > 0)
                {
                    var delta = e.Y - _dragOriginY;
                    SetOffset(_dragOriginOffset + (int)Math.Round(delta * (_maxOffset / (double)travel)));
                }
                return;
            }

            var hover = ThumbRect().Contains(e.Location);
            if (hover != _thumbHover)
            {
                _thumbHover = hover;
                Cursor = hover ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            if (_maxOffset <= 0) return;

            var thumb = ThumbRect();
            if (thumb.Contains(e.Location))
            {
                _thumbDrag = true;
                _dragOriginY = e.Y;
                _dragOriginOffset = _offset;
                Capture = true;
                Invalidate();
            }
            else if (e.X >= ClientSize.Width - BarInset - BarWidth - 6)
            {
                var page = ClientSize.Height - _inset * 2;
                SetOffset(e.Y < thumb.Y ? _offset - page : _offset + page);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _thumbDrag = false;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_thumbDrag && _thumbHover)
            {
                _thumbHover = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            // 别人刚刚处理过这记滚轮（比如光标其实悬在子控件上），别再滚一次
            if (WheelRouter.TakenRecently) return;
            if (_maxOffset <= 0) return;

            WheelRouter.Mark();
            ScrollBy(-(e.Delta / 120) * WheelStep);

            var handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
        }

        internal const int WheelStep = 64;
    }

    /// <summary>
    /// 现代风格输入框：圆角、无系统边框，内部是一个 BorderStyle=None 的 TextBox。
    /// 直接用 TextBox 的话 Windows 会给它一圈浅色直角边框，跟深色面板割裂。
    /// </summary>
    internal sealed class ModernTextBox : Control
    {
        private const int PadX = 10;
        private readonly TextBox _inner = new TextBox();
        private bool _hover;

        public ModernTextBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;

            _inner.BorderStyle = BorderStyle.None;
            _inner.BackColor = Ui.Field;
            _inner.ForeColor = Theme.Text;
            _inner.Font = Ui.Font(9.5f);
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

        public int MaxLength
        {
            get { return _inner.MaxLength; }
            set { _inner.MaxLength = value; }
        }

        public void SelectAllText()
        {
            _inner.SelectAll();
        }

        public void FocusInput()
        {
            _inner.Focus();
        }

        /// <summary>内部那个真正的 TextBox，用来挂 KeyPress 之类的输入过滤。</summary>
        public TextBox Input
        {
            get { return _inner; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            LayoutInner();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutInner();
        }

        private void LayoutInner()
        {
            var height = _inner.PreferredHeight;
            _inner.Location = new Point(PadX, Math.Max(1, (Height - height) / 2));
            _inner.Width = Math.Max(10, Width - PadX * 2);
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
            e.Graphics.Clear(Theme.Panel);
            Ui.PrepareShape(e.Graphics);

            var rect = new Rectangle(0, 0, Width, Height);
            Ui.FillRounded(e.Graphics, rect, Ui.FieldRadius, Ui.Field);

            Color border;
            if (_inner.Focused) border = Theme.Accent;
            else border = _hover ? Ui.FieldBorderHover : Ui.FieldBorder;
            Ui.StrokeRounded(e.Graphics, rect, Ui.FieldRadius, border);
        }
    }

    /// <summary>
    /// 现代风格下拉框：全部自绘（圆角 + 同色系底 + 细箭头），展开时弹出一个
    /// 圆角无边框的小浮层。系统 ComboBox 在深色面板上会带出白色直角边框和一个
    /// 很突兀的系统下拉按钮，正是"割裂感"的来源。
    /// </summary>
    internal sealed class ModernDropDown : Control
    {
        private readonly List<string> _items = new List<string>();
        private int _selected = -1;
        private bool _hover;
        private bool _open;
        private DropDownPopup _popup;

        public event EventHandler SelectedIndexChanged;

        public ModernDropDown()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            BackColor = Theme.Panel;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int ItemCount { get { return _items.Count; } }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                var v = value;
                if (v < -1) v = -1;
                if (v >= _items.Count) v = _items.Count - 1;
                if (v == _selected) return;

                _selected = v;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public string SelectedItem
        {
            get { return _selected >= 0 && _selected < _items.Count ? _items[_selected] : null; }
        }

        /// <summary>程序化地整体替换选项，**不触发** SelectedIndexChanged。</summary>
        public void SetItems(List<string> items, int selected)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items);

            var v = selected;
            if (v >= _items.Count) v = _items.Count - 1;
            if (v < -1) v = -1;

            _selected = v;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Panel);
            Ui.PrepareShape(e.Graphics);

            var rect = new Rectangle(0, 0, Width, Height);
            Ui.FillRounded(e.Graphics, rect, Ui.FieldRadius, (_hover || _open) ? Ui.FieldHover : Ui.Field);

            Color border;
            if (_open) border = Theme.Accent;
            else border = _hover ? Ui.FieldBorderHover : Ui.FieldBorder;
            Ui.StrokeRounded(e.Graphics, rect, Ui.FieldRadius, border);

            Ui.PrepareText(e.Graphics);
            var textRect = new Rectangle(11, 0, Math.Max(0, Width - 40), Height);
            var text = SelectedItem;
            Ui.DrawText(e.Graphics, text == null ? string.Empty : text,
                Ui.Font(9.5f), Theme.Text, textRect, ContentAlignment.MiddleLeft);

            Ui.PrepareShape(e.Graphics);
            Ui.DrawChevron(e.Graphics, new Point(Width - 17, Height / 2), Theme.TextDim, _open);
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
            Focus();

            if (_open) { ClosePopup(); return; }
            if (_items.Count == 0) return;
            OpenPopup();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (key == Keys.Up || key == Keys.Down || key == Keys.Space || key == Keys.Enter) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode == Keys.Down && _selected < _items.Count - 1)
            {
                SelectedIndex = _selected + 1;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Up && _selected > 0)
            {
                SelectedIndex = _selected - 1;
                e.Handled = true;
            }
            else if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && _items.Count > 0)
            {
                if (_open) ClosePopup(); else OpenPopup();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape && _open)
            {
                ClosePopup();
                e.Handled = true;
            }
        }

        private void OpenPopup()
        {
            ClosePopup();

            var popup = new DropDownPopup(new List<string>(_items), _selected, Width);
            popup.ItemChosen += delegate (int index)
            {
                ClosePopup();
                SelectedIndex = index;
            };
            popup.FormClosed += delegate { popup.Dispose(); };

            var screen = Screen.FromControl(this).WorkingArea;
            var topLeft = PointToScreen(Point.Empty);
            var y = topLeft.Y + Height + 2;

            if (y + popup.Height > screen.Bottom)
            {
                var above = topLeft.Y - popup.Height - 2;
                y = above >= screen.Top ? above : Math.Max(screen.Top, screen.Bottom - popup.Height);
            }

            popup.Location = new Point(topLeft.X, y);
            _popup = popup;
            _open = true;
            Invalidate();

            var owner = FindForm();
            if (owner != null) popup.Show(owner); else popup.Show();
        }

        private void ClosePopup()
        {
            if (_popup == null) return;

            var popup = _popup;
            _popup = null;
            _open = false;
            Invalidate();
            popup.CloseSafely();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) ClosePopup();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ClosePopup();
            base.Dispose(disposing);
        }
    }

    /// <summary>下拉展开后的浮层：圆角、无边框、置顶，点外面自动关闭。</summary>
    internal sealed class DropDownPopup : Form
    {
        public const int RowHeight = 28;
        private const int MaxRows = 8;

        private readonly ModernList _list;
        private bool _closed;

        public delegate void ItemChosenHandler(int index);
        public event ItemChosenHandler ItemChosen;

        public DropDownPopup(List<string> items, int selected, int width)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Ui.Menu;
            DoubleBuffered = true;

            var rows = items.Count < MaxRows ? items.Count : MaxRows;
            if (rows < 1) rows = 1;
            ClientSize = new Size(Math.Max(80, width), rows * RowHeight + 8);

            _list = new ModernList();
            _list.BackColor = Ui.Menu;
            _list.ItemHeight = RowHeight;
            _list.RoundedRows = true;
            _list.SetBounds(4, 4, ClientSize.Width - 8, ClientSize.Height - 8);
            _list.ItemDraw = DrawRow;
            foreach (var item in items) _list.Items.Add(item);
            _list.SelectSilently(selected);
            _list.SelectedIndexChanged += delegate
            {
                if (ItemChosen != null) ItemChosen(_list.SelectedIndex);
            };
            Controls.Add(_list);
        }

        private void DrawRow(ListItemDrawArgs args)
        {
            var box = args.Bounds;

            if (args.Selected)
            {
                Ui.FillRounded(args.Graphics, box, 5f, Theme.Accent);
            }
            else if (args.Hover)
            {
                Ui.FillRounded(args.Graphics, box, 5f, Theme.ButtonHover);
            }

            Ui.PrepareText(args.Graphics);
            var textRect = new Rectangle(box.X + 9, box.Y, Math.Max(0, box.Width - 18), box.Height);
            Ui.DrawText(args.Graphics, Convert.ToString(args.Item), Ui.Font(9.5f),
                Theme.Text, textRect, ContentAlignment.MiddleLeft);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (var region = Theme.RoundedRect(new RectangleF(0, 0, Width, Height), 8f))
            {
                Region = new Region(region);
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            CloseSafely();
        }

        public void CloseSafely()
        {
            if (_closed) return;
            _closed = true;
            try { Close(); }
            catch (ObjectDisposedException) { /* 已经没了 */ }
            catch (InvalidOperationException) { /* 已经没了 */ }
        }
    }

    /// <summary>列表项绘制参数（替代 ListBox 的 DrawItemEventArgs）。</summary>
    internal sealed class ListItemDrawArgs    {
        public Graphics Graphics;
        public Rectangle Bounds;
        public int Index;
        public object Item;
        public bool Selected;
        public bool Hover;
    }

    internal delegate void ListItemDrawHandler(ListItemDrawArgs args);

    /// <summary>
    /// 自绘列表（替代 ListBox）。换成自绘主要有两个原因：
    ///  * 系统 ListBox 的滚动条又宽又浅，跟界面不搭；
    ///  * 需要给每一行做圆角背景 / 缩略图 / 双色文字。
    /// 滚动条同样是右下角那条 4px 小短线，横向永不出现。
    /// </summary>
    internal sealed class ModernList : Control
    {
        private const int BarWidth = 4;
        private const int BarInset = 3;
        private const int MinThumb = 24;

        private readonly List<object> _items = new List<object>();
        private int _itemHeight = 50;
        private int _selected = -1;
        private int _hover = -1;
        private int _offset;
        private int _maxOffset;
        private bool _thumbHover;
        private bool _thumbDrag;
        private int _dragOriginY;
        private int _dragOriginOffset;

        public ListItemDrawHandler ItemDraw;

        public event EventHandler SelectedIndexChanged;
        public event EventHandler ItemActivated;

        public ModernList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            BackColor = Theme.Track;
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public List<object> Items { get { return _items; } }
        public int Count { get { return _items.Count; } }
        public bool RoundedRows;
        public bool ScrollbarVisible { get { return _maxOffset > 0; } }
        public int OverflowHeight { get { return _maxOffset; } }

        public int ItemHeight
        {
            get { return _itemHeight; }
            set
            {
                _itemHeight = Math.Max(16, value);
                UpdateScroll();
                Invalidate();
            }
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                SelectSilently(value);
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public object SelectedItem
        {
            get { return _selected >= 0 && _selected < _items.Count ? _items[_selected] : null; }
        }

        /// <summary>只改选中项，不发事件（初始化时用）。</summary>
        public void SelectSilently(int index)
        {
            var v = index;
            if (v >= _items.Count) v = _items.Count - 1;
            if (v < -1) v = -1;
            if (v == _selected) return;

            _selected = v;
            Invalidate();
        }

        public void BeginUpdate()
        {
        }

        public void EndUpdate()
        {
            RefreshItems();
        }

        /// <summary>增删条目之后调用，重算滚动范围。</summary>
        public void RefreshItems()
        {
            if (_selected >= _items.Count) _selected = -1;
            UpdateScroll();
            Invalidate();
        }

        private void UpdateScroll()
        {
            var needed = _items.Count * _itemHeight;
            var viewport = ClientSize.Height;
            _maxOffset = needed > viewport ? needed - viewport : 0;

            if (_offset > _maxOffset) _offset = _maxOffset;
            if (_offset < 0) _offset = 0;
        }

        public void SetOffset(int value)
        {
            if (value < 0) value = 0;
            if (value > _maxOffset) value = _maxOffset;
            if (value == _offset) return;

            _offset = value;
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateScroll();
        }

        private int IndexAtY(int y)
        {
            if (_items.Count == 0) return -1;
            var index = (y + _offset) / _itemHeight;
            if (index < 0 || index >= _items.Count) return -1;
            return index;
        }

        private Rectangle ThumbRect()
        {
            if (_maxOffset <= 0) return Rectangle.Empty;

            var trackHeight = ClientSize.Height - BarInset * 2;
            if (trackHeight <= MinThumb) return Rectangle.Empty;

            var content = ClientSize.Height + _maxOffset;
            var thumb = (int)Math.Round(trackHeight * (ClientSize.Height / (double)content));
            if (thumb < MinThumb) thumb = MinThumb;
            if (thumb > trackHeight) thumb = trackHeight;

            var travel = trackHeight - thumb;
            var y = BarInset + (int)Math.Round(travel * (_offset / (double)_maxOffset));

            return new Rectangle(ClientSize.Width - BarInset - BarWidth, y, BarWidth, thumb);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (_items.Count == 0) return;

            var reserve = _maxOffset > 0 ? BarWidth + BarInset : 0;
            var first = _offset / _itemHeight;
            var last = (_offset + ClientSize.Height) / _itemHeight;
            if (last >= _items.Count) last = _items.Count - 1;

            for (var i = first; i <= last; i++)
            {
                var row = new Rectangle(0, i * _itemHeight - _offset,
                                        Math.Max(0, ClientSize.Width - reserve), _itemHeight);

                if (RoundedRows)
                {
                    row = new Rectangle(row.X + 1, row.Y + 1, Math.Max(0, row.Width - 2), row.Height - 2);
                }

                if (ItemDraw == null) continue;

                var args = new ListItemDrawArgs();
                args.Graphics = e.Graphics;
                args.Bounds = row;
                args.Index = i;
                args.Item = _items[i];
                args.Selected = i == _selected;
                args.Hover = i == _hover;
                ItemDraw(args);
            }

            var thumb = ThumbRect();
            if (thumb.IsEmpty) return;

            Ui.PrepareShape(e.Graphics);
            Ui.FillRounded(e.Graphics, thumb, BarWidth / 2f,
                (_thumbDrag || _thumbHover) ? Ui.ThumbActive : Ui.Thumb);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_thumbDrag)
            {
                var thumb = ThumbRect();
                var travel = ClientSize.Height - BarInset * 2 - thumb.Height;
                if (travel > 0)
                {
                    var delta = e.Y - _dragOriginY;
                    SetOffset(_dragOriginOffset + (int)Math.Round(delta * (_maxOffset / (double)travel)));
                }
                return;
            }

            var overThumb = ThumbRect().Contains(e.Location);
            if (overThumb != _thumbHover)
            {
                _thumbHover = overThumb;
                Invalidate();
            }

            var index = IndexAtY(e.Y);
            if (index != _hover)
            {
                _hover = index;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            var thumb = ThumbRect();
            if (!thumb.IsEmpty && thumb.Contains(e.Location))
            {
                _thumbDrag = true;
                _dragOriginY = e.Y;
                _dragOriginOffset = _offset;
                Capture = true;
                Invalidate();
                return;
            }

            var index = IndexAtY(e.Y);
            if (index >= 0) SelectedIndex = index;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _thumbDrag = false;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover == -1 && !_thumbHover) return;
            _hover = -1;
            _thumbHover = false;
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (IndexAtY(e.Y) >= 0 && ItemActivated != null) ItemActivated(this, EventArgs.Empty);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            // 列表装得下就别"吃掉"滚轮 —— 让父级面板照常滚，否则光标停在
            // 一块没法滚的列表上时，面板就滚不动了。
            if (_maxOffset <= 0) return;

            SetOffset(_offset - (e.Delta / 120) * _itemHeight);

            // 双重保险：
            //  1) Mark 给父容器（ScrollViewport）看，让它别把面板内容也一起滚了；
            //  2) Handled 是 WinForms 官方的"别再往上冒泡"开关。
            // 只做 1) 或只做 2) 都能好，但两者依赖的机制不同，一起做才稳。
            WheelRouter.Mark();
            var handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (key == Keys.Up || key == Keys.Down) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode == Keys.Down && _selected < _items.Count - 1)
            {
                SelectedIndex = _selected + 1;
                EnsureVisible(_selected);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Up && _selected > 0)
            {
                SelectedIndex = _selected - 1;
                EnsureVisible(_selected);
                e.Handled = true;
            }
        }

        private void EnsureVisible(int index)
        {
            var top = index * _itemHeight;
            var bottom = top + _itemHeight;
            if (top < _offset) SetOffset(top);
            else if (bottom > _offset + ClientSize.Height) SetOffset(bottom - ClientSize.Height);
        }
    }

    /// <summary>
    /// 自绘滑块（替代 TrackBar）。TrackBar 的轨道和拇指是系统主题画的，
    /// 在深色面板上偏亮偏灰，跟自绘的圆角控件放一起不协调。
    /// </summary>
    internal sealed class ModernSlider : Control
    {
        private const int ThumbRadius = 7;
        private const int TrackThickness = 4;

        private int _min = 0;
        private int _max = 100;
        private int _value;
        private bool _hover;
        private bool _dragging;

        public event EventHandler ValueChanged;

        public ModernSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            BackColor = Theme.Panel;
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public int Minimum
        {
            get { return _min; }
            set
            {
                _min = value;
                if (_max < _min) _max = _min;
                Value = _value;
                Invalidate();
            }
        }

        public int Maximum
        {
            get { return _max; }
            set
            {
                _max = value;
                if (_min > _max) _min = _max;
                Value = _value;
                Invalidate();
            }
        }

        public int Value
        {
            get { return _value; }
            set
            {
                var v = value;
                if (v < _min) v = _min;
                if (v > _max) v = _max;
                if (v == _value) return;

                _value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        private int TrackLeft { get { return ThumbRadius + 3; } }
        private int TrackRight { get { return Width - ThumbRadius - 3; } }

        private int ThumbX()
        {
            var span = TrackRight - TrackLeft;
            if (span <= 0) return TrackLeft;

            var range = _max - _min;
            var t = range <= 0 ? 0.0 : (_value - _min) / (double)range;
            return TrackLeft + (int)Math.Round(span * t);
        }

        private void SetValueFromX(int x)
        {
            var span = TrackRight - TrackLeft;
            if (span <= 0) return;

            var t = (x - TrackLeft) / (double)span;
            if (t < 0) t = 0;
            if (t > 1) t = 1;

            Value = _min + (int)Math.Round((_max - _min) * t);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Panel);
            Ui.PrepareShape(e.Graphics);

            var centerY = Height / 2;
            var x0 = TrackLeft;
            var x1 = TrackRight;
            var thumbX = ThumbX();

            Ui.FillRounded(e.Graphics,
                new RectangleF(x0, centerY - TrackThickness / 2f, Math.Max(0, x1 - x0), TrackThickness),
                TrackThickness / 2f, Ui.Track);

            var filled = thumbX - x0;
            if (filled > 1)
            {
                Ui.FillRounded(e.Graphics,
                    new RectangleF(x0, centerY - TrackThickness / 2f, filled, TrackThickness),
                    TrackThickness / 2f, Theme.Accent);
            }

            var thumbColor = (_dragging || _hover) ? Color.White : Theme.Text;
            Ui.FillRounded(e.Graphics,
                new RectangleF(thumbX - ThumbRadius, centerY - ThumbRadius, ThumbRadius * 2, ThumbRadius * 2),
                ThumbRadius, thumbColor);

            // 拇指外面加一圈很淡的描边，避免在浅色底图上糊掉
            using (var pen = new Pen(Color.FromArgb(70, 0, 0, 0)))
            {
                e.Graphics.DrawEllipse(pen, thumbX - ThumbRadius, centerY - ThumbRadius,
                    ThumbRadius * 2, ThumbRadius * 2);
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

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            _dragging = true;
            Capture = true;
            SetValueFromX(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) SetValueFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
            Invalidate();
        }

        // 刻意**不覆写 OnMouseWheel**：滚轮滚过滑块时应当滚动面板，而不是偷偷改数值。
        // 需要精确调节的场合有左右方向键（见 OnKeyDown）。

        protected override bool IsInputKey(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (key == Keys.Left || key == Keys.Right) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode == Keys.Left) { Value = _value - 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { Value = _value + 1; e.Handled = true; }
        }
    }

    /// <summary>
    /// 圆角按钮。WinForms 的 Button 没法直接设圆角，这里用 Region 把四个角裁掉，
    /// 顺便去掉系统视觉样式（UseVisualStyleBackColor=false），
    /// 免得浅色的系统按钮底盖掉我们自己设的颜色。
    /// </summary>
    internal sealed class ModernButton : Button
    {
        private const float Radius = 6f;

        public ModernButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            ForeColor = Color.White;
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyRegion();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyRegion();
        }

        protected override void OnBackColorChanged(EventArgs e)
        {
            base.OnBackColorChanged(e);
            // 悬浮 / 按下状态跟着底色一起推导，不然换色后悬浮色还是旧的
            FlatAppearance.MouseOverBackColor = Shade(BackColor, 0.12f);
            FlatAppearance.MouseDownBackColor = Shade(BackColor, -0.12f);
        }

        private void ApplyRegion()
        {
            if (Width <= 2 || Height <= 2) return;

            using (var path = Theme.RoundedRect(new RectangleF(0, 0, Width, Height), Radius))
            {
                var old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        private static Color Shade(Color color, float amount)
        {
            if (amount >= 0f)
            {
                return Color.FromArgb(color.A,
                    color.R + (int)Math.Round((255 - color.R) * amount),
                    color.G + (int)Math.Round((255 - color.G) * amount),
                    color.B + (int)Math.Round((255 - color.B) * amount));
            }

            var k = 1f + amount;
            return Color.FromArgb(color.A,
                (int)Math.Round(color.R * k),
                (int)Math.Round(color.G * k),
                (int)Math.Round(color.B * k));
        }
    }
}
