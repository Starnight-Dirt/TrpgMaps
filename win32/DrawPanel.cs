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
        private ModernButton _rescanButton;
        private Label _statusLabel;
        private readonly Dictionary<string, string> _selectedFile = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Image> _thumbs = new Dictionary<string, Image>(StringComparer.Ordinal);
        private string _kind = DrawKind.Terrain;

        /// <summary>
        /// 展开着的文件夹（相对路径，忽略大小写）。折叠状态只活在本次运行里 ——
        /// 素材树千变万化，持久化反而会让"刚加的文件夹藏着看不见"。
        /// </summary>
        private readonly HashSet<string> _expandedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前列表里的一行：要么是文件夹，要么是一张图片。</summary>
        private sealed class AssetRow
        {
            public AssetFolder Folder;
            public TerrainAsset Asset;
            public int Depth;
            public bool IsFolder { get { return Folder != null; } }
        }

        private readonly List<AssetRow> _rows = new List<AssetRow>();

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

        /// <summary>
        /// 换界面语言后重建整块面板。
        ///
        /// 别的主面板是"关掉就丢"，重新打开时自然用的是新词；这个面板是**常驻控件**
        /// （`_drawPanel` 只建一次），不重建的话切到英文后它还是满屏中文。
        /// 分类 / 选中素材 / 笔刷状态这些字段都保留，只有控件重来一遍。
        /// </summary>
        public void ReloadLanguage()
        {
            var old = _scroll;
            Controls.Clear();          // 只摘不销毁
            if (old != null) old.Dispose();
            _shapeButtons.Clear();

            BuildContent();            // 重新取词、重新挂事件、重新扫素材树
            UpdateBrushPreview();      // 画布上那圈蓝选区也要跟着画回来
            AppLog.Write("绘图面板：已按当前语言重建");
        }

        /// <summary>自检用：当前选中的素材（没有则 null）。</summary>
        public TerrainAsset SelectedAsset
        {
            get { return CurrentAsset(); }
        }

        /// <summary>一行状态文本，写进 --ui / 日志用。</summary>
        public string StateText()
        {
            var asset = CurrentAsset();
            return "分类=" + DrawKind.Label(_kind) +
                   " 素材=" + (asset == null ? "(未选)" : asset.Name) +
                   " 本素材格数=" + (asset == null ? 0 : _drawings.CountAsset(_kind, asset.Relative)) +
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
            AddRow(MakeLabel(Loc.T("draw.kind"), 9f, Theme.Text), 20, inner);

            var kindRow = MakeRow();
            _kindTerrain = MakeToggle(Loc.T("draw.terrain"), 84);
            _kindTerrain.Click += delegate { SetKind(DrawKind.Terrain); };
            kindRow.Controls.Add(_kindTerrain);

            _kindEntity = MakeToggle(Loc.T("draw.entity"), 84);
            _kindEntity.Margin = new Padding(6, 3, 0, 0);
            _kindEntity.Click += delegate { SetKind(DrawKind.Entity); };
            kindRow.Controls.Add(_kindEntity);

            _kindItem = MakeToggle(Loc.T("draw.item"), 84);
            _kindItem.Margin = new Padding(6, 3, 0, 0);
            _kindItem.Click += delegate { SetKind(DrawKind.Item); };
            kindRow.Controls.Add(_kindItem);

            _content.Controls.Add(kindRow);

            // ---- 素材树 ----
            //
            // 实体 / 物品目录里允许放子文件夹（角色按职业分、物品按类型分）。
            // 列表把树**打平**成一维行：文件夹行 30px（三角号 + 名字），图片行 50px（缩略图）。
            // 内容比列表宽时底部出现横向滚动条（与竖向同一条 4px 细线）。
            _assetList = new ModernList();
            _assetList.BackColor = Theme.Track;
            _assetList.ItemHeight = 50;
            _assetList.RoundedRows = true;
            _assetList.ItemDraw = DrawAssetItem;
            _assetList.RowHeight = delegate(object item)
            {
                var row = item as AssetRow;
                return row != null && row.IsFolder ? 30 : 50;
            };
            _assetList.CanSelectItem = delegate(object item)
            {
                var row = item as AssetRow;
                return row != null && !row.IsFolder;
            };
            _assetList.ItemClicked += delegate(object item)
            {
                var row = item as AssetRow;
                if (row != null && row.IsFolder) ToggleFolder(row.Folder);
            };
            _assetList.ItemActivated += delegate { CommitAssetSelection(); };
            _assetList.SelectedIndexChanged += delegate { CommitAssetSelection(); };
            AddRow(_assetList, 208, inner);

            _assetHint = MakeLabel("", 8.5f, Theme.TextDim);
            AddRow(_assetHint, 34, inner);

            _rescanButton = MakeToggle(Loc.T("draw.rescan"), 84);
            _rescanButton.Click += delegate { RefreshAssets(); };
            AddRow(_rescanButton, 28, 84);

            AddSeparator(inner);

            // ---- 画笔模式 ----
            AddRow(MakeLabel(Loc.T("draw.brushmode"), 9f, Theme.Text), 20, inner);

            var modeRow = MakeRow();
            _modeManual = MakeToggle(Loc.T("draw.manual"), 128);
            _modeManual.Click += delegate { SetMode(BrushMode.Manual); };
            modeRow.Controls.Add(_modeManual);

            _modeBrush = MakeToggle(Loc.T("draw.brush"), 128);
            _modeBrush.Margin = new Padding(6, 3, 0, 0);
            _modeBrush.Click += delegate { SetMode(BrushMode.Brush); };
            modeRow.Controls.Add(_modeBrush);

            _content.Controls.Add(modeRow);

            // ---- 选区方式（严格 / 宽泛）----
            //
            // 刻意摆在「笔刷形状」**上面**：面板高度封顶 560px，内容一直是需要滚的，
            // 而这个开关是常用的（要在地图上边看边切），摆太靠下就得先滚一屏才够得着。
            // 放在这里，打开面板不用滚就能看见。
            AddRow(MakeLabel(Loc.T("draw.selection"), 8.5f, Theme.TextDim), 18, inner);

            var selectRow = MakeRow();
            _strictButton = MakeToggle(Loc.T("draw.strict"), 128);
            _strictButton.Click += delegate { SetSelectionMode(false); };
            selectRow.Controls.Add(_strictButton);

            _looseButton = MakeToggle(Loc.T("draw.loose"), 128);
            _looseButton.Margin = new Padding(6, 3, 0, 0);
            _looseButton.Click += delegate { SetSelectionMode(true); };
            selectRow.Controls.Add(_looseButton);

            _content.Controls.Add(selectRow);

            _selectHint = MakeLabel("", 8.5f, Theme.TextDim);
            AddRow(_selectHint, 30, inner);   // 英文两行

            AddSeparator(inner);

            // ---- 笔刷形状 ----
            var shapeLabel = MakeLabel(Loc.T("draw.shape"), 8.5f, Theme.TextDim);
            AddRow(shapeLabel, 18, inner);

            var shapeRow1 = MakeRow();
            AddShapeButton(shapeRow1, BrushShape.Line, Loc.T("draw.line"), 128, false);
            AddShapeButton(shapeRow1, BrushShape.Cone, Loc.T("draw.cone"), 128, true);
            _content.Controls.Add(shapeRow1);

            var shapeRow2 = MakeRow();
            AddShapeButton(shapeRow2, BrushShape.Cube, Loc.T("draw.cube"), 128, false);
            AddShapeButton(shapeRow2, BrushShape.Sphere, Loc.T("draw.sphere"), 128, true);
            _content.Controls.Add(shapeRow2);

            // 扇形张角只在"锥形"下出现
            _fanRow = MakeRow();
            _fanCombo = new ModernDropDown();
            _fanCombo.SetItems(new List<string> { Loc.T("draw.fan53"), Loc.T("draw.fan60"), Loc.T("draw.fan90"), Loc.T("draw.fan120") }, 2);
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
            AddRow(_modeHint, 62, inner);     // 英文四行

            AddSeparator(inner);

            // ---- 橡皮 / 清空 ----
            var eraseRow = MakeRow();
            _eraserButton = MakeToggle(Loc.T("draw.eraser"), 84);
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
            _clearAssetButton = MakeToggle(Loc.T("draw.clearasset"), 84);
            _clearAssetButton.Margin = new Padding(6, 3, 0, 0);
            _clearAssetButton.Click += delegate { ClearCurrentAsset(); };
            eraseRow.Controls.Add(_clearAssetButton);

            var clearAll = MakeToggle(Loc.T("draw.clearall"), 84);
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
                    ? Loc.T("draw.loose.hint")
                    : Loc.T("draw.strict.hint");
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
                    ? Loc.T("draw.hint.manual")
                    : Loc.T("draw.hint.brush");
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

            // 把目录树打平成列表行（只展开 _expandedFolders 里的）
            _rows.Clear();
            Flatten(_catalog.TreeOf(_kind), 0);

            _assetList.BeginUpdate();
            _assetList.Items.Clear();
            foreach (var row in _rows) _assetList.Items.Add(row);

            // 选中项尽量保持（按相对路径认）
            string previous;
            if (_selectedFile.TryGetValue(_kind, out previous))
            {
                for (var i = 0; i < _rows.Count; i++)
                {
                    if (_rows[i].Asset != null &&
                        string.Equals(_rows[i].Asset.Relative, previous, StringComparison.Ordinal))
                    {
                        _assetList.SelectSilently(i);
                        break;
                    }
                }
            }
            _assetList.EndUpdate();

            var wanted = 2;
            foreach (var row in _rows) wanted += row.IsFolder ? 30 : 50;
            if (wanted < 56) wanted = 56;
            if (wanted > 208) wanted = 208;
            _assetList.Height = wanted;

            // 横向滚动条：内容比列表宽时才出现（长文件夹名 / 深层缩进都会超）
            _assetList.ContentWidth = MeasureRows();
            AppLog.Write("素材树（" + _kind + "）：" + _rows.Count + " 行，内容宽 " +
                         _assetList.ContentWidth + "px，列表宽 " + _assetList.Width +
                         "px，横向滚动=" + (_assetList.HorizontalScrollbarVisible ? "有" : "无"));

            var total = _catalog.Of(_kind).Count;
            if (_assetHint != null)
            {
                _assetHint.Text = total == 0
                    ? Loc.T("draw.empty", DrawKind.Label(_kind), _kind)
                    : Loc.T("draw.count", _kind, total);
            }

            if (_scroll != null) _scroll.RefreshLayout();
        }

        /// <summary>把树按展开状态打平（文件夹在前、图片在后，和目录树一致）。</summary>
        private void Flatten(AssetFolder folder, int depth)
        {
            foreach (var sub in folder.Folders)
            {
                var row = new AssetRow();
                row.Folder = sub;
                row.Depth = depth;
                _rows.Add(row);

                if (_expandedFolders.Contains(sub.Relative)) Flatten(sub, depth + 1);
            }

            foreach (var asset in folder.Assets)
            {
                var row = new AssetRow();
                row.Asset = asset;
                row.Depth = depth;
                _rows.Add(row);
            }
        }

        /// <summary>点文件夹行：展开 / 折叠，并保持滚动位置大致不动。</summary>
        private void ToggleFolder(AssetFolder folder)
        {
            if (folder == null) return;

            if (_expandedFolders.Contains(folder.Relative)) _expandedFolders.Remove(folder.Relative);
            else _expandedFolders.Add(folder.Relative);

            var keepOffset = _assetList.ScrollOffset;
            RefreshAssets();
            _assetList.SetOffset(keepOffset);
            AppLog.Write("素材树：" + (folder.Relative) + (_expandedFolders.Contains(folder.Relative) ? " 展开" : " 折叠"));
        }

        /// <summary>
        /// 估算列表内容宽度（只用于判断要不要横向滚动条）。
        /// 用真实字体量，宁可略宽 —— 略宽只是多一条滚动条，量窄了文字会被硬切。
        /// </summary>
        private int MeasureRows()
        {
            var widest = 0;
            foreach (var row in _rows)
            {
                var indent = 10 + row.Depth * 14;
                var width = indent;

                if (row.IsFolder)
                {
                    width += 18;   // 三角号
                    width += TextRenderer.MeasureText(row.Folder.DisplayName,
                        Ui.Font(9f, FontStyle.Bold)).Width;
                    width += 10 + TextRenderer.MeasureText(
                        Loc.T("draw.folder.count", row.Folder.TotalCount), Ui.Font(7.5f)).Width;
                }
                else
                {
                    width += 36 + 9;   // 缩略图 + 间距
                    width += Math.Max(
                        TextRenderer.MeasureText(row.Asset.Name, Ui.Font(9f, FontStyle.Bold)).Width,
                        TextRenderer.MeasureText(row.Asset.TraitText(), Ui.Font(7.5f)).Width);
                }

                width += 16;
                if (width > widest) widest = width;
            }
            return widest;
        }

        private void CommitAssetSelection()
        {
            // ⚠️ 列表里的项是 AssetRow（文件夹行 / 图片行），**不是** TerrainAsset。
            // 这里曾经写成 `as TerrainAsset`，改多级菜单时跟着改漏了 —— 结果永远拿到 null，
            // `_selectedFile` 一次都没被写过 ⇒ 面板上"看起来选中了"，实际上没素材，
            // 手动和笔刷都画不出东西（v1.0.3 的回归）。
            var row = _assetList.SelectedItem as AssetRow;
            var asset = row == null ? null : row.Asset;
            if (asset == null) return;
            _selectedFile[_kind] = asset.Relative;
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
                lines.Add(Loc.T("draw.erasing", DrawKind.Label(_kind)));
            }
            else if (asset == null)
            {
                lines.Add(Loc.T("draw.noasset"));
            }
            else
            {
                lines.Add(Loc.T("draw.current", asset.Name, asset.TraitText()));
                if (!string.IsNullOrEmpty(asset.Note)) lines.Add(asset.Note);

                // 「清空本类」只摘这一张 —— 把"这一张到底占了多少格"摆出来，
                // 免得点和"清空全部"之间还要靠猜。
                var mine = _drawings.CountAsset(_kind, asset.Relative);
                lines.Add(Loc.T("draw.painted", mine));
            }

            if (_hasOrigin)
            {
                var where = _origin.IsCellCenter ? Loc.T("draw.origin.center") : Loc.T("draw.origin.node");
                var text = Loc.T("draw.origin", where, _origin.HX, _origin.HY);
                if (_radiusFixed) text += "  " + Loc.T("draw.radius.fixed",
                    DrawGeometry.DescribeDistance(_radiusHalf * DrawGeometry.FeetPerHalfCell));
                else text += "  " + Loc.T("draw.radius.moving");
                lines.Add(text);
            }

            lines.Add(Loc.T("draw.total", _drawings.Count));

            _statusLabel.Text = string.Join("\n", lines.ToArray());
        }

        // ============================================================
        //  素材列表绘制
        // ============================================================

        private void DrawAssetItem(ListItemDrawArgs args)
        {
            var row = args.Item as AssetRow;
            if (row == null) return;

            if (row.IsFolder)
            {
                DrawFolderRow(args, row);
                return;
            }

            var asset = row.Asset;
            if (asset == null) return;

            var box = args.Bounds;
            var selected = args.Selected;
            var background = selected ? Theme.ButtonHover : (args.Hover ? Theme.Card : Theme.Track);
            Ui.FillRounded(args.Graphics, box, 6f, background);

            var indent = row.Depth * 14;
            var slot = new Rectangle(box.X + 7 + indent, box.Y + 7,
                Math.Max(1, box.Height - 14), Math.Max(1, box.Height - 14));
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
                Math.Max(0, box.Width - (slot.Right - box.X) - 18), box.Height - 4);

            using (var format = new StringFormat())
            {
                format.LineAlignment = StringAlignment.Center;
                // 横向滚动条已经能看全了，这里就**不要省略号** —— 否则滚到右边也只看到"…"。
                format.Trimming = StringTrimming.None;
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

        /// <summary>
        /// 文件夹行：缩进 + 三角号（▶ 折叠 / ▼ 展开）+ 显示名 + 图片总数。
        /// 三角号就是"缩略图的位置"—— 文件夹没有缩略图，放个可点的箭头最直观。
        /// </summary>
        private void DrawFolderRow(ListItemDrawArgs args, AssetRow row)
        {
            var box = args.Bounds;
            var folder = row.Folder;
            if (folder == null) return;

            var background = args.Hover ? Theme.Card : Theme.Track;
            Ui.FillRounded(args.Graphics, box, 5f, background);

            var x = box.X + 10 + row.Depth * 14;
            var cy = box.Y + box.Height / 2;
            var expanded = _expandedFolders.Contains(folder.Relative);

            Ui.PrepareShape(args.Graphics);
            using (var path = new GraphicsPath())
            {
                if (expanded)
                {
                    path.AddLine(x, cy - 4, x + 9, cy - 4);
                    path.AddLine(x + 9, cy - 4, x + 4.5f, cy + 3);
                }
                else
                {
                    path.AddLine(x, cy - 5, x + 8, cy);
                    path.AddLine(x + 8, cy, x, cy + 5);
                }
                path.CloseFigure();
                using (var brush = new SolidBrush(args.Hover ? Theme.Text : Theme.Link))
                {
                    args.Graphics.FillPath(brush, path);
                }
            }

            Ui.PrepareText(args.Graphics);
            using (var format = new StringFormat())
            {
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.None;
                format.FormatFlags = StringFormatFlags.NoWrap;

                var nameRect = new Rectangle(x + 16, box.Y, Math.Max(0, box.Width - (x - box.X) - 22), box.Height);
                using (var brush = new SolidBrush(Theme.Text))
                {
                    args.Graphics.DrawString(folder.DisplayName, Ui.Font(9f, FontStyle.Bold),
                        brush, nameRect, format);
                }
            }

            // 计数紧跟在名字后面（不是贴右边 —— 横向滚动时它也应该跟着走）
            var width = TextRenderer.MeasureText(folder.DisplayName, Ui.Font(9f, FontStyle.Bold)).Width;
            var countRect = new Rectangle(x + 22 + width, box.Y, Math.Max(0, box.Width - (x - box.X) - width - 28), box.Height);
            using (var format = new StringFormat())
            {
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.None;
                format.FormatFlags = StringFormatFlags.NoWrap;
                using (var brush = new SolidBrush(Theme.TextDim))
                {
                    args.Graphics.DrawString(Loc.T("draw.folder.count", folder.TotalCount),
                        Ui.Font(7.5f), brush, countRect, format);
                }
            }
        }

        /// <summary>
        /// 素材列表里那一格小预览图（最长边 36px）。
        ///
        /// ⚠️ **必须走 <see cref="ImageLoader"/>。** 这里以前是自己写的一份
        /// <c>Image.FromStream</c>，GDI+ 不认 webp —— 素材换成 webp 之后
        /// 每张都抛异常、缩略图变 null，整个素材列表退化成一块深色背景
        /// （"地图上画得出来，预览里全是黑的"）。底图缩略图那次改对了，这一处漏了。
        /// 现在和 <c>MainForm.GetThumbnail</c> 共用同一个方法，不会再各漏一处。
        ///
        /// 失败时缓存 null，不再重试 —— 损坏的文件不该每帧都去解一遍。
        /// </summary>
        private Image GetThumb(TerrainAsset asset)
        {
            Image cached;
            if (_thumbs.TryGetValue(asset.Relative, out cached)) return cached;

            Image thumb = null;
            try
            {
                thumb = ImageLoader.LoadThumbnail(asset.Path, ThumbBox, Theme.Background);
            }
            catch (Exception ex)
            {
                AppLog.Write("生成素材缩略图失败：" + asset.Path, ex);
            }

            _thumbs[asset.Relative] = thumb;
            return thumb;
        }

        /// <summary>素材缩略图的边长上限（px）。</summary>
        private const int ThumbBox = 36;

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

            var removed = _drawings.ClearAsset(_kind, asset.Relative);
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
                : _drawings.Paint(_kind, cells, asset.Relative);

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
                : _drawings.Paint(_kind, cells, asset.Relative);

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

            // 列表里第一行可能是文件夹（实体 / 物品会分目录），要找到第一张**图片**行
            if (_assetList != null)
            {
                for (var i = 0; i < _rows.Count; i++)
                {
                    if (_rows[i].Asset == null) continue;
                    _assetList.SelectedIndex = i;
                    CommitAssetSelection();
                    break;
                }
            }
            SetMode(BrushMode.Manual);
            RefreshStatus();
        }

        /// <summary>截图用：把整棵树都展开（平时用户是点一个开一个）。</summary>
        public void ExpandAllForShot()
        {
            ExpandAll(_catalog.TreeOf(_kind));
            RefreshAssets();
        }

        private void ExpandAll(AssetFolder folder)
        {
            foreach (var sub in folder.Folders)
            {
                _expandedFolders.Add(sub.Relative);
                ExpandAll(sub);
            }
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
            Ui.DrawText(g, Loc.T("draw.title"), Ui.Font(11f, FontStyle.Bold), Color.White,
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
