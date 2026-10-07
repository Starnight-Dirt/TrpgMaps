using System;
using System.Drawing;
using System.Windows.Forms;

namespace TrpgMaps
{
    /// <summary>
    /// 更新提示框。用自绘的 <see cref="ModernButton"/>，所以三个按钮的文字
    /// 可以按需要写 —— `MessageBox` 只能给"是/否/取消"那种固定词，
    /// 写不出「下次一定」「不再提示」。
    ///
    /// 返回值：
    ///   0 = 更新   1 = 下次一定   2 = 不再提示
    /// </summary>
    internal sealed class UpdateDialog : Form
    {
        public const int ChoiceUpdate = 0;
        public const int ChoiceLater = 1;
        public const int ChoiceNever = 2;

        private int _choice = ChoiceLater;

        private UpdateDialog(UpdateManifest manifest, string source)
        {
            Text = "发现新版本";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Font = Ui.Font(9f);
            ClientSize = new Size(470, 252);

            var title = new Label();
            title.Text = "发现新版本";
            title.Font = Ui.Font(13f, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.BackColor = Color.Transparent;
            title.AutoSize = false;
            title.SetBounds(20, 18, 430, 28);
            Controls.Add(title);

            var versions = new Label();
            versions.Text = "当前版本 " + AppInfo.VersionText + "   →   最新版本 v" + manifest.Version;
            versions.ForeColor = Theme.Success;
            versions.BackColor = Color.Transparent;
            versions.AutoSize = false;
            versions.SetBounds(20, 50, 430, 22);
            Controls.Add(versions);

            var notes = new Label();
            notes.Text = string.IsNullOrEmpty(manifest.Notes)
                ? "仓库里有比本机更新的版本，建议更新到最新版。"
                : manifest.Notes;
            notes.ForeColor = Theme.TextDim;
            notes.BackColor = Color.Transparent;
            notes.AutoSize = false;
            notes.SetBounds(20, 78, 430, 62);
            Controls.Add(notes);

            var hint = new Label();
            hint.Text = "更新来源：" + (string.IsNullOrEmpty(source) ? "自动" : source) +
                        "　　升级包会从发布页（releases）下载。\r\n" +
                        "便携版只覆盖程序文件；安装版还会顺带修正注册信息。";
            hint.ForeColor = Theme.TextDim;
            hint.BackColor = Color.Transparent;
            hint.AutoSize = false;
            hint.SetBounds(20, 144, 430, 44);
            Controls.Add(hint);

            var never = MakeButton("不再提示", false);
            never.SetBounds(20, 202, 104, 32);
            never.Click += delegate { _choice = ChoiceNever; DialogResult = DialogResult.OK; };
            Controls.Add(never);

            var later = MakeButton("下次一定", false);
            later.SetBounds(238, 202, 104, 32);
            later.Click += delegate { _choice = ChoiceLater; DialogResult = DialogResult.OK; };
            Controls.Add(later);

            var update = MakeButton("更新", true);
            update.SetBounds(350, 202, 100, 32);
            update.Click += delegate { _choice = ChoiceUpdate; DialogResult = DialogResult.OK; };
            Controls.Add(update);

            AcceptButton = update;
            CancelButton = later;
        }

        private static Button MakeButton(string text, bool accent)
        {
            var button = new ModernButton();
            button.Text = text;
            button.BackColor = accent ? Theme.Accent : Theme.Button;
            button.ForeColor = Color.White;
            button.Font = Ui.Font(9.5f);
            return button;
        }

        /// <summary>弹窗询问。返回上面那三个常量之一。</summary>
        public static int Ask(IWin32Window owner, UpdateManifest manifest, string source)
        {
            using (var dialog = new UpdateDialog(manifest, source))
            {
                dialog.ShowDialog(owner);
                return dialog._choice;
            }
        }

        /// <summary>只是通知一声的弹窗（已是最新、检查失败等）。</summary>
        public static void ShowInfo(IWin32Window owner, string title, string text)
        {
            using (var dialog = new NoticeDialog(title, text, false))
            {
                dialog.ShowDialog(owner);
            }
        }

        public static void ShowWarning(IWin32Window owner, string title, string text)
        {
            using (var dialog = new NoticeDialog(title, text, true))
            {
                dialog.ShowDialog(owner);
            }
        }
    }

