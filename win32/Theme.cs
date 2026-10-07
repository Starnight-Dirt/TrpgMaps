using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace TrpgMaps
{
    /// <summary>配色与绘图小工具（与原 Python 版 dm.css 对齐）。</summary>
    internal static class Theme
    {
        public static readonly Color Background = FromHex("#1A1A1A");
        public static readonly Color Sidebar = FromHex("#333333");
        public static readonly Color Button = FromHex("#444444");
        public static readonly Color ButtonHover = FromHex("#555555");
        public static readonly Color Panel = FromHex("#2C2C2C");
        public static readonly Color Card = FromHex("#3A3A3A");
        public static readonly Color Text = FromHex("#EEEEEE");
        public static readonly Color TextDim = FromHex("#AAAAAA");
        public static readonly Color Accent = FromHex("#4A6A8A");
        public static readonly Color Track = FromHex("#3A3A3A");
        public static readonly Color Separator = FromHex("#555555");
        public static readonly Color Link = FromHex("#8AB4D8");
        public static readonly Color Success = FromHex("#9CC79C");
        /// <summary>
        /// "在线"指示灯的亮绿。刻意比 <see cref="Success"/> 亮得多：
        /// Success 是给文字用的柔和绿，而在线圆点只有 8px，摆在深灰底上
        /// 用柔和绿根本看不出亮/灭的区别。
        /// </summary>
        public static readonly Color Online = FromHex("#00E676");
        public static readonly Color Error = FromHex("#E0A0A0");
        public static readonly Color Locked = FromHex("#A05A2C");
        public static readonly Color Danger = FromHex("#A04040");

        public static Color FromHex(string hex)
        {
            var s = hex.TrimStart('#');
            return Color.FromArgb(
                int.Parse(s.Substring(0, 2), NumberStyles.HexNumber),
                int.Parse(s.Substring(2, 2), NumberStyles.HexNumber),
                int.Parse(s.Substring(4, 2), NumberStyles.HexNumber));
        }

        /// <summary>构造圆角矩形路径。</summary>
        public static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0.1f)
            {
                path.AddRectangle(rect);
                return path;
            }

            var d = radius * 2f;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;

            path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        /// <summary>只给右侧两个角做圆角（浮空面板用）。</summary>
        public static GraphicsPath RightRoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            path.AddLine(rect.Left, rect.Top, rect.Right - radius, rect.Top);
            path.AddArc(rect.Right - radius * 2f, rect.Top, radius * 2f, radius * 2f, 270f, 90f);
            path.AddLine(rect.Right, rect.Top + radius, rect.Right, rect.Bottom - radius);
            path.AddArc(rect.Right - radius * 2f, rect.Bottom - radius * 2f, radius * 2f, radius * 2f, 0f, 90f);
            path.AddLine(rect.Right - radius, rect.Bottom, rect.Left, rect.Bottom);
            path.CloseFigure();
            return path;
        }
    }
}
