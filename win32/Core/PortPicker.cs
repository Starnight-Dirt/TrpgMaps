using System;
using System.Collections.Generic;

namespace TrpgMaps
{
    /// <summary>
    /// 挑一个**真正绑得上**的端口。
    ///
    /// 为什么需要它：默认端口绑不上有两种原因，而它们**长得一模一样**，程序自己分不出来：
    ///   ① 被别的程序占着（WSAEADDRINUSE / 10048）；
    ///   ② 被 **Windows 自己**占着（WSAEACCES / 10013）—— 系统给 Hyper-V / WSL / Docker 的
    ///      `winnat` 预留了一整段端口（`netsh int ipv4 show excludedportrange protocol=tcp`
    ///      能查），那段里的端口**任何程序都绑不上**，而且**没有占用者可以杀**。
    ///      用户看到"以一种访问权限不允许的方式做了一个访问套接字的尝试"就懵了 ——
    ///      其实这不是"端口冲突"，换端口才是唯一出路。
    ///
    /// 所以这里不去分辨原因，**只要绑不上就换下一个**。
    /// 候选顺序：用户指定的那个 → 顺着往后试几个（顺延的结果好预测、好念给玩家听）
    /// → 最后随机撒网（应对"整段都被预留"这种极端情况）。
    /// </summary>
    internal static class PortPicker
    {
        /// <summary>低于 1024 要管理员权限，不碰。</summary>
        public const int MinPort = 1024;

        /// <summary>
        /// 49152 及以上是 Windows 的动态 / 临时端口范围（`winnat` 的预留也都在那附近），
        /// 监听端口避开它，免得和系统随手分出去的临时连接撞车。
        /// </summary>
        public const int MaxPort = 49151;

        /// <summary>顺着往后试几个（`port+0` … `port+9`）。</summary>
        public const int SequentialTries = 10;

        /// <summary>随机撒网的次数。</summary>
        public const int RandomTries = 60;

        /// <summary>
        /// 候选端口序列。**纯函数、不碰网络**，所以自检里可以直接断言顺序。
        /// </summary>
        /// <param name="preferred">最想用的那个（不在合法区间里就退回到默认端口）。</param>
        /// <param name="randomTries">顺延试完还不行时再随机试几次（&lt;=0 = 不随机）。</param>
        public static IEnumerable<int> Candidates(int preferred, int randomTries)
        {
            if (preferred < MinPort || preferred > MaxPort) preferred = AppEnv.DefaultPort;

            var seen = new HashSet<int>();
            for (var i = 0; i < SequentialTries && preferred + i <= MaxPort; i++)
            {
                var p = preferred + i;
                if (seen.Add(p)) yield return p;
            }

            if (randomTries <= 0) yield break;

            // 种子掺上 preferred：同一台机器上换端口重跑，随机序列也不会每次都一样。
            var rnd = new Random(unchecked(Environment.TickCount * 31 + preferred));
            for (var i = 0; i < randomTries; i++)
            {
                var p = rnd.Next(MinPort, MaxPort + 1);
                if (seen.Add(p)) yield return p;
            }
        }

        /// <summary>默认那一串候选（顺延 10 个 + 随机 60 个）。</summary>
        public static IEnumerable<int> Candidates(int preferred)
        {
            return Candidates(preferred, RandomTries);
        }

        /// <summary>
        /// 判断一个异常是不是"这个端口用不了"。
        ///
        /// 只用来**写日志 / 给界面提示**（决定要不要换个端口是另一回事 —— 那边一律照换）。
        /// </summary>
        public static bool IsPortBlocked(Exception ex, out string hint)
        {
            hint = null;
            var socket = ex as System.Net.Sockets.SocketException;
            if (socket == null)
            {
                var inner = ex == null ? null : ex.InnerException as System.Net.Sockets.SocketException;
                socket = inner;
            }
            if (socket == null) return false;

            // 10013 = WSAEACCES（系统保留 / 权限），10048 = WSAEADDRINUSE（已被占用）
            if (socket.ErrorCode == 10013)
            {
                hint = Loc.T("core.port.reserved");
                return true;
            }
            if (socket.ErrorCode == 10048)
            {
                hint = Loc.T("core.port.used");
                return true;
            }
            return false;
        }
    }
}
