using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace TrpgMaps
{
    /// <summary>对外输出的玩家信息。</summary>
    internal sealed class PlayerDto
    {
        public string Name;
        public string Ip;
        public bool Online;

        public PlayerDto(string name, string ip, bool online)
        {
            Name = name;
            Ip = ip;
            Online = online;
        }
    }

    /// <summary>
    /// 全局游戏状态：玩家名册（按玩家名唯一）+ 令牌校验 + 房间锁定。
    ///
    /// 规则：
    /// 1) 名称在房间内唯一，首次出现时服务器颁发随机 token 并写入玩家 cookie；
    /// 2) 之后用已存在的名称加入必须带匹配 token，否则按名称重复拒绝；
    /// 3) 房间锁定时拒绝任何新玩家，只有名册中已有名称 + 正确 token 的老玩家可重连。
    /// </summary>
    internal sealed class GameState
    {
        private readonly Dictionary<string, PlayerEntry> _players =
            new Dictionary<string, PlayerEntry>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private readonly ServerSettings _settings;

        public GameState(ServerSettings settings)
        {
            _settings = settings;
        }

        public List<PlayerDto> Snapshot()
        {
            lock (_gate)
            {
                var list = new List<PlayerDto>(_players.Count);
                foreach (var p in _players.Values) list.Add(new PlayerDto(p.Name, p.Ip, p.Online));
                return list;
            }
        }

        public int Count
        {
            get { lock (_gate) { return _players.Count; } }
        }

        public LoginResult Login(string connectionId, string name, string token, string ip)
        {
            name = (name ?? string.Empty).Trim();
            token = token ?? string.Empty;

            if (name.Length == 0)
                return LoginResult.Fail("名称不能为空", LoginCodes.EmptyName, name);

            lock (_gate)
            {
                var roomLocked = _settings.RoomLocked;

                // ---- 房间里已有这个名称 ----
                PlayerEntry existing;
                if (_players.TryGetValue(name, out existing))
                {
                    if (token.Length == 0 || !FixedTimeEquals(existing.Token, token))
                    {
                        var message = token.Length == 0
                            ? "该名称已被占用；如果你就是本人，请用同一浏览器重新进入"
                            : "身份校验失败（令牌不匹配），该名称已被占用";
                        return LoginResult.Fail(message, LoginCodes.TokenMismatch, name);
                    }

                    string displaced = null;
                    if (existing.Online && !string.Equals(existing.ConnectionId, connectionId, StringComparison.Ordinal))
                    {
                        displaced = existing.ConnectionId;
                    }

                    existing.ConnectionId = connectionId;
                    existing.Online = true;
                    existing.Ip = ip;
                    existing.Kicked = false;

                    return new LoginResult
                    {
                        Success = true,
                        Message = "欢迎回来",
                        Code = string.Empty,
                        Name = name,
                        Token = existing.Token,
                        IsNewToken = false,
                        DisplacedConnectionId = displaced
                    };
                }

                // ---- 新名称 ----
                if (roomLocked)
                    return LoginResult.Fail("房间已锁定，暂不接受新玩家加入", LoginCodes.RoomLocked, name);

                var newToken = GenerateToken();
                _players[name] = new PlayerEntry
                {
                    Name = name,
                    Token = newToken,
                    ConnectionId = connectionId,
                    Ip = ip,
                    Online = true
                };

                return new LoginResult
                {
                    Success = true,
                    Message = "登录成功",
                    Code = string.Empty,
                    Name = name,
                    Token = newToken,
                    IsNewToken = true
                };
            }
        }

        /// <summary>连接断开：把对应玩家标记为离线。</summary>
        public void Disconnect(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId)) return;
            lock (_gate)
            {
                foreach (var entry in _players.Values)
                {
                    if (string.Equals(entry.ConnectionId, connectionId, StringComparison.Ordinal))
                    {
                        entry.Online = false;
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// 这名玩家presented的令牌是不是对的（名册里有这个名称 + 令牌一致）。
        ///
        /// 登录时用来决定"要不要再问一次房间密码"：**凭令牌认出来的老玩家直接放行**，
        /// 只有陌生人才需要密码 —— 密码保护的是"这个房间谁能进来"，
        /// 而令牌已经证明了他就是名册里那个人。
        /// </summary>
        public bool HasValidToken(string name, string token)
        {
            name = (name ?? string.Empty).Trim();
            token = token ?? string.Empty;
            if (name.Length == 0 || token.Length == 0) return false;

            lock (_gate)
            {
                PlayerEntry entry;
                return _players.TryGetValue(name, out entry) && FixedTimeEquals(entry.Token, token);
            }
        }

        /// <summary>
        /// **退出房间**：把这条记录整个从名册里删掉（= 同时清掉"这个房间对这名玩家的记忆"，
        /// 他的令牌也就跟着作废了）。令牌不对 / 名册里没这个人都不动任何东西。
        ///
        /// 和 <see cref="Kick"/> 的区别：踢人只把条目挂起来（<c>Online=false</c>、
        /// <c>Kicked=true</c>），带原令牌还能再进来；退出是**真删**，下次得从头加入。
        /// </summary>
        public LeaveResult Leave(string name, string token)
        {
            name = (name ?? string.Empty).Trim();
            token = token ?? string.Empty;

            if (name.Length == 0) return LeaveResult.Fail(LoginCodes.EmptyName, "玩家名为空");

            lock (_gate)
            {
                PlayerEntry entry;
                if (!_players.TryGetValue(name, out entry))
                    return LeaveResult.Fail(RoomCodes.NotFound, "名册里没有这名玩家");

                if (token.Length == 0 || !FixedTimeEquals(entry.Token, token))
                    return LeaveResult.Fail(LoginCodes.TokenMismatch, "身份校验失败（令牌不匹配）");

                _players.Remove(name);
                return new LeaveResult
                {
                    Success = true,
                    Name = name,
                    Message = name + " 已退出房间",
                    ConnectionId = entry.ConnectionId
                };
            }
        }

        /// <summary>按名称踢出，返回其连接 ID；名册条目保留，带原令牌仍可重进。</summary>
        public string Kick(string name)
        {
            lock (_gate)
            {
                PlayerEntry entry;
                if (!_players.TryGetValue(name, out entry)) return null;
                entry.Kicked = true;
                entry.Online = false;
                return entry.ConnectionId;
            }
        }

        /// <summary>生成 32 位十六进制随机令牌。</summary>
        public static string GenerateToken()
        {
            var bytes = new byte[16];
            // 不用 using：RNGCryptoServiceProvider 在 .NET 3.5 上不实现 IDisposable，
            // 这样写 3.5 / 4.x 两个目标都能编译。
            var rng = new RNGCryptoServiceProvider();
            rng.GetBytes(bytes);
            var sb = new StringBuilder(32);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>常数时间字符串比较，避免时序侧信道。</summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            // 实现放在 RoomState 里：房间密码也要用同一份比较逻辑，
            // 免得两处各写一遍、改了一处漏另一处。
            return RoomState.FixedTimeEquals(a, b);
        }
    }
}
