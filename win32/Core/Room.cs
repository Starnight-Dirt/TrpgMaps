using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TrpgMaps
{
    /// <summary>
    /// 房间相关的新错误码（玩家端按 code 分支，字符串不能随手改）。
    /// 「名称为空 / 令牌不匹配」这两个沿用 <see cref="LoginCodes"/>，不在这里再抄一份。
    /// </summary>
    internal static class RoomCodes
    {
        /// <summary>地址里的房间号已经不是当前房间（DM 重启会换一个新号）。</summary>
        public const string Gone = "ROOM_GONE";

        /// <summary>房间设了密码，但玩家给的密码不对（或没给）。</summary>
        public const string BadPassword = "BAD_PASSWORD";

        /// <summary>名册里没有这名玩家（已经退出过了 / 从没进来过）。</summary>
        public const string NotFound = "NOT_FOUND";
    }

    /// <summary>
    /// 一次「退出房间」请求的结果。
    /// </summary>
    internal sealed class LeaveResult
    {
        public bool Success;
        public string Code = string.Empty;
        public string Message = string.Empty;
        public string Name = string.Empty;

        /// <summary>被删掉的那名玩家的连接（SSE 订阅 id），留给调用方决定要不要通知它。</summary>
        public string ConnectionId;

        public static LeaveResult Fail(string code, string message)
        {
            return new LeaveResult { Success = false, Code = code, Message = message };
        }
    }

    /// <summary>
    /// 房间：**8 位随机房间号** + 可选密码。
    ///
    /// 为什么要有房间号：玩家的身份（token）只对**某一个房间**有效。
    /// 以前地址就是 <c>ip:port/player</c>，DM 重启之后同一个地址还是"同一个房间"，
    /// 玩家 cookie 里的 token 看着像是还有效；换成"每次启动换一个新房间号"之后，
    /// 玩家端只需要比对"cookie 里的房间号"和"地址栏里的房间号"，就能立刻分辨
    /// **"这个房间已经换人了"**，从而先向旧房间告辞、清掉 token 再以新玩家加入。
    ///
    /// 「无冲突」= 不跟上一次（以及最近几次）用过的房间号重复。号码本身是 8 位
    /// （1 亿 种），只要避开刚刚那个，玩家手里那张旧 cookie 就不会"撞"上新房间。
    ///
    /// 持久化在 exe 旁边的 <c>room.json</c>：房间号每次启动都新生成，但
    /// **密码是 DM 设置的，要留着**（不然每次开机都得重设一遍）。
    /// 文件丢了也能跑：读不到就当"没配过"，用一个新号码。
    /// </summary>
    internal sealed class RoomState
    {
        /// <summary>密码长度上限。够用了，也免得有人在输入框里贴一整篇文章进来。</summary>
        public const int MaxPasswordLength = 32;

        private const int RecentKeep = 5;

        private readonly object _gate = new object();
        private readonly string _path;

        private string _number = string.Empty;
        private string _password = string.Empty;
        private readonly List<string> _recent = new List<string>();

        public RoomState()
            : this(DefaultPath())
        {
        }

        /// <summary>指定持久化文件（自检用临时路径，免得动到真配置）。</summary>
        public RoomState(string path)
        {
            _path = path;
            Load();

            // 每次启动都换一个新号 —— 这就是"房间"的语义：这一局是这一局。
            _number = GenerateNumber(_recent);
            Remember(_number);
            Save();
        }

        public static string DefaultPath()
        {
            return Path.Combine(AppEnv.Root, "room.json");
        }

        /// <summary>当前房间号（8 位数字）。</summary>
        public string Number
        {
            get { lock (_gate) { return _number; } }
        }

        public bool HasPassword
        {
            get { lock (_gate) { return _password.Length > 0; } }
        }

        /// <summary>当前密码（没设就是空串）。只在服务端内部用，不下发给玩家。</summary>
        public string Password
        {
            get { lock (_gate) { return _password; } }
        }

        /// <summary>
        /// 设置 / 清除房间密码（空串 = 不需要密码），并立刻落盘。
        /// 返回真正生效的密码（已去掉首尾空白、截断到上限）。
        /// </summary>
        public string SetPassword(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length > MaxPasswordLength) value = value.Substring(0, MaxPasswordLength);

            lock (_gate)
            {
                _password = value;
                Save();
                return _password;
            }
        }

        /// <summary>密码是否匹配。房间没设密码时**恒为 true**（不拦人）。</summary>
        public bool CheckPassword(string candidate)
        {
            string expected;
            lock (_gate)
            {
                expected = _password;
            }
            if (expected.Length == 0) return true;
            return FixedTimeEquals(expected, candidate ?? string.Empty);
        }

        /// <summary>
        /// 玩家访问地址：<c>http://ip:port/房间号/player</c>，设了密码再带
        /// <c>?passwd=</c>（扫码进来的玩家因此不用手打密码）。
        /// </summary>
        public string BuildUrl(string ip, int port)
        {
            string number, password;
            lock (_gate)
            {
                number = _number;
                password = _password;
            }

            var url = "http://" + ip + ":" + port.ToString(CultureInfo.InvariantCulture);
            if (number.Length > 0) url += "/" + number;
            url += "/player";
            if (password.Length > 0) url += "?passwd=" + Uri.EscapeDataString(password);
            return url;
        }

        // ============================================================
        //  号码生成
        // ============================================================

        /// <summary>
        /// 生成一个 8 位房间号（10000000–99999999），并避开 <paramref name="avoid"/> 里
        /// 刚刚用过的那些。1 亿分之一的空间里只排掉几个，撞号的概率可以忽略；
        /// 真撞上也只会多试一次。
        /// </summary>
        public static string GenerateNumber(ICollection<string> avoid)
        {
            var rng = new RNGCryptoServiceProvider();
            var bytes = new byte[4];

            for (var attempt = 0; attempt < 64; attempt++)
            {
                rng.GetBytes(bytes);
                // 取 31 位（保证非负），再压到 [0, 9e7)
                var raw = (((int)bytes[0] << 24) | ((int)bytes[1] << 16) |
                           ((int)bytes[2] << 8) | bytes[3]) & 0x7FFFFFFF;
                var value = 10000000 + (raw % 90000000);
                var text = value.ToString(CultureInfo.InvariantCulture);

                if (avoid == null || !avoid.Contains(text)) return text;
            }

            // 兜底（几乎不可能走到）：用时间戳凑一个，仍然保证 8 位。
            var fallback = 10000000 + (int)(DateTime.Now.Ticks % 90000000);
            return fallback.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>号码是不是合法的 8 位房间号（页面路由、自检都用这一条）。</summary>
        public static bool IsValidNumber(string value)
        {
            if (value == null || value.Length != 8) return false;
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9') return false;
            }
            return value[0] != '0';
        }

        private void Remember(string number)
        {
            _recent.Remove(number);
            _recent.Add(number);
            while (_recent.Count > RecentKeep) _recent.RemoveAt(0);
        }

        // ============================================================
        //  落盘
        // ============================================================

        private void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"room\":").Append(MiniJson.Write(_number));
                sb.Append(",\"password\":").Append(MiniJson.Write(_password));
                sb.Append(",\"recent\":").Append(MiniJson.Write(string.Join(",", _recent.ToArray())));
                sb.Append('}');

                // 存成**不带 BOM** 的 UTF-8：`Encoding.UTF8` 会写个 BOM 出来，
                // 而这是个用户可能手改 / 被别的工具读的小配置文件，多那 3 个字节只会添乱。
                // （读回来走 File.ReadAllText，它本来就会自动吃掉 BOM，所以这里只是"别制造"它。）
                File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // 写不进去不影响本次运行（房间号 / 密码都还在内存里）。
                AppLog.Write("保存房间配置失败：" + _path, ex);
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;

                var body = MiniJson.ParseObject(File.ReadAllText(_path, Encoding.UTF8));
                if (body == null) return;

                var number = MiniJson.GetString(body, "room");
                if (IsValidNumber(number))
                {
                    _number = number;
                    Remember(number);
                }

                _password = MiniJson.GetString(body, "password") ?? string.Empty;
                if (_password.Length > MaxPasswordLength) _password = _password.Substring(0, MaxPasswordLength);

                var recent = MiniJson.GetString(body, "recent");
                if (!string.IsNullOrEmpty(recent))
                {
                    foreach (var item in recent.Split(','))
                    {
                        var trimmed = item.Trim();
                        if (IsValidNumber(trimmed)) Remember(trimmed);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("读取房间配置失败（按没配过处理）：" + _path, ex);
            }
        }

        // ============================================================
        //  辅助
        // ============================================================

        /// <summary>
        /// 常数时间字符串比较，避免时序侧信道（token / 密码都走它）。
        /// </summary>
        internal static bool FixedTimeEquals(string a, string b)
        {
            var ba = Encoding.UTF8.GetBytes(a ?? string.Empty);
            var bb = Encoding.UTF8.GetBytes(b ?? string.Empty);
            if (ba.Length != bb.Length) return false;

            var diff = 0;
            for (var i = 0; i < ba.Length; i++) diff |= ba[i] ^ bb[i];
            return diff == 0;
        }
    }
}
