using System;
using System.Globalization;

namespace TrpgMaps
{
    /// <summary>
    /// 「网格 ↔ 底图」的几何绑定快照。**一旦构造好就只读**（字段全 readonly）：
    /// 它会被 HTTP 线程拿去生成下发给手机端的载荷，而重算发生在 UI 线程，
    /// 靠"整体换引用"而不是"改字段"来同步，才不会有半新半旧的读数。
    ///
    /// 为什么需要这份东西：
    /// 电脑端和手机端的画布尺寸差一个数量级（投屏 1920 宽 vs 手机 390 宽），
    /// 两边各画各的网格、各按自己的坐标铺绘图，贴到手机上必然错位 ——
    /// 因为电脑端的网格是钉在**画布左上角**的，底图却是**居中**摆放的，
    /// 于是"第 x 格"这个坐标在两端指向底图上的不同位置。
    ///
    /// 解决方式是把这份几何抽成 4 个与分辨率无关的数，随
    /// <c>map_change</c> / <c>api/request_map</c> 一起下发，手机端照着重放。
    /// 手机那一格多大无所谓，**位置对得上**就行。
    ///
    /// 锚点是**底图中心**（底图在两端都居中，所以底图中心 = 画布中心）：
    ///  * <see cref="CellsX"/> / <see cref="CellsY"/> —— 底图按当前显示模式的尺寸
    ///    横 / 纵向能放几格；
    ///  * <see cref="HalfCellsW"/> / <see cref="HalfCellsH"/> —— 画布半宽 / 半高是几格，
    ///    也就是"画布左上角那一点，相对底图中心偏了几个格"。
    ///
    /// 手机端的复原公式（见 wwwroot/js/player.js）：
    /// <code>
    ///   cellW = 底图显示宽 / CellsX           // 手机上"一格"多少像素
    ///   cellH = 底图显示高 / CellsY
    ///   网格原点(电脑屏幕 (0,0) 那一点) 相对底图中心 = (-HalfCellsW*cellW, -HalfCellsH*cellH)
    ///   第 k 条竖线 = 原点上 k*cellW；第 (x,y) 格 = 原点 + (x*cellW, y*cellH) 起的一格
    /// </code>
    /// </summary>
    internal sealed class GridAnchor
    {
        public GridAnchor(double cellsX, double cellsY, double halfCellsW, double halfCellsH)
        {
            CellsX = cellsX;
            CellsY = cellsY;
            HalfCellsW = halfCellsW;
            HalfCellsH = halfCellsH;
            Valid = cellsX > 0.01 && cellsY > 0.01 && halfCellsW > 0.0 && halfCellsH > 0.0;
        }

        /// <summary>底图显示宽度 ÷ 一格像素 = 横向能放几格（可以是小数）。</summary>
        public readonly double CellsX;

        /// <summary>底图显示高度 ÷ 一格像素 = 纵向能放几格。</summary>
        public readonly double CellsY;

        /// <summary>画布半宽 ÷ 一格像素。</summary>
        public readonly double HalfCellsW;

        /// <summary>画布半高 ÷ 一格像素。</summary>
        public readonly double HalfCellsH;

        /// <summary>数值可用吗（画布还没有尺寸时构造出来的那份是 false）。</summary>
        public readonly bool Valid;

        /// <summary>一行短描述，写进日志 / 自检用。</summary>
        public string Describe()
        {
            if (!Valid) return "(未就绪)";
            var c = CultureInfo.InvariantCulture;
            return "底图 " + CellsX.ToString("0.###", c) + " x " + CellsY.ToString("0.###", c) + " 格"
                 + "  画布半宽 " + HalfCellsW.ToString("0.###", c) + " 格"
                 + "  半高 " + HalfCellsH.ToString("0.###", c) + " 格";
        }
    }
}
