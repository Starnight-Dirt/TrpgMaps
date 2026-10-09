using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ProbeClick
{
    // 复刻 SetupUi.CapsuleButton 的最小形态：继承 Control，
    // 自己实现 OnMouseUp -> OnClick。
    internal sealed class MiniButton : Control
    {
        private bool _pressed;
        private readonly bool _fix;

        public MiniButton(bool fix)
        {
            _fix = fix;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable |
                     ControlStyles.SupportsTransparentBackColor, true);

            if (_fix)
            {
                SetStyle(ControlStyles.StandardClick, false);
                SetStyle(ControlStyles.StandardDoubleClick, false);
            }

            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            _pressed = true;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            var was = _pressed;
            _pressed = false;
            if (was && ClientRectangle.Contains(e.Location))
            {
                OnClick(EventArgs.Empty);
            }
        }
    }

    internal static class Program
    {
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int MK_LBUTTON = 0x0001;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [STAThread]
        private static int Main()
        {
            var before = CountClicks(false);
            var after = CountClicks(true);

            Console.WriteLine("BEFORE fix : CLICK_COUNT=" + before + "  (expect 2 = bug present)");
            Console.WriteLine("AFTER  fix : CLICK_COUNT=" + after + "  (expect 1 = fixed)");

            var ok = (before == 2 && after == 1);
            Console.WriteLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            return ok ? 0 : 1;
        }

        private static int CountClicks(bool fix)
        {
            var count = 0;
            using (var form = new Form())
            using (var button = new MiniButton(fix))
            {
                form.ClientSize = new Size(300, 200);
                button.SetBounds(50, 50, 100, 40);
                button.Click += delegate { count++; };
                form.Controls.Add(button);

                form.Show();
                Application.DoEvents();

                var lparam = (IntPtr)((20 << 16) | 50);   // y=20, x=50
                SendMessage(button.Handle, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lparam);
                Application.DoEvents();
                SendMessage(button.Handle, WM_LBUTTONUP, IntPtr.Zero, lparam);
                Application.DoEvents();

                form.Hide();
            }
            return count;
        }
    }
}