    /// <summary>统一样式的简单提示框（替代 MessageBox，免得弹出来是个亮色系统框）。</summary>
    internal sealed class NoticeDialog : Form
    {
        public NoticeDialog(string title, string text, bool warning)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Font = Ui.Font(9f);
            ClientSize = new Size(420, 170);

            var heading = new Label();
            heading.Text = title;
            heading.Font = Ui.Font(12f, FontStyle.Bold);
            heading.ForeColor = warning ? Theme.Error : Color.White;
            heading.BackColor = Color.Transparent;
            heading.AutoSize = false;
            heading.SetBounds(20, 16, 380, 26);
            Controls.Add(heading);

            var body = new Label();
            body.Text = text;
            body.ForeColor = Theme.TextDim;
            body.BackColor = Color.Transparent;
            body.AutoSize = false;
            body.SetBounds(20, 48, 380, 72);
            Controls.Add(body);

            var ok = new ModernButton();
            ok.Text = "知道了";
            ok.BackColor = Theme.Accent;
            ok.ForeColor = Color.White;
            ok.Font = Ui.Font(9.5f);
            ok.SetBounds(300, 122, 100, 32);
            ok.Click += delegate { DialogResult = DialogResult.OK; };
            Controls.Add(ok);

            AcceptButton = ok;
            CancelButton = ok;
        }
    }

    /// <summary>
    /// 模态等待框：把一件耗时的活儿放到后台线程做，做完自己关掉。
    ///
    /// 活儿是用 <see cref="Shown"/> 事件启动的（而不是构造函数里）——
    /// 构造期窗口句柄还没建出来，后台线程那时候调 `BeginInvoke` 会直接抛。
    /// 工作线程只通过 <see cref="SetStatus"/> 回 UI 线程改字，其余时间不碰控件。
    /// </summary>
    internal sealed class BusyDialog : Form
    {
        private readonly Label _status;
        private readonly Action<BusyDialog> _worker;

        public BusyDialog(string title, string initialStatus, Action<BusyDialog> worker)
        {
            _worker = worker;

            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Font = Ui.Font(9f);
            ClientSize = new Size(420, 132);

            var heading = new Label();
            heading.Text = title;
            heading.Font = Ui.Font(11f, FontStyle.Bold);
            heading.ForeColor = Color.White;
            heading.BackColor = Color.Transparent;
            heading.AutoSize = false;
            heading.SetBounds(20, 18, 380, 24);
            Controls.Add(heading);

            _status = new Label();
            _status.Text = initialStatus;
            _status.ForeColor = Theme.TextDim;
            _status.BackColor = Color.Transparent;
            _status.AutoSize = false;
            _status.SetBounds(20, 50, 380, 44);
            Controls.Add(_status);

            var bar = new ProgressBar();
            bar.Style = ProgressBarStyle.Marquee;
            bar.MarqueeAnimationSpeed = 30;
            bar.SetBounds(20, 104, 380, 8);
            Controls.Add(bar);

            Shown += delegate
            {
                if (_worker == null) { RequestClose(); return; }
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        _worker(this);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("后台任务出错", ex);
                        SetStatus("出错了：" + ex.Message);
                    }
                    finally
                    {
                        RequestClose();
                    }
                });
            };
        }

        /// <summary>从任意线程调用都安全。</summary>
        public void SetStatus(string text)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!IsDisposed) _status.Text = text;
                });
            }
            catch
            {
                // 窗口已经关了，忽略
            }
        }

        /// <summary>让后台线程请求关闭。句柄还没建出来时什么都不做（那时也没显示过）。</summary>
        public void RequestClose()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!IsDisposed) Close();
                });
            }
            catch
            {
                // 忽略
            }
        }
    }
}
