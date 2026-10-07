using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace TrpgMaps
{
    /// <summary>支持双缓冲的 Panel，避免面板内容在动画时闪烁。</summary>
    internal sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }
    }

    /// <summary>
    /// DM 主窗口。沿用原版的视觉与交互：
    ///  - 启动即全屏，不选中底图但**始终显示网格**（默认 2.5cm/格 ≈ 1 英寸，
    ///    也就是标准 D&D 战场方格；网格大小与底图缩放无关，见 MapCanvas）；
    ///  - 左侧工具栏默认 60px 只有图标，点「展开」变 180px 并淡入文字；
    ///  - 「地图设置」「网络」是从左侧滑入的浮空面板，打开时地图上盖一层灰色半透明遮罩，
    ///    点遮罩空白处 = 关闭面板 + 收回侧栏；
    ///  - 面板与侧栏之间只留一条很小的缝隙。
    ///
    /// 文字里刻意**不用 emoji**：GDI(TextRenderer) 的字体回退在 Windows 7 上
    /// 找得到 emoji 字体就会渲染成"豆腐块"（空方框），侧栏图标一律用 PNG 贴图。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private const int PanelGap = 2;
        private const int PanelInset = 16;
        private const int WindowedWidth = 1200;
        private const int WindowedHeight = 800;

        /// <summary>网格尺度可调范围（cm/格）。</summary>
        private const double GridCmMin = 0.5;
        private const double GridCmMax = 10.0;

        private static string GridLabelText(double cm)
        {
            return "网格尺度 (cm/格) " + cm.ToString("0.0");
        }

        private enum PanelKind { None, Map, Network, About }

        private readonly ServerSettings _settings;
        private readonly MapStore _maps;
        private readonly GameState _game;
        private readonly RealtimeHub _hub;
        private readonly HttpServer _server;
        private readonly Routes _routes;

        private MapCanvas _canvas;
        private SidebarPanel _sidebar;
        private BufferedPanel _panelHost;
        private ScrollViewport _panelScroll;
        private FlowLayoutPanel _panelContent;

        private bool _fullscreen = true;
        private bool _sidebarExpanded;
        private double _sidebarWidth = SidebarPanel.CollapsedWidth;
        private double _panelSlide;
        private double _maskOpacity;
        private PanelKind _panelOpen = PanelKind.None;
        private int _panelWidth = 360;
        private int _contentWidth = 320;
        private bool _busy;

        /// <summary>
        /// 当前网格尺度（cm/格）。
        /// 必须存在窗口上，**不能只存在滑块控件上** —— 浮空面板每次关闭时
        /// 都会把整棵内容树销毁（ClearPanelReferences），重开时是新建的 TrackBar，
        /// 如果新滑块初始值写死成 2.0，就会出现"重开面板后滑块跳回 2.0
        /// 但实际网格没变"的对不上现象。
        /// </summary>
        private double _cmPerCell = 2.5;

        /// <summary>无人值守模式（--ui 截图自检）：不弹任何对话框，免得自动化流程被卡住。</summary>
        public bool Headless;

        /// <summary>
        /// 自检模式专用：不起内置 Web 服务器。
        /// 起服务器要监听局域网地址，会触发 Windows 防火墙的"是否允许通信"弹窗，
        /// 无人值守时没人点，会一直挂在屏幕上。纯界面自检不需要服务器。
        /// </summary>
        public bool NoServer;

        /// <summary>
        /// 不弹任何提示框（--no-prompts）。自动化测试用：防火墙被挡住时只写日志，
        /// 否则那个模态框会把无人值守的 smoke / auth 测试卡住。
        /// </summary>
        public bool NoPrompts;

        /// <summary>窗口化时的高度覆盖（--height，0 = 用默认 800）。用来验证小屏布局。</summary>
        public int WindowedHeightOverride;

        private readonly Timer _animTimer = new Timer();
        private readonly Timer _addressTimer = new Timer();
        private readonly Timer _playerTimer = new Timer();

        /// <summary>
        /// 网格锚点的防抖广播。画布尺寸一变（全屏↔窗口、拖边框），底图就重新居中，
        /// 网格相对底图的相位跟着变，手机端必须拿到新锚点才不会错位；
        /// 但拖边框时 OnResize 会连发几十次，所以攒一个定时器等停手再播。
        /// </summary>
        private readonly Timer _anchorTimer = new Timer();
        private string _lastAnchorSignature;

        // 底图状态同步（见 SyncCanvasWithStore）：画布上"当前是哪一套状态"，
        // 以及程序化改控件时用来绕开 SelectedIndexChanged 的开关。
        private readonly Timer _mapSyncTimer = new Timer();
        private string _canvasMapName;
        private string _canvasFitMode;
        private int _canvasRotation = -1;
        private bool _syncingFromStore;

        // 侧栏宽度动画
        private bool _sidebarAnimating;
        private double _sidebarFrom;
        private double _sidebarTo;
        private int _sidebarAnimStart;
        private int _sidebarAnimDuration = 260;

        // 面板滑入 / 遮罩淡入动画
        private bool _fadeAnimating;
        private double _maskFrom;
        private double _maskTo;
        private double _slideFrom;
        private double _slideTo;
        private int _fadeAnimStart;
        private int _fadeAnimDuration = 200;

        // 地图面板控件
        private ModernSlider _gridTrack;
        private Label _gridLabel;
        private ModernDropDown _fitCombo;
        private ModernDropDown _gridColorCombo;
        private Label _gridColorHint;
        private Label _rotateLabel;
        private Label _uploadStatus;
        private ModernList _mapList;
        private readonly Dictionary<string, Image> _thumbCache = new Dictionary<string, Image>();

        // 网络面板控件
        private ModernDropDown _ipCombo;
        private ModernTextBox _portBox;
        private Button _saveButton;
        private Label _netStatus;
        private Label _fwLabel;
        private Button _fwButton;
        private FirewallReport _firewall;
        private PictureBox _qrBox;
        private Label _urlLabel;
        private ModernList _playerList;
        private Button _roomLockButton;
        private List<LocalAddress> _addresses = new List<LocalAddress>();
        private string _playersSignature;
        private string _qrCacheKey;

        // ---- 绘图 ----
        /// <summary>绘图数据（地形 / 实体 / 物品 + 落盘）。</summary>
        private readonly DrawingStore _drawings;
        /// <summary>绘图素材（terrain\ 、entity\ 、item\ 三个目录）。</summary>
        private readonly TerrainCatalog _catalog = new TerrainCatalog();
        /// <summary>素材的"一格大小"贴图缓存，绘图与 HTTP 瓦片接口共用。</summary>
        private readonly TerrainImageCache _drawCache;

        private DrawPanel _drawPanel;
        /// <summary>改动后延时落盘 + 通知玩家端，避免拖动时每格都写一次盘。</summary>
        private readonly Timer _drawSaveTimer = new Timer();
        private bool _drawPanelOpen;

        // ---- 关于 / 署名 / 更新 ----
        /// <summary>关于面板里的头像（圆形裁切）。</summary>
        private PictureBox _avatarBox;
        /// <summary>关于面板里那行更新状态文字。</summary>
        private Label _updateStatus;
        /// <summary>更新检查进行中，防止重复点。</summary>
        private bool _updateChecking;
        /// <summary>启动后自动检查更新的单次定时器。</summary>
        private Timer _updateCheckTimer;
        /// <summary>已下载到本地的升级包路径（下载完就等用户点"更新"）。</summary>
        private string _pendingUpdateZip;
        private UpdateManifest _pendingUpdateManifest;

        /// <summary>在线玩家列表的一行。</summary>
        private sealed class PlayerRow
        {
            public string Name;
            public string Ip;
            public bool Online;

            public override string ToString()
            {
                return Name;
            }
        }

        public MainForm(bool startWindowed)
            : this(startWindowed, 0)
        {
        }

        /// <summary>
        /// windowHeight &gt; 0 时覆盖窗口化高度（默认 800），用来验证小屏上细滑块的样子。
        /// 必须在构造期就传进来：窗口尺寸是构造函数里 ApplyFullscreen() 定的，
        /// 等外面 new 完再赋值就晚了（Bounds 已经设过，不会再重算）。
        /// </summary>
        public MainForm(bool startWindowed, int windowHeight)
        {
            WindowedHeightOverride = windowHeight;
            _settings = new ServerSettings();
            _maps = new MapStore();
            _game = new GameState(_settings);
            _hub = new RealtimeHub();
            _server = new HttpServer(HandleHttp);
            _drawings = new DrawingStore(DrawingStore.DefaultPath());
            _drawCache = new TerrainImageCache(_catalog);
            _routes = new Routes(_settings, _maps, _game, _hub);
            _routes.AttachServer(_server);
            _routes.AttachDrawing(_drawings, _catalog, _drawCache);
            // 「网格 ↔ 底图」的几何只有画布知道，注入一个取快照的委托给它
            // （手机端靠这几个数把绘图摆到底图上，见 GridAnchor）。
            _routes.AttachGridAnchor(delegate { return _canvas == null ? null : _canvas.AnchorSnapshot; });

            DoubleBuffered = true;
            KeyPreview = true;
            Text = "TrpgMaps";
            ApplyAppIcon();
            BackColor = Theme.Background;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            // 高度下限刻意放到 480：1366x768 那种老笔记本、甚至 1280x720 的网本
            // 都能开到合身的大小。内容放不下也没关系——面板自己会纵向滚动。
            MinimumSize = new Size(800, 480);

            BuildUi();
            ApplyFullscreen(!startWindowed);

            _animTimer.Interval = 15;
            _animTimer.Tick += AnimTick;

            _addressTimer.Interval = 5000;
            _addressTimer.Tick += delegate { RefreshAddresses(); };

            _playerTimer.Interval = 3000;
            _playerTimer.Tick += delegate { RefreshPlayers(); };

            // 网格锚点变了才播（见 ScheduleAnchorBroadcast）；600ms 足够滤掉拖边框的连发
            _anchorTimer.Interval = 600;
            _anchorTimer.Tick += delegate { _anchorTimer.Stop(); BroadcastAnchorIfChanged(); };

            // 每 200ms 对一次底图状态：HTTP 改的 MapStore 碰不到控件，这里兜住
            // （见 SyncCanvasWithStore）。200ms 对一次纯内存比较，代价可以忽略。
            _mapSyncTimer.Interval = 200;
            _mapSyncTimer.Tick += delegate { SyncCanvasWithStore(); };
            _mapSyncTimer.Start();
        }

        // ============================================================
        //  构建界面
        // ============================================================

        /// <summary>
        /// 给窗口装上项目根目录那张「图标.png」（构建期已转成 Resources\appicon.ico）。
        ///
        /// 逐级降级，任何一步失败都不会影响启动：
        ///  1. exe 同级 Resources\appicon.ico 的 256 那一张 —— 高 DPI / Alt+Tab 不糊；
        ///  2. 同一个 .ico 里的 48 那一张 —— 256 是 PNG 压缩的条目，
        ///     老版本 GDI+ 解不开时会抛，退一档基本总能成；
        ///  3. Icon.ExtractAssociatedIcon(exe) —— PE 资源段里由 ApplicationIcon
        ///     编进去的那张（通常 32x32）；
        ///  4. 全失败就保持系统默认图标。
        /// </summary>
        private void ApplyAppIcon()
        {
            // 注意：Path.Combine 的三参重载是 .NET 4.0 才有的，本工程目标 v3.5，必须层层套
            var ico = Path.Combine(Path.Combine(AppEnv.Root, "Resources"), "appicon.ico");
            if (File.Exists(ico))
            {
                foreach (var px in new[] { 256, 48, 32 })
                {
                    try
                    {
                        this.Icon = new System.Drawing.Icon(ico, new Size(px, px));
                        AppLog.Write("窗口图标取用 " + ico + " 的 " + px + "x" + px + " 条目");
                        return;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("读取 appicon.ico 的 " + px + " 条目失败，继续降级", ex);
                    }
                }
            }
            else
            {
                AppLog.Write("未找到 " + ico + "，改用 exe 内嵌图标");
            }

            try
            {
                var embedded = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (embedded != null) this.Icon = embedded;
            }
            catch (Exception ex)
            {
                AppLog.Write("取 exe 内嵌图标也失败，保持系统默认图标", ex);
            }
        }

        private void BuildUi()
        {
            _canvas = new MapCanvas();
            _canvas.BlankClicked += OnCanvasClicked;

            var iconDir = Path.Combine(AppEnv.WebRoot, "images");
            // 最后一项（"关于"）会被 SidebarPanel 贴到侧栏底部，不参与从顶往下的排布。
            // 顺序必须和 OnSidebarItemClicked 里的 case 号一一对应。
            _sidebar = new SidebarPanel(
                new[] { "展开", "地图设置", "网络", "绘图", "全屏", "关于" },
                new[]
                {
                    Path.Combine(iconDir, "expand.png"),
                    Path.Combine(iconDir, "map.png"),
                    Path.Combine(iconDir, "network.png"),
                    Path.Combine(iconDir, "draw.png"),
                    Path.Combine(iconDir, "fullscreen.png"),
                    Path.Combine(iconDir, "about.png")
                });
            _sidebar.ItemClicked += OnSidebarItemClicked;

            // 面板滚动容器：自绘的细滑块，**结构上不可能出现横向滚动条**
            // （AutoScroll 关掉，超宽内容直接裁掉），详细说明见 Widgets.cs。
            _panelScroll = new ScrollViewport();
            _panelScroll.BackColor = Theme.Panel;

            _panelHost = new BufferedPanel();
            _panelHost.BackColor = Theme.Panel;
            _panelHost.Visible = false;
            _panelHost.Controls.Add(_panelScroll);

            Controls.Add(_canvas);
            Controls.Add(_panelHost);
            Controls.Add(_sidebar);

            // z 序必须显式排定：WinForms 里后加进来的控件会落在 z 序更靠后的位置，
            // 而 MapCanvas 是铺满整窗的，如果它压在侧栏/面板上面，整个界面就只剩网格
            // （用户实际遇到的现象）。这里说清楚谁在哪一层：
            //   最底：底图 + 网格 + 遮罩（MapCanvas）
            //   中间：浮空面板（这样它可以从侧栏背后滑出来）
            //   最上：侧栏
            _canvas.SendToBack();
            _sidebar.BringToFront();

            _canvas.SetGridSize(_cmPerCell);
            _canvas.SetDrawings(_drawings, _drawCache, _catalog);
            _canvas.DrawMouseDown += OnDrawMouseDown;
            _canvas.DrawMouseMove += OnDrawMouseMove;
            _canvas.DrawMouseUp += OnDrawMouseUp;
            _canvas.DrawMouseLeave += OnDrawMouseLeave;

            // 改完延时落盘：拖动时每格都存盘会把磁盘刷爆，等手停下来再写。
            _drawSaveTimer.Interval = 900;
            _drawSaveTimer.Tick += delegate { FlushDrawings(true); };

            // 鼠标滚轮在 WinForms 里只会送给"获得焦点的那个控件"，光标悬停在
            // 标签/输入框上滚动是没反应的。这里统一挂一层转发：任何一个后代控件
            // 收到滚轮，都转给面板滚动容器（列表自己管自己的滚动，不转发）。
            MouseWheel += OnAnyMouseWheel;
            HookWheel(_panelScroll);
        }

        private void HookWheel(Control root)
        {
            root.MouseWheel += OnAnyMouseWheel;
            foreach (Control child in root.Controls) HookWheel(child);
        }

        private void OnAnyMouseWheel(object sender, MouseEventArgs e)
        {
            if (_panelScroll == null || !_panelScroll.ScrollbarVisible) return;
            if (sender == _panelScroll) return;    // 容器自己已经在 OnMouseWheel 里滚过了
            if (sender is ModernList) return;      // 列表滚自己的内容

            // 这里替面板滚了一次，得盖个戳：滚轮稍后还会冒泡到 ScrollViewport 自己身上，
            // 没有这个戳它就会再滚一次 —— 表现为"一格滚两下"。
            WheelRouter.Mark();
            _panelScroll.ScrollBy(-(e.Delta / 120) * ScrollViewport.WheelStep);
        }

        private bool HandleHttp(RequestContext ctx)
        {
            return _routes.Handle(ctx);
        }

        // ============================================================
        //  全屏 / 窗口化
        // ============================================================

        private void ApplyFullscreen(bool fullscreen)
        {
            _fullscreen = fullscreen;

            if (fullscreen)
            {
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                TopMost = true;
                Bounds = Screen.PrimaryScreen.Bounds;
            }
            else
            {
                TopMost = false;
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = FormWindowState.Normal;

                var area = Screen.PrimaryScreen.WorkingArea;
                var width = Math.Min(WindowedWidth, area.Width);
                var wanted = WindowedHeightOverride > 0 ? WindowedHeightOverride : WindowedHeight;
                var height = Math.Min(wanted, area.Height);
                Bounds = new Rectangle(
                    area.X + (area.Width - width) / 2,
                    area.Y + (area.Height - height) / 2,
                    width, height);
            }

            LayoutChildren();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // 先查防火墙，再起服务。顺序不能反：
            // 防火墙没放行时，用户需要"一键修复"，而修复要在局域网真正可用之前做完；
            // 而且系统自己的授权框需要我们把置顶让开才看得见（见 LetSystemPromptThrough）。
            CheckFirewall();

            // 自检模式刻意不起服务器：监听局域网地址会弹 Windows 防火墙授权框，
            // 无人值守时那个框没人点，会一直挂在桌面上挡住后续流程。
            if (!NoServer)
            {
                try
                {
                    _server.Start(_settings.Port);
                    AppLog.Write("Web 服务器已启动：" + _settings.PlayerUrl);
                }
                catch (Exception ex)
                {
                    AppLog.Write("启动 Web 服务器失败（端口 " + _settings.Port + "）", ex);
                    if (!Headless)
                    {
                        MessageBox.Show(this, "内置服务器启动失败，端口可能被占用：\r\n" + ex.Message,
                            "TrpgMaps", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            else
            {
                AppLog.Write("自检模式：跳过启动 Web 服务器（--no-server）");
            }

            LayoutChildren();
            _canvas.Invalidate();

            // 界面起来之后再排一次"检查更新"。放在最后是因为它要联网，
            // 而前面起服务、查防火墙都是本机操作，先做完这些用户能更快看到画面。
            StartStartupUpdateCheck();
        }

        // ============================================================
        //  防火墙（局域网能不能连进来）
        // ============================================================

        /// <summary>
        /// 启动时查一次本程序在 Windows 防火墙里的入站状态。
        ///
        /// 这是个真实踩过的坑：本窗口全屏 + 置顶，而系统的"是否允许通信"授权框是普通窗口，
        /// 会被压在底下。用户看不到、随手关掉之后，Windows 会写一条**永久的入站阻止规则**，
        /// 并且**再也不问**。表现就是 DM 端一切正常、二维码也对，但手机永远打不开 ——
        /// 而同一台机器上别的程序（比如 MAUI 版，当初点了"允许"）完全正常，
        /// 于是极易被误判成"网络问题 / 手机问题"。所以这里主动查、主动修。
        /// </summary>
        private void CheckFirewall()
        {
            _firewall = FirewallCheck.Inspect();
            AppLog.Write("防火墙：" + FirewallCheck.Describe(_firewall));

            // 截图 / 自检 / 自动化测试一律不弹任何东西：
            // 一个模态框就能把无人值守的流程永远卡在那里。
            if (Headless || NoPrompts)
            {
                if (_firewall.State != FirewallState.Allowed)
                {
                    AppLog.Write("防火墙未放行，但当前是静默模式，不弹提示；"
                        + "可用 --fix-firewall 或界面上的\"放行防火墙\"按钮处理。");
                }
                return;
            }

            if (_firewall.State == FirewallState.NoRule)
            {
                // 还没有任何规则：等一下绑定端口时系统会弹授权框，把置顶让开让它露出来。
                LetSystemPromptThrough();
                return;
            }

            if (_firewall.State != FirewallState.Blocked) return;

            // 已经是"阻止"了：系统不会再提示，只能主动改，否则手机永远连不上。
            var answer = MessageBox.Show(this,
                "Windows 防火墙里有一条『阻止』本程序接收局域网连接的规则。\r\n"
                + "手机 / 平板扫码也打不开玩家页面，就是它造成的。\r\n\r\n"
                + "是否现在放行？（会弹一次管理员确认）",
                "TrpgMaps - 局域网被防火墙挡住",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (answer != DialogResult.Yes) return;
            if (ApplyFirewallFix())
            {
                MessageBox.Show(this,
                    "已放行。现在用手机连同一个 WiFi，扫码或输入上面的玩家地址即可打开。",
                    "TrpgMaps", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>
        /// 临时取消置顶若干秒。
        ///
        /// 系统那个"是否允许通信"授权框不是本窗口的子窗口，所以哪怕本窗口是全屏置顶的，
        /// 它也会被压在最底下 —— 用户根本看不见、更点不到"允许"。
        /// 首次监听端口的那几秒把置顶让开，框才有机会露出来。几秒后自动恢复。
        /// </summary>
        private void LetSystemPromptThrough()
        {
            if (Headless || !TopMost) return;          // 窗口化模式本来就不置顶，不用管

            AppLog.Write("防火墙：尚无规则，临时取消置顶 8 秒，让系统授权框露出来");
            TopMost = false;

            var timer = new Timer();
            timer.Interval = 8000;
            timer.Tick += delegate
            {
                timer.Stop();
                timer.Dispose();
                if (_fullscreen) TopMost = true;       // 期间用户可能切了模式，只在仍全屏时恢复
            };
            timer.Start();
        }

        /// <summary>弹 UAC 跑一次 --fix-firewall，成功返回 true。</summary>
        private bool ApplyFirewallFix()
        {
            var previous = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                string message;
                if (!FirewallCheck.RunElevatedRepair(out message))
                {
                    AppLog.Write("防火墙放行失败：" + message);
                    SetNetStatus("防火墙放行失败：" + message, true);
                    RefreshFirewallStatus();
                    MessageBox.Show(this, message, "TrpgMaps - 防火墙",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                _firewall = FirewallCheck.Inspect();
                AppLog.Write("防火墙放行完成：" + FirewallCheck.Describe(_firewall));
                RefreshFirewallStatus();
                return _firewall.State == FirewallState.Allowed;
            }
            finally
            {
                Cursor = previous;
            }
        }

        private void FixFirewallFromUi()
        {
            _firewall = FirewallCheck.Inspect();
            RefreshFirewallStatus();

            if (_firewall.State == FirewallState.Allowed)
            {
                SetNetStatus("防火墙已放行，手机应该能正常访问", false);
                return;
            }

            if (ApplyFirewallFix()) SetNetStatus("防火墙已放行，手机现在可以访问了", false);
        }

        /// <summary>把当前防火墙状态写到网络面板的提示行上。</summary>
        private void RefreshFirewallStatus()
        {
            if (_fwLabel == null) return;
            if (_firewall == null) _firewall = FirewallCheck.Inspect();

            switch (_firewall.State)
            {
                case FirewallState.Allowed:
                    _fwLabel.Text = "防火墙：已放行";
                    _fwLabel.ForeColor = Theme.Success;
                    break;
                case FirewallState.Blocked:
                    _fwLabel.Text = "防火墙：被阻止，手机连不上";
                    _fwLabel.ForeColor = Theme.Error;
                    break;
                case FirewallState.NoRule:
                    _fwLabel.Text = "防火墙：未放行，首次连接会弹系统授权框";
                    _fwLabel.ForeColor = Theme.TextDim;
                    break;
                default:
                    _fwLabel.Text = "防火墙：状态未知（" + _firewall.Error + "）";
                    _fwLabel.ForeColor = Theme.TextDim;
                    break;
            }
        }

        // ============================================================
        //  无人值守界面自检（--ui <模式> <输出路径>）
        // ============================================================

        /// <summary>一次自检要截的一个画面。</summary>
        private sealed class ShotStep
        {
            public string Mode;
            public string Path;

            public ShotStep(string mode, string path)
            {
                Mode = mode;
                Path = path;
            }
        }

        /// <summary>
        /// 排定"摆好界面 → 渲染成 PNG → 退出"。给自动化测试用：
        /// 界面上有很多东西（侧栏在不在、面板贴不贴边、缝隙多大、格子到底多少像素）
        /// 是 HTTP 接口测不出来的，只能看图。
        ///
        /// mode 传 "all" 时 path 当作输出目录，在一个进程里把所有画面依次截完：
        /// 每次拉起 GUI 都可能触发安全软件的"是否允许运行"弹窗，
        /// 无人值守时能少启动一次就少一次。
        /// </summary>
        public void ScheduleUiShot(string mode, string path)
        {
            Shown += delegate
            {
                var steps = BuildShotSteps(mode, path);
                var index = 0;
                var countdown = 0;

                var timer = new Timer();
                timer.Interval = 150;
                timer.Tick += delegate
                {
                    if (index >= steps.Count)
                    {
                        timer.Stop();
                        timer.Dispose();
                        Close();
                        return;
                    }

                    if (countdown == 0)
                    {
                        try
                        {
                            ApplyShotMode(steps[index].Mode);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Write("截图步骤失败：" + steps[index].Mode, ex);
                        }
                        // 150ms × 8 ≈ 1.2s：等侧栏宽度动画、淡入动画和面板延迟加载都跑完
                        countdown = 8;
                        return;
                    }

                    countdown--;
                    if (countdown > 0) return;

                    var step = steps[index];
                    index++;

                    // 先把真实子窗口 z 序和关键几何记下来，再抓图。顺序很重要：
                    // z 序是"到底谁盖住谁"的唯一权威答案，几何是"缝隙到底几像素"的唯一答案。
                    LogChildZOrder();
                    LogLayout();

                    Activate();
                    BringToFront();
                    Update();
                    Application.DoEvents();

                    SaveUiShot(step.Path);
                };
                timer.Start();
            };
        }

        private static List<ShotStep> BuildShotSteps(string mode, string path)
        {
            var steps = new List<ShotStep>();

            if (string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(new ShotStep("base", Path.Combine(path, "01-base.png")));
                steps.Add(new ShotStep("sidebar", Path.Combine(path, "02-sidebar.png")));
                steps.Add(new ShotStep("map", Path.Combine(path, "03-map.png")));
                // 顺着往下：把滑块拖到 3.5 → 关掉面板 → 再打开，看滑块有没有记住
                steps.Add(new ShotStep("grid35", Path.Combine(path, "04-grid-35.png")));
                steps.Add(new ShotStep("closeall", Path.Combine(path, "05-closed.png")));
                steps.Add(new ShotStep("gridreopen", Path.Combine(path, "06-grid-reopen.png")));
                steps.Add(new ShotStep("net", Path.Combine(path, "07-net.png")));
                // 剩下三个早期漏掉了：rotate90 / gridcolor 是底图相关的画面，
                // draw / drawbrush 是右上角绘图浮窗 —— 都是一次进程就能拍完的，
                // 多一个步骤不会多一次启动（启动次数才是无人值守时最贵的）。
                steps.Add(new ShotStep("rotate90", Path.Combine(path, "08-rotate90.png")));
                steps.Add(new ShotStep("gridcolor", Path.Combine(path, "09-gridcolor.png")));
                steps.Add(new ShotStep("draw", Path.Combine(path, "10-draw.png")));
                steps.Add(new ShotStep("drawbrush", Path.Combine(path, "11-drawbrush.png")));
                // 笔刷"实际形状"（半透明蓝）两种典型形状各拍一张：
                // 球形看"真圆 vs 方框"、锥形看"扇形朝向"，而且都叠在真实底图上，
                // 能同时看出红覆盖格和蓝形状的差值（红多出来的角、蓝多出来的楔形）。
                steps.Add(new ShotStep("drawcone", Path.Combine(path, "12-drawcone.png")));
                // 选区方式切到「宽泛」再拍一张球形：和 11-drawbrush 是同一套原点/半径/形状，
                // 只有"形状怎么落到格上"不同，两张对着看就能确认红色确实把蓝圆压到的格都算上了。
                steps.Add(new ShotStep("drawbrushloose", Path.Combine(path, "13-drawbrush-loose.png")));
                // 半径 1.5 格（7.5 尺）+ 宽泛：圆正好和四条格线相切，最外面那一圈是"相切不算"的判据现场。
                // 它同样会把选区方式留在「宽泛」，所以和 drawbrushloose 一起排在最后。
                steps.Add(new ShotStep("drawtangent", Path.Combine(path, "14-draw-tangent.png")));
                // 关于面板放最后：它会先把绘图面板关掉，不影响前面的画面。
                steps.Add(new ShotStep("about", Path.Combine(path, "15-about.png")));
                return steps;
            }

            steps.Add(new ShotStep(mode, path));
            return steps;
        }

        /// <summary>自检用：确保"带底图"的画面里真的有一张底图。</summary>
        private void SelectFirstMapForShot()
        {
            var list = _maps.List();
            if (list == null || list.Count == 0)
            {
                AppLog.Write("自检：maps 目录里没有底图，跳过选图");
                return;
            }

            _maps.SetCurrent(list[0].Name, _maps.FitMode);
            ApplyMapToCanvas();
            AppLog.Write("自检：已选中底图 " + list[0].Name);
        }

        // ---- 子窗口 z 序诊断（Win32）----

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        private const uint GW_HWNDNEXT = 2;
        private const uint GW_CHILD = 5;

        /// <summary>
        /// 从最上层往下遍历本窗口的子窗口，把 canvas / sidebar / panelHost 的先后
        /// 关系打进日志。同时打出它们在 Controls 集合里的索引，用来确定
        /// "集合索引"与"屏幕 z 序"到底怎么对应 —— 光看代码是靠不住的。
        /// </summary>
        private void LogChildZOrder()
        {
            try
            {
                var order = new System.Text.StringBuilder();
                var rank = 0;
                for (var h = GetWindow(Handle, GW_CHILD); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
                {
                    string name;
                    if (h == _canvas.Handle) name = "canvas";
                    else if (h == _sidebar.Handle) name = "sidebar";
                    else if (h == _panelHost.Handle) name = "panelHost";
                    else
                    {
                        var cls = new System.Text.StringBuilder(256);
                        GetClassName(h, cls, cls.Capacity);
                        name = "?:" + cls;
                    }

                    if (rank > 0) order.Append("  >  ");
                    order.Append(rank++).Append(':').Append(name);
                }

                AppLog.Write("z序(0=最上层): " + order);
                AppLog.Write("Controls 索引: canvas=" + Controls.GetChildIndex(_canvas) +
                             "  sidebar=" + Controls.GetChildIndex(_sidebar) +
                             "  panelHost=" + Controls.GetChildIndex(_panelHost) +
                             "  Count=" + Controls.Count);
            }
            catch (Exception ex)
            {
                AppLog.Write("读取子窗口 z 序失败", ex);
            }
        }

        /// <summary>
        /// 把关键控件的实际几何打进日志。缝隙到底几像素、面板里有没有多出一条
        /// 横向滚动条，用尺子量图不如直接打印数字可靠。
        /// </summary>
        private void LogLayout()
        {
            try
            {
                AppLog.Write("ClientSize  =" + ClientSize.Width + "x" + ClientSize.Height);

                // 格子到底多少像素：这是"进度条数值 = 屏幕物理尺寸"的唯一可量化证据。
                // 重点看它有没有随底图缩放而变 —— 应该**始终**是 cm/格 × 37.8。
                AppLog.Write("网格        = " + _canvas.CmPerCell.ToString("0.0") + " cm/格 × " +
                             AppEnv.PixelsPerCm.ToString("0.0") + " px/cm = " +
                             _canvas.CellPixels.ToString("0.0") + " px/格" +
                             "  底图=" + (string.IsNullOrEmpty(_maps.CurrentMap) ? "(无)" : _maps.CurrentMap) +
                             "  模式=" + _maps.FitMode +
                             "  旋转=" + _maps.Rotation + "°（顺时针）");
                AppLog.Write("旋转标签    = \"" +
                             Preview(_rotateLabel == null ? "(地图面板未打开)" : _rotateLabel.Text) + "\"");
                AppLog.Write("网格颜色    = 模式=" + _maps.GridMode +
                             "  实际=" + _maps.ResolveGridColor() +
                             "  说明=\"" +
                             Preview(_gridColorHint == null ? "(地图面板未打开)" : _gridColorHint.Text) + "\"");

                LogDrawing();

                if (_gridTrack != null)
                {
                    AppLog.Write("滑块        = Value=" + _gridTrack.Value +
                                 "（" + (_gridTrack.Value / 10.0).ToString("0.0") + " cm）" +
                                 "  标签=\"" + Preview(_gridLabel == null ? "" : _gridLabel.Text) + "\"");
                }
                else
                {
                    AppLog.Write("滑块        = (地图面板未打开)");
                }

                AppLog.Write("sidebar     =" + Describe(_sidebar.Bounds) +
                             "  可见=" + _sidebar.Visible);
                AppLog.Write("panelHost   =" + Describe(_panelHost.Bounds) +
                             "  可见=" + _panelHost.Visible);

                if (_panelHost.Visible)
                {
                    AppLog.Write("面板左边缘 - 侧栏右边缘 = " + (_panelHost.Left - _sidebar.Right) + "px");
                }

                var bar = _panelScroll.ScrollbarVisible
                    ? ("显示（4px 细短线） 偏移=" + _panelScroll.ScrollOffset + "px")
                    : "不需要（内容装得下）";
                AppLog.Write("panelScroll =" + Describe(_panelScroll.Bounds) +
                             "  ClientSize=" + _panelScroll.ClientSize.Width + "x" + _panelScroll.ClientSize.Height +
                             "  横向滚动条=结构上永远没有" +
                             "  竖向溢出=" + _panelScroll.OverflowHeight + "px" +
                             "  竖向滚动条=" + bar);

                if (_panelContent != null)
                {
                    var preferred = _panelContent.PreferredSize;
                    AppLog.Write("panelContent=" + Describe(_panelContent.Bounds) +
                                 "  期望尺寸=" + preferred.Width + "x" + preferred.Height);

                    // 逐行打印，方便核对每一行的实际位置与高度是否和预期一致
                    foreach (Control row in _panelContent.Controls)
                    {
                        var inner = row is FlowLayoutPanel && row.Controls.Count > 0 ? row.Controls[0] : row;
                        AppLog.Write("  行 " + inner.GetType().Name.PadRight(18) +
                                     Describe(row.Bounds) + "  \"" + Preview(inner.Text) +
                                     "\" 内宽=" + inner.Width);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("读取布局失败", ex);
            }
        }

        private static string Preview(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 30 ? text : text.Substring(0, 30) + "…";
        }

        private static string Describe(Rectangle r)
        {
            return "(" + r.X + "," + r.Y + " " + r.Width + "x" + r.Height + ")";
        }

        private void ApplyShotMode(string mode)
        {
            switch (mode)
            {
                case "base":           // 无底图，只有网格（量"格子有多少像素"的基准）
                    ApplyShotSidebarWidth(false);
                    break;
                case "sidebar":        // 侧栏展开，不动面板（看按钮比例）
                    ApplyShotSidebarWidth(true);
                    break;
                case "map":            // 侧栏展开 + 地图面板 + 有底图
                    ApplyShotSidebarWidth(true);
                    SelectFirstMapForShot();
                    OpenPanel(PanelKind.Map);
                    break;
                case "mapcollapsed":   // 侧栏收起 + 地图面板（看两态缝隙是否一致）
                    SelectFirstMapForShot();
                    OpenPanel(PanelKind.Map);
                    break;
                case "grid35":         // 打开地图面板，把滑块拖到 3.5 cm/格
                    ApplyShotSidebarWidth(true);
                    SelectFirstMapForShot();
                    OpenPanel(PanelKind.Map);
                    if (_gridTrack != null) _gridTrack.Value = 35;
                    break;
                case "closeall":       // 全部关闭（模拟用户点空白处）
                    CloseAll();
                    break;
                case "gridreopen":     // 重开地图面板：滑块该停在 3.5，而不是跳回默认值
                    ApplyShotSidebarWidth(true);
                    OpenPanel(PanelKind.Map);
                    break;
                case "net":            // 侧栏展开 + 网络面板
                    ApplyShotSidebarWidth(true);
                    OpenPanel(PanelKind.Network);
                    break;
                case "about":          // 侧栏展开 + 关于面板（头像 / 版本 / 更新）
                    if (_drawPanelOpen) CloseDrawPanel();
                    ApplyShotSidebarWidth(true);
                    OpenPanel(PanelKind.About);
                    break;
                case "rotate90":       // 底图顺时针转 90°（看旋转有没有真的生效）
                    ApplyShotSidebarWidth(true);
                    SelectFirstMapForShot();
                    _maps.Rotate(90);
                    ApplyMapToCanvas();
                    OpenPanel(PanelKind.Map);
                    break;
                case "gridcolor":      // 选一张底图 + 网格颜色留「自动」（看判定结果与说明）
                    ApplyShotSidebarWidth(true);
                    _maps.SetGridMode("auto");
                    SelectFirstMapForShot();
                    OpenPanel(PanelKind.Map);
                    break;
                case "draw":           // 绘图面板（右上角浮窗）+ 选中第一个地形素材
                    CloseAll();        // 先把上一步留下的「地图设置」收掉，否则它会挡住左半边地图
                    ApplyShotSidebarWidth(true);
                    SelectFirstMapForShot();
                    OpenDrawPanel();
                    _drawPanel.SelectFirstAssetForShot();
                    break;
                case "drawbrush":      // 笔刷模式：放好原点、摆出一个选区，看半透明红与距离读数
                    CloseAll();
                    ApplyShotSidebarWidth(true);
                    SelectFirstMapForShot();
                    OpenDrawPanel();
                    _drawPanel.SelectFirstAssetForShot();
                    // 格号按实际画布/侧栏/浮窗位置反推（见 SetupShotBrushVisible）：
                    // 前一步可能刚把网格拉到 3.5cm/格，写死的格号会整块跑到画布外。
                    _drawPanel.SetupShotBrushVisible((int)_sidebarWidth, _drawPanel.Left, _canvas.Width);
                    break;
                case "drawcone":       // 同上，但换成扇形 —— 看蓝色实际形状是不是真的扇出去
                    CloseAll();
                    ApplyShotSidebarWidth(true);
                    SelectFirstMapForShot();
                    OpenDrawPanel();
                    _drawPanel.SelectFirstAssetForShot();
                    _drawPanel.SetupShotBrushVisible((int)_sidebarWidth, _drawPanel.Left, _canvas.Width,
                        BrushShape.Cone);
                    break;
                case "drawbrushloose": // 球形 + 宽泛选区：和 11-drawbrush 同参数，只有选区方式不同
                    CloseAll();
                    ApplyShotSidebarWidth(true);
                    SelectFirstMapForShot();
                    OpenDrawPanel();
                    _drawPanel.SelectFirstAssetForShot();
                    _drawPanel.SetSelectionMode(true);
                    _drawPanel.SetupShotBrushVisible((int)_sidebarWidth, _drawPanel.Left, _canvas.Width);
                    break;
                case "drawtangent":    // 半径**1.5 格**（7.5 尺）+ 宽泛：圆正好与格线相切
                    // 拍的就是"相切的那一格到底算不算"：半径 3 半格时，正上下左右第 2 格
                    // 只和圆相切、一格面积都没盖到，不该被选中（严格/宽泛都应该是 3×3 = 9 格）。
                    CloseAll();
                    ApplyShotSidebarWidth(true);
                    SelectFirstMapForShot();
                    OpenDrawPanel();
                    _drawPanel.SelectFirstAssetForShot();
                    _drawPanel.SetSelectionMode(true);
                    _drawPanel.SetupShotBrushVisible((int)_sidebarWidth, _drawPanel.Left, _canvas.Width,
                        BrushShape.Sphere, 3);
                    break;
            }
        }

        /// <summary>跳过动画，直接把侧栏摆到目标宽度（截图用）。</summary>
        private void ApplyShotSidebarWidth(bool expanded)
        {
            _sidebarExpanded = expanded;
            _sidebarWidth = expanded ? SidebarPanel.ExpandedWidth : SidebarPanel.CollapsedWidth;
            _sidebar.TextOpacity = expanded ? 1.0 : 0.0;
            LayoutChildren();
        }

        /// <summary>
        /// 屏幕实拍：直接把窗口占据的那块屏幕像素拷下来，所见即所得。
        ///
        /// 这里刻意**不用** DrawToBitmap：它走的是 WM_PRINT，对自定义绘制的子控件
        /// 并不按屏幕 z 序合成（实测它会把铺满整窗的 MapCanvas 画在侧栏之上），
        /// 拿它判断"谁盖住谁 / 侧栏在不在"会得出完全相反的结论。
        /// </summary>
        private void SaveUiShot(string path)
        {
            try
            {
                var bounds = Bounds;
                using (var bitmap = new Bitmap(bounds.Width, bounds.Height))
                {
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                    }
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                AppLog.Write("界面截图（屏幕实拍）已保存：" + path +
                             "（" + bounds.Width + "x" + bounds.Height + "）");
            }
            catch (Exception ex)
            {
                AppLog.Write("屏幕截图失败：" + path, ex);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _animTimer.Stop();
            _addressTimer.Stop();
            _playerTimer.Stop();

            // 关程序时把绘图内容落盘（拖动中的改动可能还攒在延时定时器里）
            _drawSaveTimer.Stop();
            _drawings.Save();

            try { _server.Stop(); } catch { /* 忽略 */ }
            if (_drawCache != null) _drawCache.Dispose();
            DisposeThumbnails();
            base.OnFormClosing(e);
        }

        // ============================================================
        //  布局
        // ============================================================

        private Rectangle PanelBounds()
        {
            var left = (int)Math.Round(_sidebarWidth + PanelGap + _panelSlide);
            return new Rectangle(left, 0, _panelWidth, ClientSize.Height);
        }

        private void LayoutChildren()
        {
            // MinimumSize / Bounds 在构造期就会触发 OnResize，那时控件还没建好，
            // 这里必须挡住，否则空引用直接让程序启动失败。
            if (_canvas == null || _sidebar == null || _panelHost == null) return;
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

            _canvas.Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            _sidebar.Bounds = new Rectangle(0, 0, (int)Math.Round(_sidebarWidth), ClientSize.Height);

            if (_panelOpen != PanelKind.None)
            {
                var bounds = PanelBounds();
                _panelHost.Bounds = bounds;
                _panelScroll.Bounds = new Rectangle(0, 0, bounds.Width, bounds.Height);

                using (var region = Theme.RightRoundedRect(
                    new RectangleF(0, 0, bounds.Width, bounds.Height), 16f))
                {
                    var old = _panelHost.Region;
                    _panelHost.Region = new Region(region);
                    if (old != null) old.Dispose();
                }

                _canvas.PanelRect = bounds;
            }
            else
            {
                _canvas.PanelRect = Rectangle.Empty;
            }

            _sidebar.Invalidate();

            // 绘图面板浮在右上角，窗口尺寸变了要把它拉回可见范围内
            if (_drawPanel != null && _drawPanelOpen)
            {
                var height = Math.Min(560, Math.Max(320, ClientSize.Height - 40));
                if (_drawPanel.Height != height) _drawPanel.Size = new Size(_drawPanel.Width, height);

                var left = Math.Min(_drawPanel.Left, Math.Max(0, ClientSize.Width - _drawPanel.Width));
                var top = Math.Min(_drawPanel.Top, Math.Max(0, ClientSize.Height - _drawPanel.Height));
                if (left < 0) left = 0;
                if (top < 0) top = 0;
                if (_drawPanel.Left != left || _drawPanel.Top != top)
                    _drawPanel.Location = new Point(left, top);

                _drawPanel.BringToFront();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
            ScheduleAnchorBroadcast();
        }

        /// <summary>画布尺寸变了就排一次锚点重播（防抖，见 <see cref="_anchorTimer"/>）。</summary>
        private void ScheduleAnchorBroadcast()
        {
            if (_canvas == null || _maps == null) return;
            if (string.IsNullOrEmpty(_maps.CurrentMap)) return;   // 没底图，手机也没得对
            _anchorTimer.Stop();
            _anchorTimer.Start();
        }

        /// <summary>锚点真的变了才重播一次 map_change（否则纯属浪费流量）。</summary>
        private void BroadcastAnchorIfChanged()
        {
            var anchor = _canvas == null ? null : _canvas.AnchorSnapshot;
            var signature = AnchorSignature(anchor);
            if (signature == _lastAnchorSignature) return;
            _lastAnchorSignature = signature;

            if (string.IsNullOrEmpty(_maps.CurrentMap)) return;
            _hub.Broadcast("map_change", MapPayloadFor(_maps.CurrentMap, _maps.FitMode));
            AppLog.Write("网格锚点更新：" + (anchor == null ? "-" : anchor.Describe()));
        }

        private static string AnchorSignature(GridAnchor anchor)
        {
            if (anchor == null || !anchor.Valid) return "-";
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return anchor.CellsX.ToString("0.####", c) + "|" + anchor.CellsY.ToString("0.####", c) + "|" +
                   anchor.HalfCellsW.ToString("0.####", c) + "|" + anchor.HalfCellsH.ToString("0.####", c);
        }

        // ============================================================
        //  动画
        // ============================================================

        private static double EaseInOut(double t)
        {
            if (t < 0.5) return 4 * t * t * t;
            var f = -2 * t + 2;
            return 1 - (f * f * f) / 2;
        }

        private void AnimTick(object sender, EventArgs e)
        {
            var now = Environment.TickCount;
            var active = false;

            if (_sidebarAnimating)
            {
                var t = _sidebarAnimDuration <= 0 ? 1.0 : (now - _sidebarAnimStart) / (double)_sidebarAnimDuration;
                if (t >= 1) { t = 1; _sidebarAnimating = false; } else active = true;

                var eased = EaseInOut(t);
                _sidebarWidth = _sidebarFrom + (_sidebarTo - _sidebarFrom) * eased;
                _sidebar.TextOpacity = _sidebarExpanded ? eased : 1 - eased;
            }

            if (_fadeAnimating)
            {
                var t = _fadeAnimDuration <= 0 ? 1.0 : (now - _fadeAnimStart) / (double)_fadeAnimDuration;
                if (t >= 1) { t = 1; _fadeAnimating = false; } else active = true;

                var eased = EaseInOut(t);
                _maskOpacity = _maskFrom + (_maskTo - _maskFrom) * eased;
                _panelSlide = _slideFrom + (_slideTo - _slideFrom) * eased;
                _canvas.MaskOpacity = _maskOpacity;
            }

            LayoutChildren();
            if (!active) _animTimer.Stop();
        }

        private void StartSidebarAnimation(bool expanded)
        {
            _sidebarFrom = _sidebarWidth;
            _sidebarTo = expanded ? SidebarPanel.ExpandedWidth : SidebarPanel.CollapsedWidth;
            _sidebarAnimStart = Environment.TickCount;
            _sidebarAnimating = true;
            _animTimer.Start();
        }

        private void StartFadeAnimation(double maskTo, double slideTo)
        {
            _maskFrom = _maskOpacity;
            _maskTo = maskTo;
            _slideFrom = _panelSlide;
            _slideTo = slideTo;
            _fadeAnimStart = Environment.TickCount;
            _fadeAnimating = true;
            _animTimer.Start();
        }

        // ============================================================
        //  交互
        // ============================================================

        private void OnSidebarItemClicked(int index)
        {
            if (_busy) return;

            switch (index)
            {
                case 0:
                    _sidebarExpanded = !_sidebarExpanded;
                    StartSidebarAnimation(_sidebarExpanded);
                    break;
                case 1:
                    TogglePanel(PanelKind.Map);
                    break;
                case 2:
                    TogglePanel(PanelKind.Network);
                    break;
                case 3:
                    ToggleDrawPanel();
                    break;
                case 4:
                    ApplyFullscreen(!_fullscreen);
                    break;
                case 5:
                    // "关于 / 署名"。走的是和地图设置、网络一样的浮空面板逻辑
                    // （左侧抽屉 + 灰色遮罩 + 侧栏收回），只有绘图面板是例外。
                    TogglePanel(PanelKind.About);
                    break;
            }
        }

        // ============================================================
        //  绘图面板（右上角浮窗，无遮罩、可拖动、不影响地图点击）
        // ============================================================

        /// <summary>
        /// 开/关绘图面板。
        ///
        /// 刻意**不复用** <see cref="OpenPanel"/>：那个是"左侧抽屉 + 灰色遮罩"，
        /// 而绘图面板要求没有遮罩（面板以外的画布要能正常点，不然没法在地图上画），
        /// 位置也不是贴左侧而是浮在右上角。所以它自己一套开合逻辑。
        /// </summary>
        private void ToggleDrawPanel()
        {
            if (_drawPanelOpen) CloseDrawPanel();
            else OpenDrawPanel();
        }

        private void OpenDrawPanel()
        {
            if (_drawPanelOpen) return;

            // 先把别的挡路的东西收掉：另外两个面板 + 灰色遮罩 + 展开的侧栏。
            // 必须在显示绘图面板之前做，否则遮罩还在的时候画布上是点不动东西的。
            ClearPanelsForDrawing();

            if (_drawPanel == null)
            {
                _drawPanel = new DrawPanel(_canvas, _drawings, _catalog, _drawCache);
                _drawPanel.CloseRequested += delegate { CloseDrawPanel(); };
                _drawPanel.DrawingChanged += OnDrawingChanged;
                Controls.Add(_drawPanel);
            }

            // 固定在右上角（留一点边距），高度跟着窗口走但不超过 560
            var height = Math.Min(560, Math.Max(320, ClientSize.Height - 40));
            _drawPanel.Size = new Size(_drawPanel.Width, height);
            _drawPanel.Location = new Point(
                Math.Max(0, ClientSize.Width - _drawPanel.Width - 16), 16);

            _drawPanel.Visible = true;
            _drawPanel.BringToFront();

            // 画布进入绘图输入模式：不再抛 BlankClicked（点画布 = 画画，不是关面板）
            _canvas.DrawInput = true;
            _drawPanelOpen = true;

            AppLog.Write("绘图面板：打开  " + _drawPanel.StateText());
        }

        /// <summary>
        /// 打开绘图面板之前把界面收拾干净：其它浮空面板和灰色遮罩**立刻**收掉，
        /// 侧栏如果展开着也一并缩回。
        ///
        /// 刻意不走 <see cref="CloseAll"/> 那套淡出动画：
        ///  * 遮罩是画在画布上的，只要还剩一点点不透明度，点画布就收不到鼠标事件，
        ///    "打开面板立刻就能在地图上画"就做不到；
        ///  * 侧栏压住画布左边一条，展开着会挡住要画的格子。
        /// 侧栏本身仍然带宽度动画（跳变太生硬），但它一启动就已经在缩，不挡事。
        /// </summary>
        private void ClearPanelsForDrawing()
        {
            StopPolling();

            if (_panelOpen != PanelKind.None)
            {
                _panelOpen = PanelKind.None;
                _fadeAnimating = false;
                _panelSlide = 0;

                _panelHost.Visible = false;
                _canvas.PanelRect = Rectangle.Empty;
                ClearPanelReferences();
            }

            _maskOpacity = 0;
            _canvas.MaskOpacity = 0;

            if (_sidebarExpanded)
            {
                _sidebarExpanded = false;
                StartSidebarAnimation(false);
            }
        }

        private void CloseDrawPanel()
        {
            if (!_drawPanelOpen) return;
            _drawPanelOpen = false;

            _canvas.DrawInput = false;
            _canvas.Preview = null;

            // 关面板 = 清掉所有选区状态，**已画的地形/实体/物品保留**
            if (_drawPanel != null)
            {
                _drawPanel.CloseSelection();
                _drawPanel.Visible = false;
            }

            FlushDrawings(true);
            AppLog.Write("绘图面板：关闭（选区已清空，绘图内容保留 " + _drawings.Count + " 格）");
        }

        private void OnDrawMouseDown(MouseEventArgs e)
        {
            if (_drawPanel != null && _drawPanelOpen) _drawPanel.OnMapMouseDown(e);
        }

        private void OnDrawMouseMove(MouseEventArgs e)
        {
            if (_drawPanel != null && _drawPanelOpen) _drawPanel.OnMapMouseMove(e);
        }

        private void OnDrawMouseUp(MouseEventArgs e)
        {
            if (_drawPanel != null && _drawPanelOpen) _drawPanel.OnMapMouseUp(e);
        }

        private void OnDrawMouseLeave()
        {
            if (_drawPanel != null && _drawPanelOpen) _drawPanel.OnMapMouseLeave();
        }

        /// <summary>绘图内容变化：刷新画布；落笔结束就立刻广播，拖动中就攒着。</summary>
        private void OnDrawingChanged(bool immediate)
        {
            _canvas.Invalidate();
            if (_drawPanel != null && _drawPanelOpen) _drawPanel.RefreshStatusText();

            if (immediate) FlushDrawings(true);
            else
            {
                _drawSaveTimer.Stop();
                _drawSaveTimer.Start();
            }
        }

        /// <summary>落盘（可选）并把绘图版本号推给玩家端。</summary>
        private void FlushDrawings(bool save)
        {
            _drawSaveTimer.Stop();
            if (save) _drawings.Save();

            // 只推一个版本号，玩家端自己去 /api/drawing 取内容 ——
            // 一笔画几十格的话，把整份绘图塞进 SSE 太浪费。
            _hub.Broadcast("drawing_change", MiniJson.WriteObject("version", _drawings.Version));
        }

        /// <summary>把绘图状态打进日志（--ui 自检时用）。</summary>
        private void LogDrawing()
        {
            AppLog.Write("绘图面板    = " + (_drawPanelOpen ? "打开" : "关闭") +
                         "  " + (_drawPanel == null ? "(未创建)" : _drawPanel.StateText()));
            if (_drawPanel != null && _drawPanelOpen)
            {
                AppLog.Write("绘图面板位置= " + Describe(_drawPanel.Bounds));
            }
            AppLog.Write("绘图素材    = terrain=" + _catalog.Of(DrawKind.Terrain).Count +
                         "  entity=" + _catalog.Of(DrawKind.Entity).Count +
                         "  item=" + _catalog.Of(DrawKind.Item).Count +
                         "  贴图缓存=" + _drawCache.Count + " 项");
            AppLog.Write("绘图数据    = " + _drawings.Count + " 格  版本=v" + _drawings.Version +
                         "  文件=" + DrawingStore.DefaultPath());
        }

        private void OnCanvasClicked()
        {
            if (_panelOpen != PanelKind.None || _sidebarExpanded) CloseAll();
        }

        private void TogglePanel(PanelKind kind)
        {
            if (_busy) return;
            if (_panelOpen == kind) { CloseAll(); return; }
            OpenPanel(kind);
        }

        /// <summary>
        /// 打开浮空面板。
        /// 不用 async/await —— 目标框架是 .NET Framework 4.0，没有 Task。
        /// 面板骨架同步建好并立刻开始做淡入/滑入动画；真正耗时的数据刷新
        /// （枚举网卡、读名册、加载底图）推迟到动画基本结束之后再跑，
        /// 避免刷新和逐帧重绘抢同一个 UI 线程导致动画掉帧。
        /// </summary>
        private void OpenPanel(PanelKind kind)
        {
            if (_busy) return;
            _busy = true;

            StopPolling();

            // 面板互斥的另一半：绘图面板浮在遮罩之上，如果它开着再开抽屉面板，
            // 屏幕上会同时挂着两个面板，遮罩的层级就没法解释了（见 ClearPanelsForDrawing）。
            // 之前只做了"开绘图面板时关掉抽屉面板"这一个方向，反过来是漏的。
            if (_drawPanelOpen) CloseDrawPanel();

            _panelWidth = Clamp((int)(ClientSize.Width * 0.25), 300, 420);
            // 内容宽度 = 面板宽 - 左右内缩。不用再给滚动条预留宽度：
            // 滚动容器是自绘的细滑块，画在 16px 内缩里，永远不会压到内容。
            _contentWidth = _panelWidth - PanelInset * 2;

            BuildPanelContent(kind);

            _panelOpen = kind;
            _panelSlide = -24;
            _maskOpacity = 0;
            _panelHost.Visible = true;
            _canvas.MaskOpacity = 0;

            LayoutChildren();
            _panelScroll.RefreshLayout();

            StartFadeAnimation(1.0, 0.0);

            // 等动画收尾后再灌数据（一次性定时器）
            Timer loader = new Timer();
            loader.Interval = 220;
            loader.Tick += delegate
            {
                loader.Stop();
                loader.Dispose();
                try
                {
                    if (_panelOpen != kind) return;   // 期间面板已被关掉，跳过

                    if (kind == PanelKind.Map)
                    {
                        RefreshMapList();
                        ApplyMapToCanvas();
                    }
                    else if (kind == PanelKind.Network)
                    {
                        RefreshSettings();
                        RefreshPlayerControls();
                        StartPolling();
                    }
                    // 关于面板没有要刷的数据（网卡、名册、底图列表它都不关心），
                    // 所以这里什么都不做 —— 别顺手调 RefreshSettings()，
                    // 那会去枚举网卡，在没插网线的机器上白等一秒。
                    else
                    {
                        RefreshUpdateStatusLine();
                    }
                }
                finally
                {
                    _busy = false;
                }
            };
            loader.Start();
        }

        private void CloseAll()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                StopPolling();

                if (_panelOpen != PanelKind.None)
                {
                    _panelOpen = PanelKind.None;
                    StartFadeAnimation(0.0, _panelSlide - 24);

                    // 面板先做淡出，再由定时器把 Visible 关掉
                    var closer = new Timer();
                    closer.Interval = 190;
                    closer.Tick += delegate
                    {
                        closer.Stop();
                        closer.Dispose();
                        _panelHost.Visible = false;
                        _canvas.PanelRect = Rectangle.Empty;
                        ClearPanelReferences();
                    };
                    closer.Start();
                }

                if (_sidebarExpanded)
                {
                    _sidebarExpanded = false;
                    StartSidebarAnimation(false);
                }
            }
            finally
            {
                _busy = false;
            }
        }

        private void ClearPanelReferences()
        {
            _gridTrack = null;
            _gridLabel = null;
            _fitCombo = null;
            _gridColorCombo = null;
            _gridColorHint = null;
            _uploadStatus = null;
            _mapList = null;
            _ipCombo = null;
            _portBox = null;
            _saveButton = null;
            _netStatus = null;
            _fwLabel = null;
            _fwButton = null;
            _qrBox = null;
            _urlLabel = null;
            _playerList = null;
            _roomLockButton = null;
            _rotateLabel = null;
            _playersSignature = null;
            _avatarBox = null;
            _updateStatus = null;

            if (_panelContent != null)
            {
                _panelScroll.SetContent(null);   // 移除并释放旧的内容树
                _panelContent = null;
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // ============================================================
        //  面板内容
        // ============================================================

        private void BuildPanelContent(PanelKind kind)
        {
            ClearPanelReferences();

            _panelContent = new FlowLayoutPanel();
            _panelContent.FlowDirection = FlowDirection.TopDown;
            _panelContent.WrapContents = false;
            _panelContent.AutoSize = true;
            _panelContent.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _panelContent.BackColor = Theme.Panel;
            // 位置与宽度交给 ScrollViewport 统一管理：它才知道内缩多少、滚到哪了。

            _panelScroll.SetContent(_panelContent);

            if (kind == PanelKind.Map) BuildMapPanel();
            else if (kind == PanelKind.Network) BuildNetworkPanel();
            else BuildAboutPanel();

            _panelContent.PerformLayout();
            _panelScroll.RefreshLayout();
            HookWheel(_panelContent);
        }

        // ---------------- 地图设置面板 ----------------

        private void BuildMapPanel()
        {
            AddTitle("地图设置");
            AddSeparator();

            // 滑块和标签都从窗口上的 _cmPerCell 初始化，而不是写死 2.0：
            // 面板关掉再打开时要把上次拖出来的数值原样显示回来。
            var currentCm = _cmPerCell;
            if (currentCm < GridCmMin) currentCm = GridCmMin;
            if (currentCm > GridCmMax) currentCm = GridCmMax;

            _gridLabel = MakeLabel(GridLabelText(currentCm), 9f, Theme.Text);
            AddRow(_gridLabel, 20);

            _gridTrack = new ModernSlider();
            _gridTrack.Minimum = (int)Math.Round(GridCmMin * 10);
            _gridTrack.Maximum = (int)Math.Round(GridCmMax * 10);
            // 先赋值再挂事件：否则构造期的赋值会假装成一次"用户拖动"
            _gridTrack.Value = (int)Math.Round(currentCm * 10);
            _gridTrack.ValueChanged += delegate
            {
                var cm = _gridTrack.Value / 10.0;
                _cmPerCell = cm;                       // 存到窗口上，面板关了也不丢
                if (_gridLabel != null) _gridLabel.Text = GridLabelText(cm);
                _canvas.SetGridSize(cm);
            };
            AddRow(_gridTrack, 30);

            // 网格线颜色：AI 生成的地图明暗差别很大，纯白线铺在亮底图上、
            // 纯黑线铺在暗底图上都会"糊"掉。默认交给程序按底图平均亮度自动选，
            // 判定不合适的图可以在这里手动锁死。
            AddRow(MakeLabel("网格颜色：", 9f, Theme.Text), 20);

            _gridColorCombo = new ModernDropDown();
            _gridColorCombo.SetItems(GridColorItems(), GridColorIndexOf(_maps.GridMode));
            _gridColorCombo.SelectedIndexChanged += delegate
            {
                var mode = GridColorFromIndex(_gridColorCombo.SelectedIndex);
                _maps.SetGridMode(mode);
                ApplyGridColor();
                _hub.Broadcast("map_change", MapPayloadFor(_maps.CurrentMap, _maps.FitMode));
                AppLog.Write("网格颜色模式：" + mode + " → 实际 " + _maps.ResolveGridColor());
            };
            AddRow(_gridColorCombo, 30);

            _gridColorHint = MakeLabel(GridColorHintText(), 8.5f, Theme.TextDim);
            AddRow(_gridColorHint, 18);

            AddRow(MakeLabel("底图显示模式：", 9f, Theme.Text), 20);
            _fitCombo = new ModernDropDown();
            _fitCombo.SetItems(
                new List<string> { "拉伸（铺满）", "填充（保持比例）" },
                _maps.FitMode == "contain" ? 1 : 0);
            _fitCombo.SelectedIndexChanged += delegate
            {
                if (_syncingFromStore) return;    // 程序化同步，不是用户在选（见 SyncCanvasWithStore）
                var name = _maps.CurrentMap;
                if (string.IsNullOrEmpty(name)) return;
                var mode = _fitCombo.SelectedIndex == 1 ? "contain" : "fill";
                _maps.SetCurrent(name, mode);
                _hub.Broadcast("map_change", MapPayloadFor(name, mode));
                ApplyMapToCanvas();
            };
            AddRow(_fitCombo, 30);

            // 底图旋转：只转图片，网格不动（网格是桌面上的实体战斗网格）。
            // 90° 用增量，连点四次绕一圈；180° 就是 delta=180。
            AddRow(MakeLabel("底图旋转（顺时针）：", 9f, Theme.Text), 20);

            var rotateRow = MakeRowPanel(new Padding(0, 0, 0, 10));

            var rotate90 = MakeButton("顺时针 90°", false, true);
            rotate90.Click += delegate { RotateMap(90); };
            rotateRow.Controls.Add(rotate90);

            var rotate180 = MakeButton("旋转 180°", false, true);
            rotate180.Margin = new Padding(8, 3, 0, 0);
            rotate180.Click += delegate { RotateMap(180); };
            rotateRow.Controls.Add(rotate180);

            _rotateLabel = MakeLabel(RotateLabelText(_maps.Rotation), 9f, Theme.Link);
            _rotateLabel.AutoSize = true;
            _rotateLabel.Margin = new Padding(12, 7, 0, 0);
            rotateRow.Controls.Add(_rotateLabel);

            _panelContent.Controls.Add(rotateRow);

            var buttons = MakeRowPanel(new Padding(0, 0, 0, 10));

            var import = MakeButton("导入图片", true, false);
            import.Click += delegate { ImportMap(); };
            buttons.Controls.Add(import);

            _uploadStatus = MakeLabel("就绪", 8.5f, Theme.TextDim);
            _uploadStatus.AutoSize = true;
            _uploadStatus.Margin = new Padding(8, 8, 0, 0);
            buttons.Controls.Add(_uploadStatus);

            _panelContent.Controls.Add(buttons);

            var header = MakeRowPanel(new Padding(0, 0, 0, 6));

            var listTitle = MakeLabel("底图列表", 9f, Theme.Text);
            listTitle.AutoSize = true;
            listTitle.Margin = new Padding(0, 4, 8, 0);
            header.Controls.Add(listTitle);

            var refresh = MakeButton("刷新", false, true);
            refresh.Click += delegate { RefreshMapList(); };
            header.Controls.Add(refresh);

            _panelContent.Controls.Add(header);

            _mapList = new ModernList();
            _mapList.BackColor = Theme.Track;
            _mapList.ItemHeight = 52;
            _mapList.RoundedRows = true;
            _mapList.ItemDraw = DrawMapItem;
            _mapList.ItemActivated += delegate { SelectCurrentListItem("fill"); };
            AddRow(_mapList, 220);

            var actions = MakeRowPanel(new Padding(0, 8, 0, 0));

            var fill = MakeButton("拉伸显示", false, true);
            fill.Click += delegate { SelectCurrentListItem("fill"); };
            actions.Controls.Add(fill);

            var contain = MakeButton("填充显示", false, true);
            contain.Margin = new Padding(8, 3, 0, 0);
            contain.Click += delegate { SelectCurrentListItem("contain"); };
            actions.Controls.Add(contain);

            _panelContent.Controls.Add(actions);

            // 撤掉底图：回到"刚打开程序"的样子（只剩网格）。
            // 刻意和「拉伸/填充显示」分开放：那两个只是改显示方式，这个是**真的把底图摘掉**，
            // 误点代价大得多，所以单独一行、用警示色。
            var removeRow = MakeRowPanel(new Padding(0, 12, 0, 0));

            var removeMap = MakeButton("撤掉地图", false, true);
            removeMap.BackColor = Theme.Danger;
            removeMap.Click += delegate { RemoveMap(); };
            removeRow.Controls.Add(removeMap);

            var removeHint = MakeLabel("撤掉后只剩网格（maps\\ 里的文件不删）", 8f, Theme.TextDim);
            removeHint.AutoSize = true;
            removeHint.Margin = new Padding(10, 7, 0, 0);
            removeRow.Controls.Add(removeHint);

            _panelContent.Controls.Add(removeRow);
        }

        /// <summary>
        /// 撤掉当前底图：画布回到"刚打开程序"的样子 —— 只有网格，没有底图。
        ///
        /// 只摘"当前选中"，**不动 maps\ 里的文件**：列表照旧，随时能再选回来。
        /// 显示模式 / 旋转角 / 网格颜色都保留（它们本来就是全局显示状态）。
        ///
        /// 改状态 + 广播那一步交给 Routes.RemoveCurrentMap()，
        /// 和 POST /api/maps/remove 用的是同一份实现；这里只负责把界面跟着刷新。
        /// </summary>
        private void RemoveMap()
        {
            var removed = _routes.RemoveCurrentMap();
            if (removed == null)
            {
                if (_uploadStatus != null)
                {
                    _uploadStatus.Text = "本来就没有底图";
                    _uploadStatus.ForeColor = Theme.TextDim;
                }
                return;
            }

            ApplyMapToCanvas();
            RefreshMapList();

            if (_uploadStatus != null)
            {
                _uploadStatus.Text = "已撤掉：" + removed;
                _uploadStatus.ForeColor = Theme.Success;
            }
        }

        private void DrawMapItem(ListItemDrawArgs args)
        {
            var info = args.Item as MapInfo;
            if (info == null) return;

            var selected = args.Selected;
            var isCurrent = string.Equals(info.Name, _maps.CurrentMap, StringComparison.Ordinal);

            var box = args.Bounds;
            var background = selected ? Theme.ButtonHover : (args.Hover ? Theme.Card : Theme.Track);
            Ui.FillRounded(args.Graphics, box, 6f, background);

            if (isCurrent)
            {
                using (var pen = new Pen(Theme.Accent, 2f))
                using (var path = Theme.RoundedRect(
                    new RectangleF(box.X + 1f, box.Y + 1f, box.Width - 2f, box.Height - 2f), 5f))
                {
                    args.Graphics.DrawPath(pen, path);
                }
            }

            // 缩略图：等比缩放进一个正方形槽位，再居中
            var slot = new Rectangle(box.X + 8, box.Y + 8, Math.Max(1, box.Height - 16), Math.Max(1, box.Height - 16));
            var thumb = GetThumbnail(info.Path);
            if (thumb != null)
            {
                var scale = Math.Min(slot.Width / (double)thumb.Width, slot.Height / (double)thumb.Height);
                var w = Math.Max(1, (int)Math.Round(thumb.Width * scale));
                var h = Math.Max(1, (int)Math.Round(thumb.Height * scale));
                var dest = new Rectangle(
                    slot.X + (slot.Width - w) / 2,
                    slot.Y + (slot.Height - h) / 2, w, h);

                var state = args.Graphics.Save();
                using (var clip = Theme.RoundedRect(
                    new RectangleF(dest.X - 1f, dest.Y - 1f, dest.Width + 2f, dest.Height + 2f), 4f))
                {
                    args.Graphics.SetClip(clip, CombineMode.Replace);
                    args.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    args.Graphics.DrawImage(thumb, dest);
                }
                args.Graphics.Restore(state);
            }
            else
            {
                Ui.FillRounded(args.Graphics, slot, 4f, Theme.Background);
            }

            Ui.PrepareText(args.Graphics);
            var textRect = new Rectangle(
                slot.Right + 10, box.Y,
                Math.Max(0, box.Right - slot.Right - 20), box.Height);

            // 注意：Ui.Font 返回的是**共享的缓存字体**，绝对不能用 using 包起来 ——
            // 一 Dispose 缓存里那条就废了，下一次绘制会抛
            // "ArgumentException: 参数无效"，整个列表变成白底红叉。
            var font = Ui.Font(9f, isCurrent ? FontStyle.Bold : FontStyle.Regular);
            using (var format = new StringFormat())
            {
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisPath;
                format.FormatFlags = StringFormatFlags.NoWrap;
                using (var brush = new SolidBrush(Theme.Text))
                {
                    args.Graphics.DrawString(Path.GetFileNameWithoutExtension(info.Name),
                        font, brush, textRect, format);
                }
            }
        }

        /// <summary>
        /// 生成底图缩略图（最长边 80px，保持比例）。
        ///
        /// 这里**必须**把源图画进一张全新的 Bitmap，不能直接返回
        /// <c>Image.GetThumbnailImage</c> 的结果：那个返回值仍然挂着源图的数据，
        /// 源图一 Dispose（本方法是 using 包掉的），缩略图就成了悬空引用 ——
        /// 刚生成的那一瞬间还能画，等 GC 真正回收源图之后再画就会抛
        /// <c>ArgumentException: 参数无效</c>，而 WinForms 会把整个控件的绘制
        /// 换成"白底红叉"的错误图。踩过一次，别再改回去。
        /// </summary>
        private Image GetThumbnail(string path)
        {
            if (_thumbCache.ContainsKey(path)) return _thumbCache[path];

            Image thumb = null;
            try
            {
                // 统一入口：webp 走自研解码器，其它格式仍旧 GDI+
                using (var image = ImageLoader.Load(path))
                {
                    const int box = 80;
                    var scale = Math.Min(box / (double)image.Width, box / (double)image.Height);
                    if (scale > 1d) scale = 1d;

                    var w = Math.Max(1, (int)Math.Round(image.Width * scale));
                    var h = Math.Max(1, (int)Math.Round(image.Height * scale));

                    var bmp = new Bitmap(w, h);
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
            catch (Exception ex)
            {
                AppLog.Write("生成缩略图失败：" + path, ex);
            }

            _thumbCache[path] = thumb;
            return thumb;
        }

        private void DisposeThumbnails()
        {
            foreach (var image in _thumbCache.Values)
            {
                if (image != null) image.Dispose();
            }
            _thumbCache.Clear();
        }

        /// <summary>
        /// 让列表高度跟着条目数走（留一个最小高度，超过上限就滚动）。
        /// 固定 220px 的话，只有一两张底图时下面会留一大块空灰块，很显眼。
        /// </summary>
        private void FitListHeight(ModernList list, int minHeight, int maxHeight)
        {
            if (list == null) return;

            var wanted = list.Count * list.ItemHeight + 2;
            if (wanted < minHeight) wanted = minHeight;
            if (wanted > maxHeight) wanted = maxHeight;
            if (list.Height == wanted) return;

            list.Height = wanted;
            if (_panelScroll != null) _panelScroll.RefreshLayout();
        }

        private void RefreshMapList()
        {
            if (_mapList == null) return;

            var list = _maps.List();
            list.Sort(delegate (MapInfo a, MapInfo b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });

            var selectedName = _mapList.SelectedItem is MapInfo ? ((MapInfo)_mapList.SelectedItem).Name : null;

            _mapList.BeginUpdate();
            _mapList.Items.Clear();
            foreach (var item in list) _mapList.Items.Add(item);

            if (selectedName != null)
            {
                for (var i = 0; i < _mapList.Items.Count; i++)
                {
                    var info = (MapInfo)_mapList.Items[i];
                    if (string.Equals(info.Name, selectedName, StringComparison.Ordinal))
                    {
                        _mapList.SelectedIndex = i;
                        break;
                    }
                }
            }
            _mapList.EndUpdate();
            FitListHeight(_mapList, 56, 220);
        }

        private void SelectCurrentListItem(string mode)
        {
            var info = _mapList == null ? null : _mapList.SelectedItem as MapInfo;
            if (info == null) return;

            _maps.SetCurrent(info.Name, mode);
            if (_fitCombo != null) _fitCombo.SelectedIndex = mode == "contain" ? 1 : 0;

            _hub.Broadcast("map_change", MapPayloadFor(info.Name, mode));
            ApplyMapToCanvas();
            RefreshMapList();
        }

        private void ImportMap()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择底图图片";
                dialog.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|所有文件|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    if (_uploadStatus != null) _uploadStatus.Text = "上传中…";
                    var bytes = File.ReadAllBytes(dialog.FileName);
                    var result = _maps.Upload(Path.GetFileName(dialog.FileName), bytes);

                    if (_uploadStatus != null)
                    {
                        _uploadStatus.Text = result.Success ? "导入成功" : "导入失败：" + result.Message;
                        _uploadStatus.ForeColor = result.Success ? Theme.Success : Theme.Error;
                    }
                    RefreshMapList();
                }
                catch (Exception ex)
                {
                    AppLog.Write("导入底图失败", ex);
                    if (_uploadStatus != null)
                    {
                        _uploadStatus.Text = "导入失败：" + ex.Message;
                        _uploadStatus.ForeColor = Theme.Error;
                    }
                }
            }
        }

        /// <summary>
        /// 底图顺时针再转 <paramref name="delta"/> 度。玩家端会在此基础上
        /// **再**顺时针加 90°（手机"上端"对应电脑"左端"），见 player.js。
        /// </summary>
        private void RotateMap(int delta)
        {
            var rotation = _maps.Rotate(delta);
            if (_rotateLabel != null) _rotateLabel.Text = RotateLabelText(rotation);

            _hub.Broadcast("map_change", MapPayloadFor(_maps.CurrentMap, _maps.FitMode));
            ApplyMapToCanvas();
            AppLog.Write("底图旋转：" + rotation + "°（本次 delta=" + delta + "）");
        }

        private static string RotateLabelText(int rotation)
        {
            return "当前 " + rotation + "°";
        }

        // ---------------- 网格颜色 ----------------

        private static List<string> GridColorItems()
        {
            return new List<string> { "自动（跟随底图）", "白色", "黑色" };
        }

        private static int GridColorIndexOf(string mode)
        {
            if (mode == "white") return 1;
            if (mode == "black") return 2;
            return 0;
        }

        private static string GridColorFromIndex(int index)
        {
            if (index == 1) return "white";
            if (index == 2) return "black";
            return "auto";
        }

        /// <summary>
        /// 把"自动判定成了什么、依据是什么"摆到面板上。
        /// 光有一个「自动」选项，出了问题（比如某张图判定反了）根本没法排查，
        /// 所以把平均亮度一起显示出来 —— 一眼就能看出为什么选了白/黑。
        /// </summary>
        private string GridColorHintText()
        {
            var color = _maps.ResolveGridColor();
            var label = color == "black" ? "黑线" : "白线";

            if (_maps.GridMode != "auto") return "固定" + label;
            if (string.IsNullOrEmpty(_maps.CurrentMap)) return "自动 → 白线（未选底图，深色底）";

            var tone = _maps.CurrentTone();
            if (!tone.Ok) return "自动 → " + label + "（底图读不出来，按白线处理）";

            return "自动 → " + label + "（底图平均亮度 " + tone.Mean.ToString("0.00") +
                   (tone.Mean < MapTone.Threshold ? "，偏暗）" : "，偏亮）");
        }

        /// <summary>把解析出来的网格线颜色应用到画布，并刷新那行说明。</summary>
        private void ApplyGridColor()
        {
            _canvas.SetGridColor(_maps.ResolveGridColor());
            if (_gridColorHint != null) _gridColorHint.Text = GridColorHintText();
        }

        private void ApplyMapToCanvas()
        {
            _canvas.SetRotation(_maps.Rotation);
            // 换底图之后 auto 的判定结果可能就变了（亮土黄 → 暗洞窟），
            // 所以每次刷新底图都顺手把网格线颜色重新解一遍。
            ApplyGridColor();

            var name = _maps.CurrentMap;
            if (string.IsNullOrEmpty(name))
            {
                _canvas.SetMap(null, 0, 0, _maps.FitMode);
            }
            else
            {
                var path = Path.Combine(AppEnv.MapsFolder, name);
                var size = ImageSizeReader.Read(path);
                _canvas.SetMap(path,
                    size == null ? 0 : size.Width,
                    size == null ? 0 : size.Height,
                    _maps.FitMode);
            }

            // 记下"画布现在到底是哪一套状态"，给 SyncCanvasWithStore 做比较用。
            // 必须放在最后：SetMap 一返回，网格锚点就已经是按这组参数算好的了。
            _canvasMapName = name;
            _canvasFitMode = _maps.FitMode;
            _canvasRotation = _maps.Rotation;

            // 关键：网格锚点是**从画布现算**的，而所有调用点几乎都是"先广播、后摆画布"
            // （面板上的处理器都是 _hub.Broadcast(...) 在前、ApplyMapToCanvas() 在后），
            // 于是刚播出去的那条 map_change 里的锚点描述的其实还是**上一张**底图的几何。
            // 这里排一次防抖重播把它纠正过来 —— 手机端会把随后这条覆盖上去。
            ScheduleAnchorBroadcast();
        }

        /// <summary>
        /// 把画布（以及面板上那几个镜像状态的小控件）对齐到 <see cref="MapStore"/> 的当前状态。
        ///
        /// 为什么需要这一层：<c>/api/maps/*</c> 那几个路由改的是 MapStore，它们跑在 HTTP
        /// 线程上，而 Routes 刻意不跨线程去动 UI 控件（见 Routes 的说明），所以画布不会
        /// 自己跟着变。DM 在面板上点的时候是同步调 <see cref="ApplyMapToCanvas"/> 的，
        /// 但无人值守的自测脚本、以及任何以后可能加的远程控制都只走 HTTP ——
        /// 少了这一层，DM 自己的画布和手机就显示着两张不同的底图，
        /// 而**网格锚点又是从画布读的**，锚点会跟着一起错，手机上的绘图就跑到别处去了。
        ///
        /// 只在真不一致时才动手：这是每 200ms 跑一次的热路径，而 ApplyMapToCanvas
        /// 会重新解码一张几 MB 的底图，不能白跑。
        /// </summary>
        private void SyncCanvasWithStore()
        {
            if (_maps == null || _canvas == null) return;

            var name = _maps.CurrentMap;
            var same = string.Equals(name, _canvasMapName, StringComparison.Ordinal)
                    && string.Equals(_maps.FitMode, _canvasFitMode, StringComparison.Ordinal)
                    && _maps.Rotation == _canvasRotation;
            if (same) return;

            AppLog.Write("画布落后于 MapStore，重新同步："
                + (name == null ? "(无底图)" : name)
                + " / " + _maps.FitMode + " / " + _maps.Rotation + "°");

            // 下面会程序化地改下拉框，它的 SelectedIndexChanged 又会去改 MapStore ——
            // 那是用户交互用的路径，程序化同步时要绕开，否则会多播一次 map_change。
            _syncingFromStore = true;
            try
            {
                ApplyMapToCanvas();
                RefreshMapList();
                if (_rotateLabel != null) _rotateLabel.Text = RotateLabelText(_maps.Rotation);
                if (_fitCombo != null) _fitCombo.SelectedIndex = _maps.FitMode == "contain" ? 1 : 0;
                if (_gridColorHint != null) _gridColorHint.Text = GridColorHintText();
            }
            finally
            {
                _syncingFromStore = false;
            }
        }

        // ---------------- 网络面板 ----------------

        private void BuildNetworkPanel()
        {
            AddTitle("网络连接");
            AddSeparator();

            AddRow(MakeLabel("IP 地址", 9f, Theme.Text), 20);
            _ipCombo = new ModernDropDown();
            AddRow(_ipCombo, 30);

            AddRow(MakeLabel("端口（1-65535）", 9f, Theme.Text), 20);
            _portBox = new ModernTextBox();
            _portBox.MaxLength = 5;                 // 端口最多 5 位数，输入框没必要占满整行
            _portBox.Text = _settings.Port.ToString();
            _portBox.Input.KeyPress += delegate (object s, KeyPressEventArgs e)
            {
                if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
            };
            AddRow(_portBox, 30, 104);

            _saveButton = MakeButton("保存并重开服务", true, false);
            _saveButton.Click += delegate { SaveSettings(); };
            AddRow(_saveButton, 34);

            _netStatus = MakeLabel(string.Empty, 8.5f, Theme.Success);
            AddRow(_netStatus, 22);

            // 防火墙这一行不是装饰：手机连不上时，90% 的原因就在这。
            // 光看二维码和地址是看不出问题的（它们都对），必须把状态摆出来。
            _fwLabel = MakeLabel(string.Empty, 8.5f, Theme.TextDim);
            AddRow(_fwLabel, 20);

            _fwButton = MakeButton("放行防火墙（需管理员确认）", false, true);
            _fwButton.Click += delegate { FixFirewallFromUi(); };
            AddRow(_fwButton, 30, 210);
            RefreshFirewallStatus();

            AddSeparator();

            AddRow(MakeLabel("玩家地址", 11f, Color.White), 26);
            _urlLabel = MakeLabel(_settings.PlayerUrl, 9f, Theme.Link);
            AddRow(_urlLabel, 22);

            _qrBox = new PictureBox();
            _qrBox.BackColor = Theme.Panel;
            _qrBox.Height = 160;
            _qrBox.Width = 160;
            _qrBox.SizeMode = PictureBoxSizeMode.Zoom;
            var qrRow = MakeRowPanel(new Padding(0, 0, 0, 4));
            _qrBox.Margin = new Padding((_contentWidth - 160) / 2, 0, 0, 0);
            qrRow.Controls.Add(_qrBox);
            _panelContent.Controls.Add(qrRow);

            var qrHint = MakeLabel("手机扫码即可加入", 8f, Theme.TextDim);
            qrHint.TextAlign = ContentAlignment.MiddleCenter;
            AddRow(qrHint, 20);

            AddSeparator();

            var header = MakeRowPanel(new Padding(0, 0, 0, 6));

            var playersTitle = MakeLabel("在线玩家", 11f, Color.White);
            playersTitle.AutoSize = true;
            playersTitle.Margin = new Padding(0, 6, 8, 0);
            header.Controls.Add(playersTitle);

            _roomLockButton = MakeButton("锁定房间", false, true);
            _roomLockButton.Click += delegate { ToggleRoomLock(); };
            header.Controls.Add(_roomLockButton);

            _panelContent.Controls.Add(header);

            _playerList = new ModernList();
            _playerList.BackColor = Theme.Track;
            _playerList.ItemHeight = 30;
            _playerList.RoundedRows = true;
            _playerList.ItemDraw = DrawPlayerItem;
            AddRow(_playerList, 150);

            var kick = MakeButton("踢出选中玩家", false, true);
            kick.BackColor = Theme.Danger;
            kick.Click += delegate { KickSelectedPlayer(); };
            AddRow(kick, 30);
        }

        private void DrawPlayerItem(ListItemDrawArgs args)
        {
            var row = args.Item as PlayerRow;
            if (row == null) return;

            var box = args.Bounds;
            var background = args.Selected ? Theme.ButtonHover : (args.Hover ? Theme.Card : Theme.Track);
            Ui.FillRounded(args.Graphics, box, 6f, background);

            // 在线状态用一个彩色圆点表示（Win7 上 GDI 画圆点比 ● 字符更可靠）
            var dot = new Rectangle(box.X + 10, box.Y + box.Height / 2 - 4, 8, 8);
            using (var brush = new SolidBrush(row.Online ? Theme.Online : Theme.TextDim))
            {
                args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                args.Graphics.FillEllipse(brush, dot);
            }

            Ui.PrepareText(args.Graphics);

            var nameWidth = Math.Max(0, box.Width - 34);
            var nameRect = new Rectangle(dot.Right + 10, box.Y, nameWidth, box.Height);
            Ui.DrawText(args.Graphics, row.Name, Ui.Font(9.5f),
                row.Online ? Theme.Text : Theme.TextDim, nameRect, ContentAlignment.MiddleLeft);

            var ipRect = new Rectangle(nameRect.Right, box.Y, Math.Max(0, box.Width - nameRect.Right - 8), box.Height);
            Ui.DrawText(args.Graphics, row.Ip, Ui.Font(8.5f), Theme.TextDim, ipRect, ContentAlignment.MiddleRight);
        }

        private void RefreshSettings()
        {
            RefreshAddresses(force: true);
            if (_portBox != null) _portBox.Text = _settings.Port.ToString();
            if (_urlLabel != null) _urlLabel.Text = _settings.PlayerUrl;
            UpdateRoomLockButton();
            RefreshQrImage();
        }

        private void UpdateRoomLockButton()
        {
            if (_roomLockButton == null) return;
            _roomLockButton.Text = _settings.RoomLocked ? "解锁房间" : "锁定房间";
            _roomLockButton.BackColor = _settings.RoomLocked ? Theme.Locked : Theme.Button;
        }

        private void RefreshQrImage()
        {
            if (_qrBox == null) return;

            var url = _settings.PlayerUrl;
            if (_qrCacheKey == url && _qrBox.Image != null) return;

            try
            {
                var dataUrl = QrCodeGenerator.RenderDataUrl(url, 6, 2);
                var base64 = dataUrl.Substring(dataUrl.IndexOf(',') + 1);
                var bytes = Convert.FromBase64String(base64);

                using (var ms = new MemoryStream(bytes))
                {
                    using (var image = Image.FromStream(ms))
                    {
                        var old = _qrBox.Image;
                        _qrBox.Image = new Bitmap(image);
                        if (old != null) old.Dispose();
                    }
                }
                _qrCacheKey = url;
            }
            catch (Exception ex)
            {
                AppLog.Write("生成二维码图片失败", ex);
            }
        }

        private void RefreshAddresses()
        {
            RefreshAddresses(false);
        }

        private void RefreshAddresses(bool force)
        {
            if (_panelOpen != PanelKind.Network || _ipCombo == null) return;

            var fresh = IpEnumerator.Enumerate();
            if (fresh.Count == 0) return;

            var same = !force && fresh.Count == _addresses.Count;
            if (same)
            {
                for (var i = 0; i < fresh.Count; i++)
                {
                    if (!string.Equals(fresh[i].Address, _addresses[i].Address, StringComparison.OrdinalIgnoreCase))
                    {
                        same = false;
                        break;
                    }
                }
            }
            if (same) return;

            var selected = SelectedAddress();
            _addresses = fresh;

            var names = new List<string>();
            foreach (var item in _addresses) names.Add(item.Display);

            var index = IndexOfAddress(selected);
            if (index < 0) index = IndexOfAddress(_settings.ListenIp);
            _ipCombo.SetItems(names, index >= 0 ? index : 0);
        }

        private int IndexOfAddress(string address)
        {
            if (string.IsNullOrEmpty(address)) return -1;
            for (var i = 0; i < _addresses.Count; i++)
            {
                if (string.Equals(_addresses[i].Address, address, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private string SelectedAddress()
        {
            if (_ipCombo == null || _ipCombo.SelectedIndex < 0 || _ipCombo.SelectedIndex >= _addresses.Count)
                return null;
            return _addresses[_ipCombo.SelectedIndex].Address;
        }

        private void SaveSettings()
        {
            var ip = SelectedAddress();
            if (ip == null)
            {
                SetNetStatus("请先选择一个 IP 地址", true);
                return;
            }

            int port;
            if (!int.TryParse(_portBox == null ? string.Empty : _portBox.Text, out port) ||
                !ServerSettings.IsValidPort(port))
            {
                SetNetStatus("端口必须是 1-65535 之间的整数", true);
                return;
            }

            var changed = !string.Equals(ip, _settings.ListenIp, StringComparison.OrdinalIgnoreCase)
                          || port != _settings.Port;

            string message;
            if (!_settings.TryUpdate(ip, port, out message))
            {
                SetNetStatus("失败：" + message, true);
                return;
            }

            if (changed)
            {
                if (_saveButton != null) _saveButton.Enabled = false;
                SetNetStatus("正在用新地址重开服务…", false);

                try
                {
                    _hub.CloseAll();
                    _server.Stop();
                    _server.Start(port);

                    _hub.Broadcast("server_moved", MiniJson.WriteObject("playerUrl", _settings.PlayerUrl));
                    _hub.Broadcast("map_change", MapPayloadFor(_maps.CurrentMap, _maps.FitMode));

                    SetNetStatus("已切换到 " + _settings.ListenIp + ":" + _settings.Port +
                                 "（底图与名册已保留）", false);
                }
                catch (Exception ex)
                {
                    AppLog.Write("重开服务失败", ex);
                    SetNetStatus("新地址未能就绪（端口可能被占用）：" + ex.Message, true);
                }
                finally
                {
                    if (_saveButton != null) _saveButton.Enabled = true;
                }
            }
            else
            {
                SetNetStatus("配置未变化", false);
            }

            if (_urlLabel != null) _urlLabel.Text = _settings.PlayerUrl;
            _qrCacheKey = null;
            RefreshQrImage();
        }

        private void SetNetStatus(string text, bool error)
        {
            if (_netStatus == null) return;
            _netStatus.Text = text;
            _netStatus.ForeColor = error ? Theme.Error : Theme.Success;
        }

        private void ToggleRoomLock()
        {
            _settings.RoomLocked = !_settings.RoomLocked;
            _hub.Broadcast("room_state", MiniJson.WriteObject("locked", _settings.RoomLocked));
            UpdateRoomLockButton();
        }

        private void StartPolling()
        {
            StopPolling();
            _addressTimer.Start();
            _playerTimer.Start();
        }

        private void StopPolling()
        {
            _addressTimer.Stop();
            _playerTimer.Stop();
        }

        private void RefreshPlayerControls()
        {
            _playersSignature = null;
            RefreshPlayers();
        }

        private void RefreshPlayers()
        {
            if (_panelOpen != PanelKind.Network || _playerList == null) return;

            var players = _game.Snapshot();

            var signature = new System.Text.StringBuilder();
            foreach (var player in players)
            {
                signature.Append(player.Name).Append('\u0001')
                         .Append(player.Ip).Append('\u0001')
                         .Append(player.Online ? '1' : '0').Append('|');
            }
            var current = signature.ToString();
            if (current == _playersSignature) return;
            _playersSignature = current;

            var selectedRow = _playerList.SelectedItem as PlayerRow;
            var selectedIndex = _playerList.SelectedIndex;

            _playerList.BeginUpdate();
            _playerList.Items.Clear();
            foreach (var player in players)
            {
                var row = new PlayerRow();
                row.Name = player.Name;
                row.Ip = player.Ip;
                row.Online = player.Online;
                _playerList.Items.Add(row);
            }
            _playerList.EndUpdate();

            if (selectedIndex >= 0 && selectedIndex < _playerList.Items.Count)
            {
                _playerList.SelectedIndex = selectedIndex;
            }
            else if (selectedRow != null)
            {
                for (var i = 0; i < _playerList.Items.Count; i++)
                {
                    var row = _playerList.Items[i] as PlayerRow;
                    if (row != null && string.Equals(row.Name, selectedRow.Name, StringComparison.Ordinal))
                    {
                        _playerList.SelectedIndex = i;
                        break;
                    }
                }
            }

            FitListHeight(_playerList, 68, 150);
        }

        private void KickSelectedPlayer()
        {
            if (_playerList == null || _playerList.SelectedIndex < 0) return;

            var players = _game.Snapshot();
            if (_playerList.SelectedIndex >= players.Count) return;

            var name = players[_playerList.SelectedIndex].Name;
            var answer = MessageBox.Show(this,
                "确定要踢出 " + name + " 吗？\r\n（他带着原令牌仍可重新进入）",
                "踢出玩家", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;

            var connectionId = _game.Kick(name);
            if (connectionId != null)
            {
                _hub.SendTo(connectionId, "kicked", "{\"reason\":\"kicked\"}");
                _hub.Broadcast("player_list", MiniJson.WriteObject("changed", true));
            }
            _playersSignature = null;
            RefreshPlayers();
        }

        // ============================================================
        //  关于 / 署名 / 更新
        // ============================================================

        private void BuildAboutPanel()
        {
            AddTitle("关于");
            AddSeparator();

            // 头像。用 PictureBox + 椭圆 Region 做圆形裁切 —— GDI+ 画圆角图片
            // 最省事的办法就是"让控件本身是个圆的"，不必去画蒙版。
            // 图片路径取自 exe 同级的 Resources\avatar.png（构建时随程序一起复制）。
            _avatarBox = new PictureBox();
            _avatarBox.Width = AvatarSize;
            _avatarBox.Height = AvatarSize;
            _avatarBox.BackColor = Theme.Panel;
            _avatarBox.SizeMode = PictureBoxSizeMode.Zoom;

            var avatarPath = Path.Combine(Path.Combine(AppEnv.Root, "Resources"), "avatar.png");
            try
            {
                if (File.Exists(avatarPath))
                {
                    var loaded = ImageLoader.Load(avatarPath);
                    // 先缩放成正好 AvatarSize 的方图再挂上去：Zoom 会保持比例，
                    // 而头像本来就不是严格正方形（原图 720×721），
                    // 不先规整的话椭圆 Region 会和图片边缘错开一两个像素。
                    var square = new Bitmap(AvatarSize, AvatarSize);
                    using (var g = Graphics.FromImage(square))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(loaded, new Rectangle(0, 0, AvatarSize, AvatarSize));
                    }
                    loaded.Dispose();
                    _avatarBox.Image = square;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("加载头像失败：" + avatarPath, ex);
            }

            using (var circle = new GraphicsPath())
            {
                circle.AddEllipse(0, 0, AvatarSize, AvatarSize);
                _avatarBox.Region = new Region(circle);
            }

            var avatarRow = MakeRowPanel(new Padding(0, 0, 0, 12));
            _avatarBox.Margin = new Padding(Math.Max(0, (_contentWidth - AvatarSize) / 2), 0, 0, 0);
            avatarRow.Controls.Add(_avatarBox);
            _panelContent.Controls.Add(avatarRow);

            var name = MakeLabel(AppInfo.Name + "  " + AppInfo.VersionText, 13f, Color.White);
            name.TextAlign = ContentAlignment.MiddleCenter;
            AddRow(name, 26);

            var tagline = MakeLabel("跑团地图投屏工具 · Win7 / 32 位兼容版", 8.5f, Theme.TextDim);
            tagline.TextAlign = ContentAlignment.MiddleCenter;
            AddRow(tagline, 22);

            AddSeparator();

            AddRow(MakeLabel("作者", 9f, Theme.Text), 20);
            var author = MakeLabel(AppInfo.Author, 10f, Color.White);
            AddRow(author, 24);

            // 两行链接。做成可点的 Label（Label 本身收不到 Click 的话就退化成
            // 只能看不能点 —— 所以下面还留了一条"复制到剪贴板"的提示行）。
            AddLinkRow(AppInfo.GitHubHome, AppInfo.GitHubHome);
            AddLinkRow(AppInfo.GiteeHome, AppInfo.GiteeHome);

            AddRow(MakeLabel("源码仓库", 9f, Theme.Text), 20);
            AddLinkRow(AppInfo.GitHubRepo, AppInfo.GitHubRepo);

            AddSeparator();

            AddRow(MakeLabel("版本与更新", 9f, Theme.Text), 20);
            _updateStatus = MakeLabel(string.Empty, 8.5f, Theme.TextDim);
            AddRow(_updateStatus, 40);
            RefreshUpdateStatusLine();

            var check = MakeButton("检查更新", false, true);
            check.Click += delegate { CheckForUpdates(false); };
            AddRow(check, 30, 120);

            AddSeparator();

            var installed = AppRegistry.IsInstalledAt(AppEnv.Root);
            var channel = MakeLabel(installed ? "当前为安装版" : "当前为便携版", 8.5f,
                installed ? Theme.Success : Theme.TextDim);
            AddRow(channel, 20);

            var legal = MakeLabel("Copyright © " + AppInfo.Author + "　保留所有权利。", 8f, Theme.TextDim);
            AddRow(legal, 20);
        }

        private const int AvatarSize = 112;

        /// <summary>一行可点的链接（点开系统默认浏览器）。</summary>
        private void AddLinkRow(string text, string url)
        {
            var label = MakeLabel(text, 9f, Theme.Link);
            label.Cursor = Cursors.Hand;
            label.Click += delegate { OpenUrl(url); };
            AddRow(label, 22);
        }

        private static void OpenUrl(string url)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = url;
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                AppLog.Write("打开链接失败：" + url, ex);
            }
        }

        /// <summary>把"上次检查结果"写回关于面板那一行。</summary>
        private void RefreshUpdateStatusLine()
        {
            if (_updateStatus == null) return;

            if (_updateChecking)
            {
                _updateStatus.Text = "正在检查更新…";
                return;
            }

            var skipped = AppRegistry.GetSkippedVersion();
            var text = "当前版本 " + AppInfo.VersionText + "\r\n";
            text += "更新源：Gitee 优先，失败自动转 GitHub";
            if (!string.IsNullOrEmpty(skipped)) text += "\r\n已忽略版本：v" + skipped;

            _updateStatus.Text = text;
        }

        /// <summary>
        /// 启动若干秒后自动查一次更新。
        ///
        /// 刻意**晚一点**再查：启动那一瞬间要起 HTTP 服务、枚举网卡、渲染底图，
        /// 再塞一个网络请求进去会明显拖慢冷启动。而且这是"背景任务"，
        /// 用户感知不到那两秒。
        /// </summary>
        private void StartStartupUpdateCheck()
        {
            // 无头模式 / 自动化测试（--ui、--selfcheck、--no-prompts）不联网：
            // 那些场景要么不该有网络副作用，要么必须安静。
            if (Headless || NoPrompts) return;

            _updateCheckTimer = new Timer();
            _updateCheckTimer.Interval = 4000;
            _updateCheckTimer.Tick += delegate
            {
                _updateCheckTimer.Stop();
                _updateCheckTimer.Dispose();
                _updateCheckTimer = null;
                CheckForUpdates(true);
            };
            _updateCheckTimer.Start();
        }

        /// <summary>
        /// 检查更新。<paramref name="silent"/> = true 表示"没更新/检查失败都别弹窗"，
        /// 只在真发现有新版本时才打扰用户（启动时自动跑的就是这种）。
        /// </summary>
        private void CheckForUpdates(bool silent)
        {
            if (_updateChecking) return;
            _updateChecking = true;
            RefreshUpdateStatusLine();

            UpdateCheck.QueryAsync(delegate (UpdateResult result)
            {
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate { OnUpdateQueried(result, silent); });
                }
                catch
                {
                    // 窗口正在关闭，丢掉这次结果
                }
            });
        }

        private void OnUpdateQueried(UpdateResult result, bool silent)
        {
            _updateChecking = false;

            if (!result.Ok)
            {
                if (_updateStatus != null)
                {
                    _updateStatus.Text = "检查更新失败。\r\n" + Trim(result.Error, 120);
                }

                if (!silent)
                {
                    UpdateDialog.ShowWarning(this, "检查更新失败",
                        "没能连上更新服务器。\r\n\r\n" + Trim(result.Error, 320) +
                        "\r\n\r\n提示：老版本 Windows 7 如果从没更新过根证书，会连不上 HTTPS 站点，" +
                        "装一次 Windows Update 里的根证书更新即可。也可以直接到仓库的 Releases 页手动下载。");
                }
                return;
            }

            var manifest = result.Manifest;

            if (!result.HasUpdate)
            {
                if (_updateStatus != null)
                {
                    _updateStatus.Text = "已是最新版本（" + AppInfo.VersionText + "）\r\n" +
                                         "检查源：" + SourceName(result.Source);
                }

                if (!silent)
                {
                    UpdateDialog.ShowInfo(this, "已是最新版本",
                        "当前 " + AppInfo.VersionText + " 已经是仓库里的最新版本了。");
                }
                return;
            }

            if (_updateStatus != null)
            {
                _updateStatus.Text = "发现新版本 v" + manifest.Version + "\r\n检查源：" + SourceName(result.Source);
            }

            if (NoPrompts) return;      // 自动化测试：查到也不弹

            AppLog.Write("检查更新：发现新版本 v" + manifest.Version + "（当前 " + AppInfo.VersionText + "）");

            switch (UpdateDialog.Ask(this, manifest, SourceName(result.Source)))
            {
                case UpdateDialog.ChoiceNever:
                    AppRegistry.SetSkippedVersion(manifest.Version);
                    AppLog.Write("检查更新：用户选择「不再提示 v" + manifest.Version + "」");
                    if (_updateStatus != null) RefreshUpdateStatusLine();
                    break;

                case UpdateDialog.ChoiceUpdate:
                    BeginUpdate(manifest);
                    break;

                default:
                    AppLog.Write("检查更新：用户选择稍后再说");
                    break;
            }
        }

        /// <summary>下载升级包，然后交给升级程序接管，本程序退出。</summary>
        private void BeginUpdate(UpdateManifest manifest)
        {
            var installed = AppRegistry.IsInstalledAt(AppEnv.Root);
            var zip = Path.Combine(Path.GetTempPath(),
                string.IsNullOrEmpty(manifest.UpdateFile) ? AppInfo.UpdateFileName : manifest.UpdateFile);

            var downloaded = false;
            var error = string.Empty;

            using (var busy = new BusyDialog("正在下载更新", "正在连接下载升级包，请稍候…",
                       delegate (BusyDialog dialog)
                       {
                           string source;
                           string message;
                           downloaded = UpdateCheck.DownloadUpdate(manifest, zip, out source, out message);
                           if (downloaded)
                           {
                               dialog.SetStatus("下载完成（来自 " + SourceName(source) + "），准备应用更新…");
                           }
                           else
                           {
                               error = message;
                               dialog.SetStatus("下载失败。");
                           }
                       }))
            {
                busy.ShowDialog(this);
            }

            if (!downloaded)
            {
                UpdateDialog.ShowWarning(this, "更新失败",
                    "升级包下载失败。\r\n\r\n" + Trim(error, 320) +
                    "\r\n\r\n也可以到仓库的 Releases 页手动下载安装包。");
                return;
            }

            _pendingUpdateZip = zip;
            _pendingUpdateManifest = manifest;

            string stageError;
            if (!UpdateCheck.StageAndLaunch(zip, installed, out stageError))
            {
                UpdateDialog.ShowWarning(this, "更新失败", stageError);
                return;
            }

            AppLog.Write("更新：已把控制权交给升级程序，本程序退出。升级包=" + zip + " 安装版=" + installed);

            // 升级程序会等本进程退出后再覆盖文件，所以这里必须真的退出。
            Close();
        }

        private static string SourceName(string source)
        {
            if (string.Equals(source, "gitee", StringComparison.OrdinalIgnoreCase)) return "Gitee";
            if (string.Equals(source, "github", StringComparison.OrdinalIgnoreCase)) return "GitHub";
            return string.IsNullOrEmpty(source) ? "未知" : source;
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        // ============================================================
        //  小工具
        // ============================================================

        private string MapPayloadFor(string name, string mode)
        {
            // 直接复用服务端那一份：两边字段**必须完全一样**，
            // 否则"面板里点一下"和"HTTP 调一下"播出去的东西就会不一致。
            return _routes.MapPayload(name, mode);
        }

        private void AddTitle(string text)
        {
            var label = MakeLabel(text, 15f, Color.White);
            AddRow(label, 34);
        }

        private void AddSeparator()
        {
            var line = new Panel();
            line.BackColor = Theme.Separator;
            // 行距统一交给 AddRow，这里不用再设 Margin
            AddRow(line, 1);
        }

        private void AddRow(Control control, int height)
        {
            AddRow(control, height, _contentWidth);
        }

        /// <summary>
        /// 加一行。控件会被塞进一个左对齐的流式容器里：既能给窄控件
        /// （比如端口号最多 5 位数）单独指定宽度，又用 MaximumSize 把整行夹在
        /// 面板宽度以内 —— 内容永远不会比面板宽，也就永远不会出现横向滚动条。
        /// </summary>
        private void AddRow(Control control, int height, int width)
        {
            var row = MakeRowPanel(new Padding(0, 0, 0, 10));

            control.AutoSize = false;
            control.Width = width;
            control.Height = height;
            control.Margin = new Padding(0, 0, 0, 0);
            row.Controls.Add(control);

            _panelContent.Controls.Add(row);
        }

        /// <summary>统一的"一行"容器（横向排列、不换行、宽度受面板宽度约束）。</summary>
        private FlowLayoutPanel MakeRowPanel(Padding margin)
        {
            var row = new FlowLayoutPanel();
            row.FlowDirection = FlowDirection.LeftToRight;
            row.WrapContents = false;
            row.AutoSize = true;
            row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            row.BackColor = Theme.Panel;
            row.MaximumSize = new Size(_contentWidth, 0);
            row.Margin = margin;
            return row;
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

        private static Button MakeButton(string text, bool accent, bool small)
        {
            var button = new ModernButton();
            button.Text = text;
            button.BackColor = accent ? Theme.Accent : Theme.Button;
            button.ForeColor = Color.White;
            button.Font = Ui.Font(small ? 8.5f : 9.5f);
            button.Height = small ? 28 : 34;
            button.Cursor = Cursors.Hand;
            button.AutoSize = true;
            return button;
        }
    }
}
