using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace TrpgMaps
{
    /// <summary>
    /// 绘图面板。和「地图设置 / 网络」两个面板**形态完全不同**：
    ///
    ///  - 那两个是"左侧滑出的抽屉 + 灰色遮罩"，打开就吃掉整个画布的点击；
    ///  - 这个是"右上角的浮窗"：**没有遮罩**、可拖动、大小固定（内容自己纵向滚动），
    ///    面板以外的画布照常能点 —— 因为它就是拿来在地图上画东西的，
    ///    要是盖上遮罩就没法画了。
    ///
    /// 它自己持有全部交互状态（分类 / 素材 / 画笔模式 / 笔刷形状 / 原点与半径），
    /// 由 MainForm 把画布的鼠标事件转发进来。所有写操作都落到 <see cref="DrawingStore"/>，
    /// 写完抛 <see cref="DrawingChanged"/> 让 MainForm 去存盘和广播。
    /// </summary>
    internal sealed class DrawPanel : Panel
    {
        private const int TitleHeight = 36;
        private const int PanelWidth = 334;
        private const int PanelInset = 14;
        private const int CloseSize = 22;

        private readonly MapCanvas _canvas;
        private readonly DrawingStore _drawings;
        private readonly TerrainCatalog _catalog;
        private readonly TerrainImageCache _cache;

        private ScrollViewport _scroll;
        private FlowLayoutPanel _content;

        // ---- 分类 / 素材 ----
        private ModernButton _kindTerrain, _kindEntity, _kindItem;
        private ModernList _assetList;
        private Label _assetHint;
        private Label _statusLabel;
        private readonly Dictionary<string, string> _selectedFile = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Image> _thumbs = new Dictionary<string, Image>(StringComparer.Ordinal);
        private string _kind = DrawKind.Terrain;

        // ---- 画笔 ----
        private ModernButton _modeManual, _modeBrush;
        private readonly Dictionary<BrushShape, ModernButton> _shapeButtons = new Dictionary<BrushShape, ModernButton>();
        private ModernDropDown _fanCombo;
        private FlowLayoutPanel _fanRow;
        private ModernButton _eraserButton;
        private Label _modeHint;

        private BrushMode _mode = BrushMode.Manual;
        private BrushShape _shape = BrushShape.Sphere;
        private double _fanAngle = 90.0;
        private bool _eraser;

        // ---- 选区方式（严格 / 宽泛）----
        /// <summary>
        /// false = 严格（格心落在形状里才算），true = 宽泛（形状真正压到的格都算，只相切不算）。
        /// 只影响"形状怎么落到格上"，形状本身（蓝色那层）两种方式完全一样。
        /// </summary>
        private bool _loose;
        private ModernButton _strictButton;
        private ModernButton _looseButton;
        private Label _selectHint;

        // ---- 清空 ----
        /// <summary>「清空本类」按钮 ——没选中素材时要灰掉，所以留成字段（见 ApplyButtonStates）。
        /// 注意：文字叫「本类」，行为是"只摘当前选中的那一张素材"（按用户要求只改文案）。</summary>
        private ModernButton _clearAssetButton;

        // ---- 笔刷状态机 ----
        private bool _hasOrigin;
        private SnapPoint _origin;
        private int _radiusHalf;
        private bool _radiusFixed;
        private double _direction;
        private SnapPoint _hover;
        private Point _cursor;
        private bool _manualPainting;
        private Point _lastPaintPoint;

        /// <summary>
        /// 截图模式：锁住"鼠标驱动的预览刷新"。
        ///
        /// 为什么需要它：`--ui` 截图是**屏幕实拍**，窗口得真的显示出来，于是系统会把
        /// 真实的鼠标位置当成一次 `WM_MOUSEMOVE` 灌进来 —— 脚本刚摆好的"原点 + 2 格半径"
        /// 会被鼠标所在的位置覆盖，截出来的圆有多大纯看鼠标停在哪儿（实测半径 9 半格、
        /// 读数写着 22.5 尺，和脚本设的 2 格对不上）。这是纯测试链路的污染，跟功能无关。
        ///
        /// 只由 <see cref="SetupShotBrush"/> 置位，正常使用永远不会打开。
        /// </summary>
        private bool _shotLocked;

        /// <summary>截图用：锁 / 解锁鼠标驱动的预览刷新（见 <see cref="_shotLocked"/>）。</summary>
        public bool ShotLocked
        {
            get { return _shotLocked; }
            set { _shotLocked = value; }
        }

        // ---- 拖动窗口 ----
        private bool _draggingWindow;
        private Point _dragOrigin;
        private bool _closeHover;
        private bool _closePressed;

        /// <summary>面板要关闭（点 × 或按 Esc）。MainForm 负责真的收起来。</summary>
        public event Action CloseRequested;

        /// <summary>绘图内容变了。参数 true 表示需要落盘。</summary>
        public event Action<bool> DrawingChanged;

        public DrawPanel(MapCanvas canvas, DrawingStore drawings, TerrainCatalog catalog, TerrainImageCache cache)
        {
            _canvas = canvas;
            _drawings = drawings;
            _catalog = catalog;
            _cache = cache;

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            BackColor = Theme.Panel;
            Size = new Size(PanelWidth, 560);

            BuildContent();
        }

        /// <summary>当前选中的分类。</summary>
        public string CurrentKind
        {
            get { return _kind; }
        }

        /// <summary>一行状态文本，写进 --ui / 日志用。</summary>
        public string StateText()
        {
            var asset = CurrentAsset();
            return "分类=" + DrawKind.Label(_kind) +
                   " 素材=" + (asset == null ? "(未选)" : asset.Name) +
                   " 本素材格数=" + (asset == null ? 0 : _drawings.CountAsset(_kind, asset.File)) +
                   " 模式=" + DrawGeometry.ModeLabel(_mode) +
                   " 形状=" + DrawGeometry.ShapeLabel(_shape) +
                   " 选区=" + (_loose ? "宽泛" : "严格") +
                   " 张角=" + _fanAngle.ToString("0") + "°" +
                   " 橡皮=" + (_eraser ? "开" : "关") +
                   " 原点=" + (_hasOrigin
                       ? ("(" + _origin.HX + "," + _origin.HY + ")" + (_radiusFixed ? " 半径已定" : " 待定半径"))
                       : "(未放)") +
                   " 数据=" + _drawings.Count + " 格 v" + _drawings.Version;
        }

        // ============================================================
        //  内容
        // ============================================================

        private void BuildContent()
        {
            _scroll = new ScrollViewport();
            _scroll.BackColor = Theme.Panel;
            _scroll.Bounds = new Rectangle(0, TitleHeight, Width, Height - TitleHeight);
            Controls.Add(_scroll);

            _content = new FlowLayoutPanel();
            _content.FlowDirection = FlowDirection.TopDown;
            _content.WrapContents = false;
            _content.AutoSize = true;
            _content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _content.BackColor = Theme.Panel;
            _scroll.SetContent(_content);

            var inner = PanelWidth - PanelInset * 2;

            // ---- 分类 ----
            AddRow(MakeLabel("绘制分类", 9f, Theme.Text), 20, inner);

            var kindRow = MakeRow();
            _kindTerrain = MakeToggle("地形", 84);
            _kindTerrain.Click += delegate { SetKind(DrawKind.Terrain); };
            kindRow.Controls.Add(_kindTerrain);

            _kindEntity = MakeToggle("实体", 84);
            _kindEntity.Margin = new Padding(6, 3, 0, 0);
            _kindEntity.Click += delegate { SetKind(DrawKind.Entity); };
            kindRow.Controls.Add(_kindEntity);

            _kindItem = MakeToggle("物品", 84);
            _kindItem.Margin = new Padding(6, 3, 0, 0);
            _kindItem.Click += delegate { SetKind(DrawKind.Item); };
            kindRow.Controls.Add(_kindItem);

            _content.Controls.Add(kindRow);

            // ---- 素材列表 ----
            _assetList = new ModernList();
            _assetList.BackColor = Theme.Track;
            _assetList.ItemHeight = 50;
            _assetList.RoundedRows = true;
            _assetList.ItemDraw = DrawAssetItem;
            _assetList.ItemActivated += delegate { CommitAssetSelection(); };
            _assetList.SelectedIndexChanged += delegate { CommitAssetSelection(); };
            AddRow(_assetList, 208, inner);

            _assetHint = MakeLabel("", 8.5f, Theme.TextDim);
            AddRow(_assetHint, 34, inner);

            AddSeparator(inner);

            // ---- 画笔模式 ----
            AddRow(MakeLabel("画笔模式", 9f, Theme.Text), 20, inner);

            var modeRow = MakeRow();
            _modeManual = MakeToggle("手动", 128);
            _modeManual.Click += delegate { SetMode(BrushMode.Manual); };
            modeRow.Controls.Add(_modeManual);

            _modeBrush = MakeToggle("笔刷", 128);
            _modeBrush.Margin = new Padding(6, 3, 0, 0);
            _modeBrush.Click += delegate { SetMode(BrushMode.Brush); };
            modeRow.Controls.Add(_modeBrush);

            _content.Controls.Add(modeRow);

            // ---- 选区方式（严格 / 宽泛）----
            //
            // 刻意摆在「笔刷形状」**上面**：面板高度封顶 560px，内容一直是需要滚的，
            // 而这个开关是常用的（要在地图上边看边切），摆太靠下就得先滚一屏才够得着。
            // 放在这里，打开面板不用滚就能看见。
            AddRow(MakeLabel("选区方式（笔刷模式）", 8.5f, Theme.TextDim), 18, inner);

            var selectRow = MakeRow();
            _strictButton = MakeToggle("严格", 128);
            _strictButton.Click += delegate { SetSelectionMode(false); };
            selectRow.Controls.Add(_strictButton);

            _looseButton = MakeToggle("宽泛", 128);
            _looseButton.Margin = new Padding(6, 3, 0, 0);
            _looseButton.Click += delegate { SetSelectionMode(true); };
            selectRow.Controls.Add(_looseButton);

            _content.Controls.Add(selectRow);

            _selectHint = MakeLabel("", 8.5f, Theme.TextDim);
            AddRow(_selectHint, 20, inner);

            AddSeparator(inner);

            // ---- 笔刷形状 ----
            var shapeLabel = MakeLabel("笔刷形状（笔刷模式）", 8.5f, Theme.TextDim);
            AddRow(shapeLabel, 18, inner);

            var shapeRow1 = MakeRow();
            AddShapeButton(shapeRow1, BrushShape.Line, "线形", 128, false);
            AddShapeButton(shapeRow1, BrushShape.Cone, "锥形/扇形", 128, true);
            _content.Controls.Add(shapeRow1);

            var shapeRow2 = MakeRow();
            AddShapeButton(shapeRow2, BrushShape.Cube, "立方", 128, false);
            AddShapeButton(shapeRow2, BrushShape.Sphere, "球形/圆形", 128, true);
            _content.Controls.Add(shapeRow2);

            // 扇形张角只在"锥形"下出现
            _fanRow = MakeRow();
            _fanCombo = new ModernDropDown();
            _fanCombo.SetItems(new List<string> { "张角 53°（D&D 喷吐）", "张角 60°", "张角 90°（四分之一圆）", "张角 120°" }, 2);
            _fanCombo.SelectedIndexChanged += delegate
            {
                _fanAngle = FanAngleFromIndex(_fanCombo.SelectedIndex);
                RefreshStatus();
            };
            _fanCombo.AutoSize = false;
            _fanCombo.Width = 258;
            _fanCombo.Height = 30;
            _fanCombo.Margin = new Padding(0, 0, 0, 0);
            _fanRow.Controls.Add(_fanCombo);
            _content.Controls.Add(_fanRow);

            _modeHint = MakeLabel("", 8.5f, Theme.TextDim);
            AddRow(_modeHint, 32, inner);

            AddSeparator(inner);

            // ---- 橡皮 / 清空 ----
            var eraseRow = MakeRow();
            _eraserButton = MakeToggle("橡皮擦", 84);
            _eraserButton.Click += delegate
            {
                _eraser = !_eraser;
                ApplyButtonStates();
                CancelSelection();
                RefreshStatus();
            };
            eraseRow.Controls.Add(_eraserButton);

            // 「清空本类」= 只摘掉**当前选中的那一张素材**画上去的格子。
            // 以前这里是按分类清（ClearKind），而地形类里同时铺了「高草丛」「火焰」etc. 时，
            // 点一下会把整类全清 —— 和「清空全部」看起来一模一样，等于没有这个按钮。
            // （按钮文字按用户要求叫「清空本类」，行为仍然是"只摘当前这一张素材"。）
            _clearAssetButton = MakeToggle("清空本类", 84);
            _clearAssetButton.Margin = new Padding(6, 3, 0, 0);
            _clearAssetButton.Click += delegate { ClearCurrentAsset(); };
            eraseRow.Controls.Add(_clearAssetButton);

            var clearAll = MakeToggle("清空全部", 84);
            clearAll.Margin = new Padding(6, 3, 0, 0);
            clearAll.BackColor = Theme.Danger;
            clearAll.Click += delegate { ClearAll(); };
            eraseRow.Controls.Add(clearAll);

            _content.Controls.Add(eraseRow);

            // ---- 状态 ----
            _statusLabel = MakeLabel("", 8.5f, Theme.Link);
            AddRow(_statusLabel, 58, inner);

            _content.PerformLayout();
            _scroll.RefreshLayout();

            RefreshAssets();
            ApplyButtonStates();
            RefreshStatus();
        }

        private void AddShapeButton(FlowLayoutPanel row, BrushShape shape, string text, int width, bool second)
        {
            var button = MakeToggle(text, width);
            if (second) button.Margin = new Padding(6, 3, 0, 0);
            button.Click += delegate { SetShape(shape); };
            row.Controls.Add(button);
            _shapeButtons[shape] = button;
        }

        private static double FanAngleFromIndex(int index)
        {
            switch (index)
            {
                case 0: return 53.0;
                case 1: return 60.0;
                case 3: return 120.0;
                default: return 90.0;
            }
        }

        // ---- 小工具（与主面板保持同一套观感，但宽度按本面板算）----

        private FlowLayoutPanel MakeRow()
        {
            var row = new FlowLayoutPanel();
            row.FlowDirection = FlowDirection.LeftToRight;
            row.WrapContents = false;
            row.AutoSize = true;
            row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            row.BackColor = Theme.Panel;
            row.MaximumSize = new Size(PanelWidth - PanelInset * 2, 0);
            row.Margin = new Padding(0, 0, 0, 10);
            return row;
        }

        private void AddRow(Control control, int height, int width)
        {
            var row = MakeRow();
            control.AutoSize = false;
            control.Width = width;
            control.Height = height;
            control.Margin = new Padding(0, 0, 0, 0);
            row.Controls.Add(control);
            _content.Controls.Add(row);
        }

        private void AddSeparator(int width)
        {
            var line = new Panel();
            line.BackColor = Theme.Separator;
            AddRow(line, 1, width);
        }

        private static Label MakeLabel(string text, float size, Color color)
        {
            var label = new Label();
            label.Text = text;
            label.Font = Ui.Font(size);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.AutoSize = false;
            return label;
        }

        private static ModernButton MakeToggle(string text, int width)
        {
            var button = new ModernButton();
            button.Text = text;
            button.BackColor = Theme.Button;
            button.ForeColor = Color.White;
            button.Font = Ui.Font(8.5f);
            button.Height = 28;
            button.AutoSize = false;
            button.Width = width;
            button.Margin = new Padding(0, 3, 0, 0);
            button.Cursor = Cursors.Hand;
            return button;
        }

        private static void MarkToggle(ModernButton button, bool on)
        {
            if (button == null) return;
            button.BackColor = on ? Theme.Accent : Theme.Button;
        }

        // ============================================================
        //  状态与界面同步
        // ============================================================

        private TerrainAsset CurrentAsset()
        {
            string file;
            if (!_selectedFile.TryGetValue(_kind, out file)) return null;
            return _catalog.Resolve(_kind, file);
        }

        public void SetKind(string kind)
        {
            var normalized = DrawKind.Normalize(kind);
            if (normalized == _kind) return;
            _kind = normalized;
            CancelSelection();
            RefreshAssets();
            ApplyButtonStates();
            RefreshStatus();
        }

        public void SetMode(BrushMode mode)
        {
            if (_mode == mode) return;
            _mode = mode;
            CancelSelection();
            ApplyButtonStates();
            RefreshStatus();
        }

        public void SetShape(BrushShape shape)
        {
            if (_shape == shape) return;
            _shape = shape;
            CancelSelection();
            ApplyButtonStates();
            RefreshStatus();
        }

        /// <summary>
        /// 切选区方式。**刻意不 <see cref="CancelSelection"/>**：严格↔宽泛只是"同一片形状怎么落到格上"，
        /// 原点 / 半径 / 朝向都还有效，正在量半径的时候点一下就能直接对比两边的格数，
        /// 不用重新点原点再拖一遍半径。
        /// </summary>
        public void SetSelectionMode(bool loose)
        {
            if (_loose == loose) return;
            _loose = loose;
            ApplyButtonStates();
            UpdateBrushPreview();
            RefreshStatus();
            AppLog.Write("绘图：选区方式 = " + (_loose ? "宽泛（形状真正压到的格都算，只相切不算）" : "严格（格心在形状内）"));
        }

        /// <summary>当前选区方式（写日志 / 自检 / 截图用）。</summary>
        public bool LooseSelection
        {
            get { return _loose; }
        }

        private void ApplyButtonStates()
        {
            MarkToggle(_kindTerrain, _kind == DrawKind.Terrain);
            MarkToggle(_kindEntity, _kind == DrawKind.Entity);
            MarkToggle(_kindItem, _kind == DrawKind.Item);

            MarkToggle(_modeManual, _mode == BrushMode.Manual);
            MarkToggle(_modeBrush, _mode == BrushMode.Brush);

            foreach (var pair in _shapeButtons)
            {
                MarkToggle(pair.Value, _mode == BrushMode.Brush && _shape == pair.Key);
            }

            if (_fanRow != null) _fanRow.Visible = _mode == BrushMode.Brush && _shape == BrushShape.Cone;

            MarkToggle(_strictButton, !_loose);
            MarkToggle(_looseButton, _loose);

            if (_selectHint != null)
            {
                _selectHint.Text = _loose
                    ? "宽泛：形状真正压到的格都算（只相切不算）"
                    : "严格：只有格心落在形状里的格才算";
            }

            if (_eraserButton != null)
            {
                _eraserButton.BackColor = _eraser ? Theme.Locked : Theme.Button;
            }

            // 「清空本类」没有素材可选时是没意义的 —— 灰掉，避免误点后又以为清空了整张图
            if (_clearAssetButton != null)
            {
                _clearAssetButton.Enabled = CurrentAsset() != null;
            }

            if (_modeHint != null)
            {
                _modeHint.Text = _mode == BrushMode.Manual
                    ? "按住左键在地图上拖动，扫过的格子覆盖素材。"
                    : "先点一下放原点，再拖出半径后点第二下成形；扇形还要转好角度点第三下。右键取消。";
            }

            if (_scroll != null) _scroll.RefreshLayout();

            // 标题栏里的「分类 · 模式」这几个字是本控件自己 OnPaint 画上去的，
            // 换分类/换模式只让子按钮重绘是不够的 —— 面板不 Invalidate 的话，
            // 这行字会一直停在旧值（点「笔刷」后仍写「手动」）。子控件的重绘
            // 不会自动带上父控件，所以这里必须显式刷一次。
            Invalidate();
        }

        /// <summary>重扫素材并把列表刷新一遍。</summary>
        public void RefreshAssets()
        {
            _catalog.Reload();
            _cache.Clear();
            DisposeThumbs();

            var list = _catalog.Of(_kind);
            _assetList.BeginUpdate();
            _assetList.Items.Clear();
            foreach (var asset in list) _assetList.Items.Add(asset);
            _assetList.EndUpdate();

            var wanted = list.Count * _assetList.ItemHeight + 2;
            if (wanted < 56) wanted = 56;
            if (wanted > 208) wanted = 208;
            _assetList.Height = wanted;

            // 选中项尽量保持
            string previous;
            if (_selectedFile.TryGetValue(_kind, out previous))
            {
                for (var i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i].File, previous, StringComparison.Ordinal))
                    {
                        _assetList.SelectSilently(i);
                        break;
                    }
                }
            }

            if (_assetHint != null)
            {
                _assetHint.Text = list.Count == 0
                    ? "（「" + DrawKind.Label(_kind) + "」还没有素材）\n把图片放进 exe 同级的 " + _kind + "\\ 目录，再点「重新扫描」"
                    : _kind + "\\ 共 " + list.Count + " 项";
            }

            if (_scroll != null) _scroll.RefreshLayout();
        }

        private void CommitAssetSelection()
        {
            var asset = _assetList.SelectedItem as TerrainAsset;
            if (asset == null) return;
            _selectedFile[_kind] = asset.File;
            _eraser = false;
            ApplyButtonStates();
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (_statusLabel == null) return;

            var asset = CurrentAsset();
            var lines = new List<string>();

            if (_eraser)
            {
                lines.Add("橡皮擦：擦掉「" + DrawKind.Label(_kind) + "」这一层");
            }
            else if (asset == null)
            {
                lines.Add("还没选素材 —— 先在上面点一项");
            }
            else
            {
                lines.Add("当前：" + asset.Name + "（" + asset.TraitText() + "）");
                if (!string.IsNullOrEmpty(asset.Note)) lines.Add(asset.Note);

                // 「清空本类」只摘这一张 —— 把"这一张到底占了多少格"摆出来，
                // 免得点和"清空全部"之间还要靠猜。
                var mine = _drawings.CountAsset(_kind, asset.File);
                lines.Add("本素材已画 " + mine + " 格（「清空本类」只摘这一张）");
            }

            if (_hasOrigin)
            {
                var where = _origin.IsCellCenter ? "格心" : "网格点";
                var text = "原点：" + where + " (" + _origin.HX + "," + _origin.HY + ")";
                if (_radiusFixed) text += "  半径 " + DrawGeometry.DescribeDistance(_radiusHalf * DrawGeometry.FeetPerHalfCell);
                else text += "  —— 移动鼠标量半径，再点一下";
                lines.Add(text);
            }

            lines.Add("已画 " + _drawings.Count + " 格");

            _statusLabel.Text = string.Join("\n", lines.ToArray());
        }

        // ============================================================
        //  素材列表绘制
        // ============================================================

        private void DrawAssetItem(ListItemDrawArgs args)
        {
            var asset = args.Item as TerrainAsset;
            if (asset == null) return;

            var box = args.Bounds;
            var selected = args.Selected;
            var background = selected ? Theme.ButtonHover : (args.Hover ? Theme.Card : Theme.Track);
            Ui.FillRounded(args.Graphics, box, 6f, background);

            var slot = new Rectangle(box.X + 7, box.Y + 7, Math.Max(1, box.Height - 14), Math.Max(1, box.Height - 14));
            var thumb = GetThumb(asset);
            if (thumb != null)
            {
                args.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                args.Graphics.DrawImage(thumb, slot);
            }
            else
            {
                Ui.FillRounded(args.Graphics, slot, 4f, Theme.Background);
            }

            Ui.PrepareText(args.Graphics);
            var textRect = new Rectangle(slot.Right + 9, box.Y + 2,
                Math.Max(0, box.Right - slot.Right - 18), box.Height - 4);

            using (var format = new StringFormat())
            {
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;

                var nameRect = new Rectangle(textRect.X, textRect.Y, textRect.Width, textRect.Height / 2 + 2);
                var traitRect = new Rectangle(textRect.X, textRect.Y + textRect.Height / 2, textRect.Width, textRect.Height / 2);

                using (var brush = new SolidBrush(Theme.Text))
                {
                    args.Graphics.DrawString(asset.Name, Ui.Font(9f, FontStyle.Bold), brush, nameRect, format);
                }
                using (var brush = new SolidBrush(Theme.TextDim))
                {
                    args.Graphics.DrawString(asset.TraitText(), Ui.Font(7.5f), brush, traitRect, format);
                }
            }
        }

        private Image GetThumb(TerrainAsset asset)
        {
            Image cached;
            if (_thumbs.TryGetValue(asset.File, out cached)) return cached;

            Image thumb = null;
            try
            {
                using (var fs = new FileStream(asset.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    using (var image = Image.FromStream(fs))
                    {
                        const int box = 36;
                        var scale = Math.Min(box / (double)image.Width, box / (double)image.Height);
                        if (scale > 1d) scale = 1d;

                        var w = Math.Max(1, (int)Math.Round(image.Width * scale));
                        var h = Math.Max(1, (int)Math.Round(image.Height * scale));
                        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.Clear(Theme.Background);
                            g.DrawImage(image, 0, 0, w, h);
                        }
                        thumb = bmp;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("生成素材缩略图失败：" + asset.Path, ex);
            }

            _thumbs[asset.File] = thumb;
            return thumb;
        }

        private void DisposeThumbs()
        {
            foreach (var image in _thumbs.Values)
            {
                if (image != null) image.Dispose();
            }
            _thumbs.Clear();
        }

        // ============================================================
        //  清空
        // ============================================================

        /// <summary>
        /// 清空**当前选中的那一张素材**（当前分类下）。
        ///
        /// 和「清空全部」的区别就是粒度：地形类里可能同时铺了「高草丛」「火焰」「墙」……
        /// 这里只摘掉列表里正选着的那一张，别的素材一格都不动。
        /// 没选中素材时按钮是灰的（见 <see cref="ApplyButtonStates"/>），点了也不会有事。
        /// </summary>
        private void ClearCurrentAsset()
        {
            var asset = CurrentAsset();
            if (asset == null)
            {
                RefreshStatus();
                AppLog.Write("绘图：清空本类 —— 还没选中素材，忽略");
                return;
            }

            var removed = _drawings.ClearAsset(_kind, asset.File);
            CancelSelection();
            RefreshStatus();
            Notify(true);
            AppLog.Write("绘图：清空素材「" + asset.Name + "」（" + DrawKind.Label(_kind) + "）" + removed + " 格" +
                         "，其余分类/素材保留");
        }

        private void ClearAll()
        {
            var removed = _drawings.ClearAll();
            CancelSelection();
            RefreshStatus();
            Notify(true);
            AppLog.Write("绘图：清空全部 " + removed + " 格");
        }

        // ============================================================
        //  画布鼠标（由 MainForm 转发）
        // ============================================================

        public void OnMapMouseDown(MouseEventArgs e)
        {
            if (_shotLocked) return;          // 截图模式：别让真实鼠标破坏摆好的画面
            _cursor = e.Location;

            if (e.Button == MouseButtons.Right)
            {
                CancelSelection();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            if (_mode == BrushMode.Manual)
            {
                _manualPainting = true;
                _lastPaintPoint = e.Location;
                ApplyAt(e.Location, false);
                _canvas.Invalidate();
                return;
            }

            // 笔刷模式：第一次点 = 放原点；第二次 = 定半径；扇形第三次 = 定角度
            if (!_hasOrigin)
            {
                _origin = DrawGeometry.Snap(e.X, e.Y, _canvas.CellPixels);
                _hasOrigin = true;
                _radiusFixed = false;
                _radiusHalf = 0;
                _direction = 0;
                RefreshStatus();
                UpdateBrushPreview();
                return;
            }

            _hover = DrawGeometry.Snap(e.X, e.Y, _canvas.CellPixels);

            if (!_radiusFixed)
            {
                _radiusHalf = DrawGeometry.HalfUnitsBetween(_origin, _hover);
                if (_shape == BrushShape.Cone && _radiusHalf > 0)
                {
                    // 扇形：半径先定下来，角度继续跟着鼠标
                    _radiusFixed = true;
                    _direction = AngleTo(_origin, _hover);
                    RefreshStatus();
                    UpdateBrushPreview();
                    return;
                }
                CommitBrush();
                return;
            }

            CommitBrush();
        }

        public void OnMapMouseMove(MouseEventArgs e)
        {
            if (_shotLocked) return;          // 截图模式：别让真实鼠标破坏摆好的画面
            _cursor = e.Location;

            if (_mode == BrushMode.Manual)
            {
                if (!_manualPainting) return;
                ApplyAt(e.Location, true);
                _lastPaintPoint = e.Location;
                _canvas.Invalidate();
                return;
            }

            if (!_hasOrigin)
            {
                var preview = new DrawPreview();
                preview.HasHover = true;
                preview.Hover = DrawGeometry.Snap(e.X, e.Y, _canvas.CellPixels);
                preview.Cursor = e.Location;
                preview.Shape = _shape;
                preview.Eraser = _eraser;
                _canvas.Preview = preview;
                return;
            }

            UpdateBrushPreview();
        }

        public void OnMapMouseUp(MouseEventArgs e)
        {
            if (_shotLocked) return;          // 截图模式：别让真实鼠标破坏摆好的画面
            if (e.Button == MouseButtons.Right) return;

            if (_mode == BrushMode.Manual)
            {
                if (!_manualPainting) return;
                _manualPainting = false;
                Notify(true);
                return;
            }

            // 笔刷模式在 MouseDown 里就落笔了，这里不用做事
        }

        public void OnMapMouseLeave()
        {
            if (_shotLocked) return;          // 截图模式：别让真实鼠标破坏摆好的画面
            if (_mode == BrushMode.Manual)
            {
                if (!_manualPainting) return;
                _canvas.Invalidate();
                return;
            }

            if (!_hasOrigin && _canvas.Preview != null)
            {
                _canvas.Preview = null;
            }
        }

        private static double AngleTo(SnapPoint from, SnapPoint to)
        {
            var dx = to.HX - from.HX;
            var dy = to.HY - from.HY;
            if (dx == 0 && dy == 0) return 0;
            return Math.Atan2(dy, dx) * 180.0 / Math.PI;
        }

        /// <summary>手动模式：把鼠标扫过的格子刷一遍（两点之间补线，防止快速拖动漏格）。</summary>
        private void ApplyAt(Point location, bool fromLast)
        {
            if (_eraser)
            {
                // 橡皮不需要"选素材"
            }
            else if (CurrentAsset() == null)
            {
                return;
            }

            var cell = _canvas.CellPixels;
            var current = DrawGeometry.Snap(location.X, location.Y, cell);
            var cells = new List<GridCell>();

            if (fromLast)
            {
                var previous = DrawGeometry.Snap(_lastPaintPoint.X, _lastPaintPoint.Y, cell);
                cells = DrawGeometry.LineCells(previous, current, cell);
            }
            else
            {
                var single = DrawGeometry.CellAt(location.X, location.Y, cell);
                cells.Add(single);
            }

            if (cells.Count == 0) return;

            var asset = CurrentAsset();
            var changed = _eraser
                ? _drawings.Erase(_kind, cells)
                : _drawings.Paint(_kind, cells, asset.File);

            if (changed > 0)
            {
                RefreshStatus();
                Notify(false);
            }
        }

        /// <summary>笔刷模式：按当前形状落笔。</summary>
        private void CommitBrush()
        {
            if (_radiusHalf <= 0 && _shape != BrushShape.Line)
            {
                CancelSelection();
                return;
            }

            var asset = CurrentAsset();
            if (!_eraser && asset == null) return;

            var target = _hover;
            var cells = DrawGeometry.Region(_shape, _origin, target, _radiusHalf,
                _direction, _fanAngle, _canvas.CellPixels, _loose);
            if (cells.Count == 0)
            {
                CancelSelection();
                return;
            }

            var changed = _eraser
                ? _drawings.Erase(_kind, cells)
                : _drawings.Paint(_kind, cells, asset.File);

            AppLog.Write("绘图笔刷：" + DrawGeometry.ShapeLabel(_shape) +
                         " 半径 " + _radiusHalf + " 半格 → " + cells.Count + " 格，改动 " + changed);

            CancelSelection();
            RefreshStatus();
            Notify(true);
        }

        private void CancelSelection()
        {
            _hasOrigin = false;
            _radiusFixed = false;
            _radiusHalf = 0;
            _manualPainting = false;
            if (_canvas != null) _canvas.Preview = null;
            RefreshStatus();
        }

        /// <summary>刷新笔刷模式的预览（选区、候选点、距离读数）。</summary>
        private void UpdateBrushPreview()
        {
            var cell = _canvas.CellPixels;
            var preview = new DrawPreview();
            preview.Shape = _shape;
            preview.Eraser = _eraser;
            preview.Cursor = _cursor;
            preview.HasOrigin = _hasOrigin;
            preview.Origin = _origin;

            if (_hasOrigin)
            {
                var hovered = DrawGeometry.Snap(_cursor.X, _cursor.Y, cell);
                preview.HasHover = true;
                preview.Hover = hovered;
                _hover = hovered;

                var radiusHalf = _radiusFixed ? _radiusHalf : DrawGeometry.HalfUnitsBetween(_origin, hovered);
                if (!_radiusFixed) _radiusHalf = radiusHalf;

                var direction = AngleTo(_origin, hovered);
                _direction = direction;

                preview.Cells = DrawGeometry.Region(_shape, _origin, hovered, radiusHalf,
                    direction, _fanAngle, cell, _loose);

                // 蓝色"实际形状"层：半径 / 朝向 / 张角都按**半格**与**度**给过去，
                // 具体怎么画（圆 / 扇形 / 方 / 线段）由 DrawOverlayRenderer 决定。
                // 线形用不到半径，它的形状就是 origin→Hover 那一段。
                preview.HasShapeOutline = true;
                preview.RadiusHalf = radiusHalf;
                preview.Direction = direction;
                preview.FanAngle = _fanAngle;

                // 横竖方向上的候选落点（格心 / 边中点 / 交点）。
                // 范围按**画布**尺寸算：够覆盖到屏幕外一点点就行，不必铺到天边。
                var diagonal = Math.Sqrt((double)_canvas.Width * _canvas.Width +
                                         (double)_canvas.Height * _canvas.Height);
                var reach = (int)Math.Min(60, Math.Ceiling(diagonal / Math.Max(1.0, cell / 2.0)));
                preview.Guides = DrawGeometry.GuidePoints(_origin, reach);

                var feet = DrawGeometry.HalfUnitsBetween(_origin, hovered) * DrawGeometry.FeetPerHalfCell;
                preview.DistanceText = DrawGeometry.DescribeDistance(feet);
            }

            _canvas.Preview = preview;
            _canvas.Invalidate();
        }

        private void Notify(bool save)
        {
            if (DrawingChanged != null) DrawingChanged(save);
        }

        /// <summary>面板被收起时调用：清掉所有选区状态（已画的内容保留）。</summary>
        public void CloseSelection()
        {
            CancelSelection();
            if (_canvas != null) _canvas.Preview = null;
        }

        /// <summary>外部改了绘图数据之后，让状态行跟上。</summary>
        public void RefreshStatusText()
        {
            RefreshStatus();
        }

        /// <summary>无人值守截图用：选中素材列表的第一项，回到手动模式。</summary>
        public void SelectFirstAssetForShot()
        {
            _shotLocked = false;          // 这一步不摆选区，把鼠标还给系统（见 _shotLocked）
            if (_assetList != null && _assetList.Count > 0)
            {
                _assetList.SelectedIndex = 0;
                CommitAssetSelection();
            }
            SetMode(BrushMode.Manual);
            RefreshStatus();
        }

        /// <summary>
        /// 无人值守截图用：摆出一个已经放好原点、正在量半径的笔刷选区。
        /// 参数都是**格坐标**（不是像素）—— 原点取单元格心，光标放在另一个格心上，
        /// 这样截图上能同时看到半透明红选区、半透明蓝实际形状、横竖方向上的候选点和距离读数。
        /// </summary>
        public void SetupShotBrush(int originCellX, int originCellY, int hoverCellX, int hoverCellY,
            BrushShape shape = BrushShape.Sphere, double hoverOffsetHalfUnits = 0.0)
        {
            var cell = _canvas.CellPixels;

            SetMode(BrushMode.Brush);
            SetShape(shape);

            _origin = DrawGeometry.Snap((originCellX + 0.5) * cell, (originCellY + 0.5) * cell, cell);
            _hasOrigin = true;
            _radiusFixed = false;

            // 默认把光标放在某个格的**格心**上（半径一定是整数格）；
            // hoverOffsetHalfUnits > 0 时改成"原点往右这么多半格"，可以落在格线交点上 ——
            // 半径 1.5 格（3 半格）这种"圆正好与格线相切"的画面就是靠它拍的。
            var cursorX = hoverOffsetHalfUnits > 0
                ? (originCellX + 0.5) * cell + hoverOffsetHalfUnits * cell / 2.0
                : (hoverCellX + 0.5) * cell;
            _cursor = new Point(
                (int)Math.Round(cursorX),
                (int)Math.Round((hoverCellY + 0.5) * cell));

            UpdateBrushPreview();
            RefreshStatus();

            // 摆好就锁住：窗口显示的那一刻系统会把真实鼠标位置当 WM_MOUSEMOVE 灌进来，
            // 不锁的话上面这两行算了半天，下一秒就被真实鼠标覆盖掉（见 _shotLocked）。
            _shotLocked = true;
        }

        /// <summary>
        /// 无人值守截图用：按画布 / 侧栏 / 面板的**实际位置**挑两个一定看得见的格心，
        /// 再交给 <see cref="SetupShotBrush"/>。
        ///
        /// 为什么不写死格号：一格多少像素是跟着「网格尺度」滑块变的。截图序列里
        /// 前一步刚把滑块拖到 3.5 cm/格（一格 132 px），写死的 (6,4)→(16,10) 换算
        /// 出来是 x=860 → 2183，整块选区直接跑到 1200 px 宽的画布外面，
        /// 截图上既看不到半透明红、也看不到候选点，白白浪费一次启动。
        ///
        /// 半径默认只取 2 格：要拍的是"整个形状和底图的对位关系"，
        /// 半径给大一点圆（或扇形）就会伸出画布外，只剩一段弧，看不出形状对不对。
        ///
        /// `radiusHalfUnits > 0` 时按半格给死半径，用来拍特定半径的画面 ——
        /// 比如 3 半格（1.5 格）时圆正好**与格线相切**，那是"相切的一格算不算被扫到"的判据现场。
        /// </summary>
        public void SetupShotBrushVisible(int sidebarWidth, int panelLeft, int canvasWidth,
            BrushShape shape = BrushShape.Sphere, double radiusHalfUnits = 0.0)
        {
            var cell = Math.Max(1.0, _canvas.CellPixels);

            var left = sidebarWidth + 24;                        // 侧栏压住的那一条让开
            var limit = Math.Min(Math.Max(panelLeft, 0), Math.Max(canvasWidth, 1));
            var right = Math.Max(left + 3 * cell, limit - 24);   // 右上角浮窗的左边缘

            var originCellX = Math.Max(0, (int)(left / cell));
            var hoverCellX = Math.Min((int)(right / cell) - 1, originCellX + 2);
            if (hoverCellX <= originCellX) hoverCellX = originCellX + 1;

            var midCellY = Math.Max(0, (int)(_canvas.Height / 2.0 / cell));

            // radiusHalfUnits > 0 时按"半格"给死半径（可落在格线上，拍相切画面）；
            // 否则还是老规矩：光标放在右侧第 2 个格心上，半径 2 格。
            SetupShotBrush(originCellX, midCellY, hoverCellX, midCellY, shape,
                radiusHalfUnits > 0 ? radiusHalfUnits : 0.0);

            var actualHalf = DrawGeometry.HalfUnitsBetween(_origin, _hover);

            // 截图是给人和脚本看的，**必须能复盘**：把"为什么选中这几格"一路的中间值
            // 都记下来，否则以后图不对只能靠猜（第一版就吃过这个亏 —— 半径看着不对，
            // 却分不清是 cell 变了、还是 right 被夹住了、还是 clamp 生效了）。
            AppLog.Write("截图画笔：形状=" + DrawGeometry.ShapeLabel(shape) +
                         " 格=" + cell.ToString("0.0") + "px" +
                         " 侧栏=" + sidebarWidth + " 面板左=" + panelLeft + " 画布宽=" + canvasWidth +
                         " 左界=" + left.ToString("0.0") + " 右界=" + right.ToString("0.0") +
                         " → 原点格=(" + originCellX + "," + midCellY + ")" +
                         " 目标格=(" + hoverCellX + "," + midCellY + ")" +
                         " 半径=" + (actualHalf / 2.0).ToString("0.##") + " 格（" + actualHalf + " 半格）");

            // 复盘用：同一片形状，两种选区方式各覆盖多少格。红色到底比"严格"多算了哪几格，
            // 光看图不容易数清，把两个数直接写进日志最省事。
            var strict = DrawGeometry.Region(shape, _origin, _hover, _radiusHalf, _direction, _fanAngle,
                cell, false);
            var current = DrawGeometry.Region(shape, _origin, _hover, _radiusHalf, _direction, _fanAngle,
                cell, _loose);
            AppLog.Write("截图画笔：选区方式=" + (_loose ? "宽泛" : "严格") +
                         " 覆盖 " + current.Count + " 格（同样的形状按严格算 " + strict.Count + " 格）");
        }

        /// <summary>Esc / 右键之类的键盘清理。</summary>
        public void CancelAll()
        {
            CancelSelection();
        }

        // ============================================================
        //  面板自身：拖动 + 标题栏
        // ============================================================

        private Rectangle CloseRect
        {
            get { return new Rectangle(Width - CloseSize - 10, (TitleHeight - CloseSize) / 2, CloseSize, CloseSize); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (var path = Theme.RoundedRect(new RectangleF(0, 0, Width, Height), 10f))
            {
                using (var brush = new SolidBrush(Theme.Panel))
                {
                    g.FillPath(brush, path);
                }
                using (var pen = new Pen(Theme.Separator, 1f))
                {
                    g.DrawPath(pen, path);
                }
            }

            // 标题栏
            using (var brush = new SolidBrush(Theme.Card))
            using (var path = Theme.RoundedRect(new RectangleF(0, 0, Width, TitleHeight + 8), 10f))
            {
                var old = g.Clip;
                g.SetClip(new Rectangle(0, 0, Width, TitleHeight));
                g.FillPath(brush, path);
                g.Clip = old;
            }

            using (var pen = new Pen(Theme.Separator, 1f))
            {
                g.DrawLine(pen, 0, TitleHeight, Width, TitleHeight);
            }

            Ui.PrepareText(g);
            Ui.DrawText(g, "绘图", Ui.Font(11f, FontStyle.Bold), Color.White,
                new Rectangle(PanelInset, 0, 120, TitleHeight), ContentAlignment.MiddleLeft);

            Ui.DrawText(g, DrawKind.Label(_kind) + " · " + DrawGeometry.ModeLabel(_mode),
                Ui.Font(8.5f), Theme.TextDim,
                new Rectangle(PanelInset + 46, 0, Width - PanelInset - 46 - CloseSize - 18, TitleHeight),
                ContentAlignment.MiddleLeft);

            // 关闭按钮
            var close = CloseRect;
            if (_closeHover || _closePressed)
            {
                Ui.FillRounded(g, close, 5f, _closePressed ? Theme.ButtonHover : Theme.Card);
            }
            using (var pen = new Pen(Color.White, 1.6f))
            {
                var inset = 7;
                g.DrawLine(pen, close.Left + inset, close.Top + inset, close.Right - inset, close.Bottom - inset);
                g.DrawLine(pen, close.Right - inset, close.Top + inset, close.Left + inset, close.Bottom - inset);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            if (CloseRect.Contains(e.Location))
            {
                _closePressed = true;
                Invalidate();
                return;
            }

            if (e.Y < TitleHeight)
            {
                _draggingWindow = true;
                _dragOrigin = e.Location;
                Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            var overClose = CloseRect.Contains(e.Location);
            if (overClose != _closeHover)
            {
                _closeHover = overClose;
                Invalidate();
            }

            if (_draggingWindow)
            {
                var parent = Parent;
                if (parent == null) return;

                var left = Left + (e.X - _dragOrigin.X);
                var top = Top + (e.Y - _dragOrigin.Y);

                var maxLeft = parent.ClientSize.Width - Width;
                var maxTop = parent.ClientSize.Height - Height;
                if (left < 0) left = 0;
                if (top < 0) top = 0;
                if (maxLeft > 0 && left > maxLeft) left = maxLeft;
                if (maxTop > 0 && top > maxTop) top = maxTop;

                Location = new Point(left, top);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            var wasClose = _closePressed;
            _closePressed = false;
            _draggingWindow = false;
            Cursor = Cursors.Default;
            Invalidate();

            if (wasClose && CloseRect.Contains(e.Location) && CloseRequested != null)
            {
                CloseRequested();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_closeHover)
            {
                _closeHover = false;
                Invalidate();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_scroll != null)
            {
                _scroll.Bounds = new Rectangle(0, TitleHeight, Width, Math.Max(1, Height - TitleHeight));
                _scroll.RefreshLayout();
            }

            using (var path = Theme.RoundedRect(new RectangleF(0, 0, Width, Height), 10f))
            {
                var old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeThumbs();
                var old = Region;
                if (old != null) old.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
