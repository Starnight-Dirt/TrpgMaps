using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TrpgMaps
{
    // ============================================================
    //  网络地址枚举
    // ============================================================

    /// <summary>下拉栏里的一项：本机的一个 IPv4 地址。</summary>
    internal sealed class LocalAddress
    {
        public string Address;
        public string InterfaceName;
        public bool IsRecommended;

        public LocalAddress(string address, string interfaceName, bool isRecommended)
        {
            Address = address;
            InterfaceName = interfaceName;
            IsRecommended = isRecommended;
        }

        public string Display
        {
            get
            {
                return IsRecommended
                    ? Address + "  (" + InterfaceName + ")"
                    : Address + "  (" + InterfaceName + " · 不可用于局域网)";
            }
        }

        public override string ToString()
        {
            return Address;
        }
    }

    /// <summary>
    /// 枚举本机 IPv4 地址，供网络面板的下拉栏使用。
    /// 私有网段 + 物理网卡优先，虚拟网卡降权；明显不可用的地址不列出。
    /// </summary>
    internal static class IpEnumerator
    {
        public static List<LocalAddress> Enumerate()
        {
            var scored = new List<KeyValuePair<LocalAddress, int>>();

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    var isVirtual = IsVirtual(nic.Description);

                    foreach (var info in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                        var ip = info.Address.ToString();
                        var score = Score(nic, info.Address, isVirtual);
                        if (score < 0) continue;

                        scored.Add(new KeyValuePair<LocalAddress, int>(
                            new LocalAddress(ip, nic.Name, score >= 3), score));
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("枚举本机 IP 失败", ex);
            }

            if (scored.Count == 0)
            {
                scored.Add(new KeyValuePair<LocalAddress, int>(
                    new LocalAddress("127.0.0.1", "本机回环", false), 0));
            }

            var result = scored
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key.Address, StringComparer.Ordinal)
                .Select(p => p.Key)
                .ToList();

            // 去重（按地址）
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = new List<LocalAddress>();
            foreach (var item in result)
            {
                if (seen.Add(item.Address)) unique.Add(item);
            }
            return unique;
        }

        /// <summary>挑一个最适合局域网访问的地址。</summary>
        public static string PickRecommended()
        {
            var list = Enumerate();
            foreach (var item in list)
            {
                if (item.IsRecommended) return item.Address;
            }
            return list[0].Address;
        }

        private static int Score(NetworkInterface nic, IPAddress address, bool isVirtual)
        {
            var bytes = address.GetAddressBytes();

            if (IPAddress.IsLoopback(address)) return 0;

            // 169.254.x.x 链路本地地址（未接线的网卡）
            if (bytes[0] == 169 && bytes[1] == 254) return -1;

            var isPrivate = bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);

            int transport;
            switch (nic.NetworkInterfaceType)
            {
                case NetworkInterfaceType.Ethernet:
                case NetworkInterfaceType.GigabitEthernet:
                case NetworkInterfaceType.FastEthernetT:
                case NetworkInterfaceType.FastEthernetFx:
                    transport = 3;
                    break;
                case NetworkInterfaceType.Wireless80211:
                    transport = 2;
                    break;
                default:
                    transport = 1;
                    break;
            }

            var score = 0;
            if (isPrivate) score += 4;
            score += transport;
            if (isVirtual) score -= 5;
            if (!isPrivate) score -= 2;

            return Math.Max(score, 0);
        }

        private static readonly string[] VirtualMarkers =
        {
            "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "vbox",
            "docker", "wsl", "tap-", "tun", "wireguard", "openvpn", "zerotier",
            "tailscale", "loopback", "bluetooth", "蓝牙", "虚拟", "radmin", "hamachi"
        };

        private static bool IsVirtual(string description)
        {
            if (string.IsNullOrEmpty(description)) return false;
            var lower = description.ToLowerInvariant();
            foreach (var marker in VirtualMarkers)
            {
                if (lower.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }
    }

    // ============================================================
    //  服务器设置
    // ============================================================

    /// <summary>监听地址、端口、房间锁定状态。运行中可修改并重开服务。</summary>
    internal sealed class ServerSettings
    {
        /// <summary>命令行 --port 传入的端口覆盖（自动化测试用，避免撞上正在运行的实例）。</summary>
        public static int PortOverride;

        private readonly object _gate = new object();
        private string _listenIp;
        private int _port = AppEnv.DefaultPort;
        private bool _roomLocked;

        public ServerSettings()
        {
            _listenIp = IpEnumerator.PickRecommended();
            if (IsValidPort(PortOverride)) _port = PortOverride;
        }

        public string ListenIp
        {
            get { lock (_gate) { return _listenIp; } }
        }

        public int Port
        {
            get { lock (_gate) { return _port; } }
        }

        public bool RoomLocked
        {
            get { lock (_gate) { return _roomLocked; } }
            set { lock (_gate) { _roomLocked = value; } }
        }

        /// <summary>玩家访问地址。</summary>
        public string PlayerUrl
        {
            get { return "http://" + ListenIp + ":" + Port + "/player"; }
        }

        public static bool IsValidPort(int port)
        {
            return port >= 1 && port <= 65535;
        }

        /// <summary>更新监听参数；校验失败返回 false 并给出原因。</summary>
        public bool TryUpdate(string ip, int port, out string message)
        {
            if (string.IsNullOrEmpty(ip))
            {
                message = "请选择一个 IP 地址";
                return false;
            }
            if (!IsValidPort(port))
            {
                message = "端口必须在 1-65535 之间";
                return false;
            }

            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in IpEnumerator.Enumerate()) available.Add(item.Address);
            available.Add("127.0.0.1");
            if (!available.Contains(ip))
            {
                message = "本机当前没有地址 " + ip + "，请刷新后重新选择";
                return false;
            }

            lock (_gate)
            {
                _listenIp = ip;
                _port = port;
            }
            message = "已保存";
            return true;
        }
    }

    // ============================================================
    //  玩家名册
    // ============================================================

    /// <summary>玩家条目。以玩家名为主键（房间内名称唯一）。</summary>
    internal sealed class PlayerEntry
    {
        public string Name = string.Empty;
        public string Token = string.Empty;
        public string ConnectionId = string.Empty;
        public string Ip = string.Empty;
        public bool Online;
        public bool Kicked;
    }

    internal static class LoginCodes
    {
        public const string EmptyName = "EMPTY_NAME";
        public const string NameTaken = "NAME_TAKEN";
        public const string TokenMismatch = "TOKEN_MISMATCH";
        public const string RoomLocked = "ROOM_LOCKED";
    }

    internal sealed class LoginResult
    {
        public bool Success;
        public string Message = string.Empty;
        public string Code = string.Empty;
        public string Name = string.Empty;
        public string Token = string.Empty;
        public bool IsNewToken;
        public string DisplacedConnectionId;

        public static LoginResult Fail(string message, string code, string name)
        {
            return new LoginResult { Success = false, Message = message, Code = code, Name = name };
        }
    }

    // ============================================================
    //  底图信息
    // ============================================================

    internal sealed class MapInfo
    {
        public string Name;
        public string Path;
        public string Md5;
        public string Url;

        public MapInfo(string name, string path, string md5, string url)
        {
            Name = name;
            Path = path;
            Md5 = md5;
            Url = url;
        }
    }

    // ============================================================
    //  图片尺寸（只读文件头）
    // ============================================================

    /// <summary>读取图片像素尺寸，只解析文件头。支持 JPEG / PNG / GIF / BMP / WebP。</summary>
    internal static class ImageSizeReader
    {
        public sealed class Size
        {
            public int Width;
            public int Height;
            public Size(int w, int h) { Width = w; Height = h; }
        }

        public static Size Read(string path)
        {
            try
            {
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var header = new byte[32];
                    var read = stream.Read(header, 0, header.Length);
                    if (read < 2) return null;
                    stream.Position = 0;

                    if (IsPng(header)) return ReadPng(stream);
                    if (IsGif(header)) return ReadGif(header);
                    if (IsBmp(header)) return ReadBmp(header);
                    if (IsWebp(header)) return ReadWebp(stream);
                    if (IsJpeg(header)) return ReadJpeg(stream);
                    return null;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("读取图片尺寸失败：" + path, ex);
                return null;
            }
        }

        private static bool IsPng(byte[] h)
        {
            return h.Length >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47;
        }

        private static bool IsGif(byte[] h)
        {
            return h.Length >= 6 && h[0] == (byte)'G' && h[1] == (byte)'I' && h[2] == (byte)'F';
        }

        private static bool IsBmp(byte[] h)
        {
            return h.Length >= 2 && h[0] == (byte)'B' && h[1] == (byte)'M';
        }

        private static bool IsWebp(byte[] h)
        {
            return h.Length >= 12 && h[0] == (byte)'R' && h[1] == (byte)'I' && h[2] == (byte)'F' && h[3] == (byte)'F'
                   && h[8] == (byte)'W' && h[9] == (byte)'E' && h[10] == (byte)'B' && h[11] == (byte)'P';
        }

        private static bool IsJpeg(byte[] h)
        {
            return h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF;
        }

        private static Size ReadPng(Stream s)
        {
            s.Position = 16; // IHDR 宽高
            var buf = new byte[8];
            if (s.Read(buf, 0, 8) < 8) return null;
            var w = (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
            var h = (buf[4] << 24) | (buf[5] << 16) | (buf[6] << 8) | buf[7];
            return w > 0 && h > 0 ? new Size(w, h) : null;
        }

        private static Size ReadGif(byte[] h)
        {
            var w = h[6] | (h[7] << 8);
            var ht = h[8] | (h[9] << 8);
            return w > 0 && ht > 0 ? new Size(w, ht) : null;
        }

        private static Size ReadBmp(byte[] h)
        {
            var w = BitConverter.ToInt32(h, 18);
            var ht = BitConverter.ToInt32(h, 22);
            if (ht < 0) ht = -ht;
            return w > 0 && ht > 0 ? new Size(w, ht) : null;
        }

        private static Size ReadWebp(Stream s)
        {
            // 从 12 开始是第一个 RIFF 块。四种 WebP 的头部布局不一样，别互相抄下标：
            //   "VP8 "(有损)  chunk[0..3] 四字符码，[4..7] 块长度，
            //                 [8..10] 帧标签，[11..13] 同步码 9D 01 2A，
            //                 [14..15] 宽（14 位小端，低字节在前），[16..17] 高
            //   "VP8L"(无损)  [8] 签名 0x2F，[9..12] 打包的 (宽-1) | (高-1)<<14
            //   "VP8X"(扩展)  [8..11] 标志+保留，[12..14] 画布宽-1，[15..17] 画布高-1
            //
            // ⚠️ 有损那一支踩过坑：曾经写成 chunk[13] | (chunk[12] << 8) 这种
            // "从 12 起 + 高低字节写反"的组合，读到的其实是同步码 01 2A 两个字节，
            // 于是 1672x941 的图被报成 298x2054 —— 解码本身没问题，但
            // ComputeImageRect() 会按这个错误尺寸去算适配比例，底图被拉成一长条，
            // 锚点（手机端网格定位）也跟着全错。自检里那条"解码尺寸 == 文件头尺寸"
            // 的断言就是钉这个的。
            s.Position = 12;
            var chunk = new byte[18];
            if (s.Read(chunk, 0, chunk.Length) < 18) return null;

            if (chunk[0] == (byte)'V' && chunk[1] == (byte)'P' && chunk[2] == (byte)'8' && chunk[3] == (byte)'X')
            {
                var w = (chunk[12] | (chunk[13] << 8) | (chunk[14] << 16)) + 1;
                var h = (chunk[15] | (chunk[16] << 8) | (chunk[17] << 16)) + 1;
                return new Size(w, h);
            }
            if (chunk[0] == (byte)'V' && chunk[1] == (byte)'P' && chunk[2] == (byte)'8' && chunk[3] == (byte)' ')
            {
                var w = (chunk[14] | (chunk[15] << 8)) & 0x3FFF;
                var h = (chunk[16] | (chunk[17] << 8)) & 0x3FFF;
                return w > 0 && h > 0 ? new Size(w, h) : null;
            }
            if (chunk[0] == (byte)'V' && chunk[1] == (byte)'P' && chunk[2] == (byte)'8' && chunk[3] == (byte)'L')
            {
                var bits = chunk[9] | (chunk[10] << 8) | (chunk[11] << 16) | (chunk[12] << 24);
                var w = (bits & 0x3FFF) + 1;
                var h = ((bits >> 14) & 0x3FFF) + 1;
                return new Size(w, h);
            }
            return null;
        }

        private static Size ReadJpeg(Stream s)
        {
            s.Position = 2;
            while (s.Position < s.Length - 4)
            {
                var b = s.ReadByte();
                if (b != 0xFF) continue;

                var marker = s.ReadByte();
                while (marker == 0xFF) marker = s.ReadByte();
                if (marker < 0) break;

                // 无长度字段的标记
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;

                var lenHi = s.ReadByte();
                var lenLo = s.ReadByte();
                if (lenHi < 0 || lenLo < 0) break;
                var length = (lenHi << 8) | lenLo;
                if (length < 2) break;

                var isSof = marker >= 0xC0 && marker <= 0xCF &&
                            marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (isSof)
                {
                    var payload = new byte[5];
                    if (s.Read(payload, 0, payload.Length) < payload.Length) break;
                    var h = (payload[1] << 8) | payload[2];
                    var w = (payload[3] << 8) | payload[4];
                    return w > 0 && h > 0 ? new Size(w, h) : null;
                }

                s.Position += length - 2;
            }
            return null;
        }
    }
}
