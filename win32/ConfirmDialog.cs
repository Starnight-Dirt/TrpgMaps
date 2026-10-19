using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TrpgMaps
{
    /// <summary>
    /// 统一样式的确认弹窗：深色圆角面板、**蓝底白字的确认** + **灰底白字的取消**。
    ///
    /// 为什么不用 MessageBox：系统弹窗在 Win7 / Win10 上长相完全不同（Aero vs 平面），
    /// 而且按钮文字是"是/否"这种固定词，写不出「保留 / 取消」这种语感。
    ///
    /// 两种用法：
    ///   * <see cref="Ask"/> —— 普通的"要不要"（关闭程序走这个）；
    ///   * <see cref="AskWithCountdown"/> —— 带倒计时的"不操作就回退"（语言切换走这个）。
    /// </summary>
    internal sealed class ConfirmDialog : Form
    {
        private const int Pad = 22;

        private readonly Label _message;
        private readonly Label _countdownLabel;
        private readonly Timer _timer;
        private readonly string _countdownFormat;
        private readonly string _countdownArg;
        private int _seconds;
        private bool _confirmed;

        private ConfirmDialog(string title, string message, string confirmText, string cancelText,
            int seconds, string countdownFormat, string countdownArg)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Font = Ui.Font(9f);
            ClientSize = new Size(440, 200);

            var titleLabel = new Label();
            titleLabel.Text = title;
            titleLabel.Font = Ui.Font(13f, FontStyle.Bold);
            titleLabel.ForeColor = Color.White;
            titleLabel.BackColor = Color.Transparent;
            titleLabel.AutoSize = false;
            titleLabel.SetBounds(Pad, 18, ClientSize.Width - Pad * 2, 28);
            Controls.Add(titleLabel);

            _message = new Label();
            _message.Text = message;
            _message.ForeColor = Theme.TextDim;
            _message.BackColor = Color.Transparent;
            _message.AutoSize = false;
            _message.SetBounds(Pad, 52, ClientSize.Width - Pad * 2, 62);
            Controls.Add(_message);

            _countdownLabel = new Label();
            _countdownLabel.Text = string.Empty;
            _countdownLabel.ForeColor = Theme.Link;
            _countdownLabel.BackColor = Color.Transparent;
            _countdownLabel.AutoSize = false;
            _countdownLabel.SetBounds(Pad, 116, ClientSize.Width - Pad * 2, 20);
            Controls.Add(_countdownLabel);

            var cancel = MakeButton(cancelText, false);
            cancel.SetBounds(ClientSize.Width - Pad - 108, 152, 108, 32);
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);

            var confirm = MakeButton(confirmText, true);
            confirm.SetBounds(ClientSize.Width - Pad - 108 - 116, 152, 108, 32);
            confirm.Click += delegate
            {
                _confirmed = true;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(confirm);

            AcceptButton = confirm;
            CancelButton = cancel;

            _countdownFormat = countdownFormat;
            _countdownArg = countdownArg;
            _seconds = seconds;

            if (seconds > 0)
            {
                _timer = new Timer();
                _timer.Interval = 1000;
                _timer.Tick += delegate
                {
                    _seconds--;
                    if (_seconds <= 0)
                    {
                        _timer.Stop();
                        Close();                // 没点确认 = 回退
                        return;
                    }
                    UpdateCountdown();
                };
                UpdateCountdown();
                // ⚠️ 别忘了 Start()：少了这一句，秒数会**停在初始值**（15 一直不变），
                // 倒计时结束也不会自动关窗 —— 看起来就像"15 秒写死了"。
                _timer.Start();
            }

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
        }

        private void UpdateCountdown()
        {
            if (_countdownLabel == null || string.IsNullOrEmpty(_countdownFormat)) return;
            try
            {
                _countdownLabel.Text = string.Format(_countdownFormat, _seconds, _countdownArg);
            }
            catch (FormatException)
            {
                _countdownLabel.Text = _countdownFormat;
            }
        }

        private static Button MakeButton(string text, bool accent)
        {
            var button = new ModernButton();
            button.Text = text;
            button.BackColor = accent ? Theme.Accent : Theme.Button;
            button.ForeColor = Color.White;      // 蓝底白字 / 灰底白字
            button.Font = Ui.Font(9.5f);
            return button;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyRoundedRegion();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyRoundedRegion();
        }

        /// <summary>
        /// 圆角靠 Region 裁 —— 必须在**句柄创建之后**和每次 Resize 都设一遍，
        /// 否则窗口一改大小，圆角就变回直角（安装器那边踩过同样的坑）。
        /// </summary>
        private void ApplyRoundedRegion()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
            using (var path = Theme.RoundedRect(new RectangleF(0, 0, Width, Height), 12f))
            {
                var old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Panel);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 12f))
            using (var pen = new Pen(Theme.Separator, 1f))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
            }
            base.OnFormClosed(e);
        }

        /// <summary>
        /// 自检用：真的让计时器跑两秒半，看倒计时那行字有没有**跟着秒数变**。
        ///
        /// 为什么非要跑真时钟：这条 bug 不是"格式写错"，而是 `Timer` 忘了 `Start()` ——
        /// 秒数会一直停在初始值、到点也不关窗。只调一次 UpdateCountdown 是测不出来的，
        /// 必须让它自己走几拍。不显示窗口，只 `DoEvents` 抽消息。
        /// </summary>
        internal static bool ProbeCountdown(int seconds, out string first, out string later)
        {
            using (var dialog = new ConfirmDialog("probe", "probe", "ok", "cancel",
                       seconds, "{0} / {1}", "X"))
            {
                first = dialog._countdownLabel.Text;

                var until = Environment.TickCount + 2500;
                while (Environment.TickCount < until)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(30);
                }

                later = dialog._countdownLabel.Text;
                return !string.IsNullOrEmpty(first) && first != later;
            }
        }

        /// <summary>普通确认。返回 true = 点了确认。</summary>
        public static bool Ask(IWin32Window owner, string title, string message,
            string confirmText, string cancelText)
        {
            using (var dialog = new ConfirmDialog(title, message, confirmText, cancelText,
                       0, null, null))
            {
                dialog.ShowDialog(owner);
                return dialog._confirmed;
            }
        }

        /// <summary>
        /// 带倒计时的确认：**不点确认**（超时或点取消）就算回退。
        /// <paramref name="countdownFormat"/> 里 {0} = 剩余秒数，{1} = <paramref name="countdownArg"/>。
        /// </summary>
        public static bool AskWithCountdown(IWin32Window owner, string title, string message,
            string confirmText, string cancelText, int seconds,
            string countdownFormat, string countdownArg)
        {
            using (var dialog = new ConfirmDialog(title, message, confirmText, cancelText,
                       seconds, countdownFormat, countdownArg))
            {
                dialog.ShowDialog(owner);
                return dialog._confirmed;
            }
        }
    }
}
