using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace TrpgMaps
{
    /// <summary>
    /// HTTP 接口层，对应原 Python 版 app.py 的路由与 SocketIO 事件。
    /// 实时推送走 SSE（/api/events），客户端动作用普通 POST。
    ///
    /// 权限划分：
    ///  - 玩家可用：/player、静态资源、/api/events、/api/login、/api/request_map、/api/ping；
    ///  - 仅本机 DM 可用：底图管理、玩家名单/踢人、房间锁定、IP/端口设置。
    /// </summary>
    internal sealed class Routes
    {
        private readonly ServerSettings _settings;
        private readonly MapStore _maps;
        private readonly GameState _game;
        private readonly RealtimeHub _hub;
        private HttpServer _server;

        // ---- 绘图 ----
        private DrawingStore _drawings;
        private TerrainCatalog _catalog;
        private TerrainImageCache _drawCache;

        /// <summary>
        /// 「网格 ↔ 底图」几何的来源。画布尺寸只有界面层知道，所以由 MainForm
        /// 注入一个取快照的委托；每次生成下发载荷时现取一次（取到的是一份只读快照，
        /// 跨线程安全，见 <see cref="GridAnchor"/>）。
        /// </summary>
        private Func<GridAnchor> _gridAnchorProvider;

        // 二维码生成开销不小，而 /api/network 会被反复探测；按地址缓存
        private readonly object _qrGate = new object();
        private string _qrKey;
        private string _qrValue;

        public Routes(ServerSettings settings, MapStore maps, GameState game, RealtimeHub hub)
        {
            _settings = settings;
            _maps = maps;
            _game = game;
            _hub = hub;
        }

        /// <summary>服务器实例（用于重开服务），构造后注入以避免循环依赖。</summary>
        public void AttachServer(HttpServer server)
        {
            _server = server;
        }

        /// <summary>绘图数据 / 素材 / 贴图缓存，构造后注入（避免构造函数越铺越长）。</summary>
        public void AttachDrawing(DrawingStore drawings, TerrainCatalog catalog, TerrainImageCache cache)
        {
            _drawings = drawings;
            _catalog = catalog;
            _drawCache = cache;
        }

        /// <summary>
        /// 注入「网格 ↔ 底图」几何的来源（画布尺寸只有界面层知道，见 <see cref="GridAnchor"/>）。
        /// </summary>
        public void AttachGridAnchor(Func<GridAnchor> provider)
        {
            _gridAnchorProvider = provider;
        }

        // ============================================================
        //  分发
        // ============================================================

        public bool Handle(RequestContext ctx)
        {
            var path = ctx.Request.Path;
            var method = ctx.Request.Method;

            if (method == "GET" || method == "HEAD")
            {
                if (path == "/" || path == "/player" || path == "/map")
                {
                    return ServePage(ctx, "player.html");
                }
                if (path.StartsWith("/css/", StringComparison.Ordinal) ||
                    path.StartsWith("/js/", StringComparison.Ordinal) ||
                    path.StartsWith("/images/", StringComparison.Ordinal))
                {
                    return ServeStatic(ctx, path.Substring(1));
                }
                if (path == "/api/events") return HandleEvents(ctx);
                if (path == "/api/network") return Json(ctx, NetworkPayload());
                if (path == "/api/maps") return Json(ctx, MapsPayload());
                if (path.StartsWith("/api/maps/size/", StringComparison.Ordinal))
                    return HandleMapSize(ctx, path.Substring("/api/maps/size/".Length));
                if (path.StartsWith("/api/maps/image/", StringComparison.Ordinal))
                    return HandleMapImage(ctx, path.Substring("/api/maps/image/".Length));
                if (path == "/api/terrain") return Json(ctx, _catalog == null ? "{\"kinds\":[],\"assets\":[]}" : _catalog.ToJson());
                if (path.StartsWith("/api/terrain/image/", StringComparison.Ordinal))
                    return HandleTerrainImage(ctx, path.Substring("/api/terrain/image/".Length), false);
                if (path.StartsWith("/api/terrain/tile/", StringComparison.Ordinal))
                    return HandleTerrainImage(ctx, path.Substring("/api/terrain/tile/".Length), true);
                if (path == "/api/drawing") return Json(ctx, _drawings == null ? "{\"version\":0,\"cells\":[]}" : _drawings.ToJson());
                if (path == "/api/settings") return HandleGetSettings(ctx);
                if (path == "/api/addresses") return HandleGetAddresses(ctx);
                if (path == "/api/players") return HandleGetPlayers(ctx);
            }

            if (method == "POST")
            {
                if (path == "/api/login") return HandleLogin(ctx);
                if (path == "/api/request_map") return HandleRequestMap(ctx);
                if (path == "/api/ping") return HandlePing(ctx);
                if (path == "/api/maps/set") return HandleSetMap(ctx);
                if (path == "/api/maps/remove") return HandleRemoveMap(ctx);
                if (path == "/api/maps/rotate") return HandleRotateMap(ctx);
                if (path == "/api/maps/gridcolor") return HandleGridColor(ctx);
                if (path == "/api/maps/upload") return HandleUpload(ctx);
                if (path == "/api/drawing/clear") return HandleDrawingClear(ctx);
                if (path == "/api/players/kick") return HandleKick(ctx);
                if (path == "/api/room/lock") return HandleRoomLock(ctx);
                if (path == "/api/settings") return HandleSaveSettings(ctx);
            }

            ctx.WriteText(404, "{\"ok\":false,\"msg\":\"not found\"}");
            return false;
        }

        // ============================================================
        //  静态资源与页面
        // ============================================================

        private static bool ServePage(RequestContext ctx, string fileName)
        {
            var full = Path.Combine(AppEnv.WebRoot, fileName);
            if (!File.Exists(full))
            {
                ctx.WriteText(404, "{\"ok\":false,\"msg\":\"page not found\"}");
                return false;
            }
            var bytes = File.ReadAllBytes(full);
            ctx.WriteResponse(200, "text/html; charset=utf-8", bytes, false);
            return false;
        }

        private static bool ServeStatic(RequestContext ctx, string relative)
        {
            // 防目录穿越
            var normalized = relative.Replace('/', Path.DirectorySeparatorChar);
            if (normalized.Contains(".."))
            {
                ctx.WriteText(404, "{\"ok\":false,\"msg\":\"not found\"}");
                return false;
            }

            var full = Path.Combine(AppEnv.WebRoot, normalized);
            if (!File.Exists(full))
            {
                ctx.WriteText(404, "{\"ok\":false,\"msg\":\"not found\"}");
                return false;
            }

            var bytes = File.ReadAllBytes(full);
            ctx.WriteResponse(200, ContentTypeFor(full), bytes, false);
            return false;
        }

        private static string ContentTypeFor(string fileName)
        {
            switch (Path.GetExtension(fileName).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".png": return "image/png";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                case ".ico": return "image/x-icon";
                default: return "image/jpeg";
            }
        }

        // ============================================================
        //  底图
        // ============================================================

        private string MapsPayload()
        {
            var list = _maps.List();

            // 按中文名称排序
            list.Sort(delegate (MapInfo a, MapInfo b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });

            var items = new List<object>();
            foreach (var item in list)
            {
                var size = ImageSizeReader.Read(item.Path);
                items.Add(MiniJson.WriteObject(
                    "name", item.Name,
                    "path", item.Path,
                    "md5", item.Md5,
                    "url", item.Url,
                    "width", size == null ? 0 : size.Width,
                    "height", size == null ? 0 : size.Height));
            }

            var sb = new StringBuilder();
            sb.Append("{\"images\":[");
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append((string)items[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private bool HandleMapSize(RequestContext ctx, string rawName)
        {
            var name = Uri.UnescapeDataString(rawName);
            var full = MapStore.ResolveImagePath(name);
            if (full == null) return NotFound(ctx);

            var size = ImageSizeReader.Read(full);
            if (size == null) return NotFound(ctx);

            ctx.WriteText(200, MiniJson.WriteObject(
                "name", Path.GetFileName(name), "width", size.Width, "height", size.Height));
            return false;
        }

        private bool HandleMapImage(RequestContext ctx, string rawName)
        {
            var name = Uri.UnescapeDataString(rawName);
            var full = MapStore.ResolveImagePath(name);
            if (full == null) return NotFound(ctx);

            var bytes = File.ReadAllBytes(full);
            ctx.WriteResponse(200, ContentTypeFor(full), bytes, false);
            return false;
        }

        // ============================================================
        //  绘图素材与绘图内容
        // ============================================================

        /// <summary>
        /// 素材图片。<paramref name="tile"/> = true 时按 <c>?px=</c> 现降采样一张小图，
        /// 给手机端用。
        ///
        /// 为什么要单独的瓦片接口：地形原图是 1254×1254、单张 1.3–3.3MB，
        /// 手机要按格子铺满整张地图的话得把这些原图全下下来，几百兆流量。
        /// 一格在手机上也就三四十个 CSS 像素，64px 的小图完全够用（还能放大看）。
        /// </summary>
        private bool HandleTerrainImage(RequestContext ctx, string rest, bool tile)
        {
            var split = rest.IndexOf('/');
            if (split <= 0) return NotFound(ctx);

            var kind = Uri.UnescapeDataString(rest.Substring(0, split));
            var file = Uri.UnescapeDataString(rest.Substring(split + 1));
            var full = TerrainCatalog.ResolvePath(kind, file);
            if (full == null) return NotFound(ctx);

            if (!tile)
            {
                var bytes = File.ReadAllBytes(full);
                ctx.WriteResponse(200, ContentTypeFor(full), bytes, false);
                return false;
            }

            var px = 64;
            int.TryParse(ctx.Request.GetQuery("px"), out px);
            if (px < 16) px = 16;
            if (px > 512) px = 512;

            if (_drawCache == null) return NotFound(ctx);

            var asset = _catalog == null ? null : _catalog.Resolve(kind, file);
            var opacity = asset == null ? 1.0 : asset.Opacity;

            try
            {
                using (var bitmap = _drawCache.Get(kind, file, px, opacity))
                {
                    if (bitmap == null) return NotFound(ctx);

                    using (var ms = new MemoryStream())
                    {
                        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ctx.WriteResponse(200, "image/png", ms.ToArray(), false);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("生成素材瓦片失败：" + kind + "/" + file, ex);
                return NotFound(ctx);
            }
            return false;
        }

        /// <summary>清空全部绘图（仅本机）。给自动化测试和"擦不干净"时的兜底用。</summary>
        private bool HandleDrawingClear(RequestContext ctx)
        {
            if (!ctx.IsLocal) return Forbidden(ctx);
            if (_drawings == null) return NotFound(ctx);

            var removed = _drawings.ClearAll();
            _drawings.Save();
            _hub.Broadcast("drawing_change", MiniJson.WriteObject("version", _drawings.Version));

            ctx.WriteText(200, MiniJson.WriteObject(
                "success", true,
                "removed", removed,
                "version", _drawings.Version,
                "message", "已清空绘图（" + removed + " 格）"));
            return false;
        }

        private bool HandleSetMap(RequestContext ctx)
        {
            if (!ctx.IsLocal) return Forbidden(ctx);

            var body = ctx.Request.BodyJson();
            var image = MiniJson.GetString(body, "image");
            var mode = MiniJson.GetString(body, "mode");

            if (string.IsNullOrEmpty(image))
            {
                ctx.WriteText(400, "{\"success\":false,\"message\":\"缺少图片名称\"}");
                return false;
            }
            if (!_maps.SetCurrent(image, mode))
            {
                ctx.WriteText(404, "{\"success\":false,\"message\":\"图片不存在\"}");
                return false;
            }

            _hub.Broadcast("map_change", BuildMapPayload(_maps.CurrentMap, _maps.FitMode, _maps.Rotation));
            ctx.WriteText(200, MiniJson.WriteObject(
                "success", true, "message", "底图已更新", "current", _maps.CurrentMap));
            return false;
        }

        /// <summary>
        /// 撤掉当前底图，回到"只有网格"的初始状态。
        ///
        /// 只摘"当前选中"：<c>maps\</c> 里的文件一个都不删，底图列表照旧，随时能再选回来。
        /// 显示模式 / 旋转角 / 网格颜色也都不动 —— 它们本来就是全局显示状态。
        ///
        /// 界面上那个「撤掉地图」按钮走的是 <see cref="RemoveCurrentMap"/>（同一个实现），
        /// 这个路由是给外部工具和自测脚本用的，两处语义必须一致，所以不再各写一遍。
        /// </summary>
        private bool HandleRemoveMap(RequestContext ctx)
        {
            if (!ctx.IsLocal) return Forbidden(ctx);

            var result = RemoveCurrentMap();
            ctx.WriteText(200, MiniJson.WriteObject(
                "success", result != null,
                "message", result != null ? "已撤掉：" + result : "本来就没有底图",
                "removed", result == null ? string.Empty : result));
            return false;
        }

        /// <summary>
        /// 真的把当前底图摘掉，并把 map_change 播出去。返回被撤掉的底图名（本来就没有则 null）。
        /// 界面按钮与 <c>POST /api/maps/remove</c> 共用这一份实现。
        /// </summary>
        public string RemoveCurrentMap()
        {
            var removed = _maps.CurrentMap;
            if (string.IsNullOrEmpty(removed)) return null;

            _maps.ClearCurrent();

            // 广播要在改完之后：载荷里的 image / url 都会是空串，
            // 手机端收到就把底图摘掉、只留网格（见 player.js 的 map_change）。
            _hub.Broadcast("map_change", BuildMapPayload(_maps.CurrentMap, _maps.FitMode, _maps.Rotation));
            AppLog.Write("底图：已撤掉「" + removed + "」（文件保留，可随时重新选中）");
            return removed;
        }

        /// <summary>
        /// 顺时针再转 delta 度（默认 90），支持连续点击。只动底图，网格不动。
        /// 用增量而不是绝对值：面板上的「90°」按钮可以连点四次绕一圈，
        /// 「180°」按钮就是 delta=180（0↔180、90↔270）。
        /// </summary>
        private bool HandleRotateMap(RequestContext ctx)
        {
            if (!ctx.IsLocal) return Forbidden(ctx);

            var body = ctx.Request.BodyJson();
            var delta = MiniJson.GetInt(body, "delta", 90);
            var rotation = _maps.Rotate(delta);

            _hub.Broadcast("map_change", BuildMapPayload(_maps.CurrentMap, _maps.FitMode, rotation));
            ctx.WriteText(200, MiniJson.WriteObject(
                "success", true, "rotation", rotation,
                "message", "底图已旋转 " + rotation + "°"));
            return false;
        }

        /// <summary>
        /// 切换网格线颜色：<c>auto</c>（跟随底图明暗自动切换）/ <c>white</c> / <c>black</c>。
        ///
        /// auto 的判定在服务端做（<see cref="MapStore.ResolveGridColor"/>），
        /// 播下去的 <c>gridColor</c> 已经是 **white / black 这两个确定值**，
        /// 玩家端不需要自己去读底图像素 —— 手机上解码一张 3MB 的图太浪费，
        /// 而且万一手机和电脑判得不一样，玩家看到的网格就和 DM 的对不上了。
        /// </summary>
        private bool HandleGridColor(RequestContext ctx)
        {
            if (!ctx.IsLocal) return Forbidden(ctx);

            var body = ctx.Request.BodyJson();
            var mode = _maps.SetGridMode(MiniJson.GetString(body, "mode"));
            var color = _maps.ResolveGridColor();

            _hub.Broadcast("map_change", BuildMapPayload(_maps.CurrentMap, _maps.FitMode, _maps.Rotation));
            ctx.WriteText(200, MiniJson.WriteObject(
                "success", true,
                "mode", mode,
                "color", color,
                "message", GridColorMessage(mode, color)));
            return false;
        }

        private static string GridColorMessage(string mode, string color)
        {
            var label = color == "black" ? "黑色" : "白色";
            return mode == "auto"
                ? "网格颜色：自动（当前判定为" + label + "）"
                : "网格颜色：固定" + label;
        }

        private bool HandleUpload(RequestContext ctx)
        {
            if (!ctx.IsLocal) return Forbidden(ctx);

            var boundary = GetMultipartBoundary(ctx.Request.GetHeader("Content-Type"));
            if (boundary == null)
            {
                ctx.WriteText(400, "{\"success\":false,\"message\":\"没有文件\"}");
                return false;
            }

            string fileName;
            byte[] content;
            if (!TryParseSingleFile(ctx.Request.Body, boundary, out fileName, out content) ||
                content == null || content.Length == 0)
            {
                ctx.WriteText(400, "{\"success\":false,\"message\":\"没有文件\"}");
                return false;
            }

            var result = _maps.Upload(fileName, content);
            if (result.Success)
            {
                ctx.WriteText(200, MiniJson.WriteObject(
                    "success", true,
                    "message", result.Message,
                    "data", new Dictionary<string, object>
                    {
                        { "name", result.Data.Name },
                        { "url", result.Data.Url },
                        { "md5", result.Data.Md5 }
                    }));
                return false;
            }

            var duplicate = result.Message.Contains("已存在") || result.Message.Contains("重复");
            ctx.WriteText(duplicate ? 409 : 400,
                MiniJson.WriteObject("success", false, "message", result.Message));
            return false;
        }

        // ============================================================
        //  网络信息与设置
        // ============================================================

        private string NetworkPayload()
        {
            var playerUrl = _settings.PlayerUrl;
            return MiniJson.WriteObject(
                "ip", _settings.ListenIp,
                "port", _settings.Port,
                "playerUrl", playerUrl,
                "qrCode", BuildQr(playerUrl),
                "roomLocked", _settings.RoomLocked);
        }

        private string BuildQr(string playerUrl)
        {
            lock (_qrGate)
            {
                if (_qrKey == playerUrl && _qrValue != null) return _qrValue;
                try
                {
                    _qrValue = QrCodeGenerator.RenderDataUrl(playerUrl, 8, 2);
                }
                catch (Exception ex)
                {
                    AppLog.Write("生成二维码失败", ex);
                    _qrValue = string.Empty;
                }
                _qrKey = playerUrl;
                return _qrValue;
            }
        }

        private bool HandleGetSettings(RequestContext ctx)
        {
            if (!ctx.IsLocal) return Forbidden(ctx);

            var sb = new StringBuilder();
            sb.Append("{\"listenIp\":").Append(MiniJson.Write(_settings.ListenIp));
            sb.Append(",\"port\":").Append(_settings.Port);
            sb.Append(",\"roomLocked\":").Append(_settings.RoomLocked ? "true" : "false");
            sb.Append(",\"playerUrl\":").Append(MiniJson.Write(_settings.PlayerUrl));
            sb.Append(",\"addresses\":").Append(AddressListJson());
            sb.Append(",\"network\":").Append(NetworkPayload());
            sb.Append('}');

            ctx.WriteText(200, sb.ToString());
            return false;
        }

        private bool HandleGetAddresses(RequestContext ctx)
        {
            if (!ctx.IsLocal) return Forbidden(ctx);

            var found = IpEnumerator.Enumerate();
            var recommended = found[0].Address;
            foreach (var item in found)
            {
                if (item.IsRecommended) { recommended = item.Address; break; }
            }

            ctx.WriteText(200, "{\"addresses\":" + AddressListJson() +
                               ",\"recommended\":" + MiniJson.Write(recommended) + "}");
            return false;
        }

        private static string AddressListJson()
        {
            var sb = new StringBuilder();
            sb.Append('[');
            var first = true;
            foreach (var item in IpEnumerator.Enumerate())
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(MiniJson.WriteObject(
                    "address", item.Address,
                    "interfaceName", item.InterfaceName,
                    "display", item.Display,
                    "recommended", item.IsRecommended));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private bool HandleSaveSettings(RequestContext ctx)
        {
            if (!ctx.IsLocal) return Forbidden(ctx);

            var body = ctx.Request.BodyJson();
            var targetIp = MiniJson.GetString(body, "ip");
            if (string.IsNullOrEmpty(targetIp)) targetIp = _settings.ListenIp;
            var targetPort = MiniJson.GetInt(body, "port", _settings.Port);

            var changed = !string.Equals(targetIp, _settings.ListenIp, StringComparison.OrdinalIgnoreCase)
                          || targetPort != _settings.Port;

            string message;
            if (!_settings.TryUpdate(targetIp, targetPort, out message))
            {
                // 校验失败也用 200：与 MAUI 版保持同一套契约 ——
                // 调用方统一读 success/message 字段，不靠 HTTP 状态码区分业务失败。
                ctx.WriteText(200, MiniJson.WriteObject("success", false, "message", message));
                return false;
            }

            if (changed)
            {
                ctx.WriteText(200, MiniJson.WriteObject(
                    "success", true,
                    "message", "已提交新地址，服务正在重新开放…",
                    "restarting", true,
                    "listenIp", _settings.ListenIp,
                    "port", _settings.Port,
                    "playerUrl", _settings.PlayerUrl,
                    "playerCount", _game.Count));

                var port = targetPort;
                var restarter = new Thread(delegate ()
                {
                    Thread.Sleep(250);
                    try
                    {
                        if (_server != null) _server.Stop();
                        _hub.CloseAll();
                        if (_server != null) _server.Start(port);
                        _hub.Broadcast("server_moved", MiniJson.WriteObject("playerUrl", _settings.PlayerUrl));
                        _hub.Broadcast("map_change", BuildMapPayload(_maps.CurrentMap, _maps.FitMode, _maps.Rotation));
                        AppLog.Write("已按新地址重开服务：" + _settings.PlayerUrl + "（端口 " + port + "）");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("按新 IP/端口重开服务失败", ex);
                    }
                });
                restarter.IsBackground = true;
                restarter.Start();

                return false;
            }

            _hub.Broadcast("map_change", BuildMapPayload(_maps.CurrentMap, _maps.FitMode, _maps.Rotation));
            ctx.WriteText(200, MiniJson.WriteObject(
                "success", true,
                "message", "配置未变化",
                "restarting", false,
                "listenIp", _settings.ListenIp,
                "port", _settings.Port,
                "playerUrl", _settings.PlayerUrl,
                "playerCount", _game.Count));
            return false;
        }

        // ============================================================
        //  玩家名单与房间锁定
        // ============================================================

        private bool HandleGetPlayers(RequestContext ctx)
        {
            if (!ctx.IsLocal) return Forbidden(ctx);
            ctx.WriteText(200, PlayersJson());
            return false;
        }

        private string PlayersJson()
        {
            var sb = new StringBuilder();
            sb.Append('[');
            var first = true;
            foreach (var player in _game.Snapshot())
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(MiniJson.WriteObject("name", player.Name, "ip", player.Ip, "online", player.Online));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private bool HandleKick(RequestContext ctx)
        {
            if (!ctx.IsLocal) return Forbidden(ctx);

            var body = ctx.Request.BodyJson();
            var name = MiniJson.GetString(body, "name");
            if (string.IsNullOrEmpty(name))
            {
                ctx.WriteText(200, "{\"success\":false,\"msg\":\"玩家不存在\"}");
                return false;
            }

            var connectionId = _game.Kick(name);
            if (connectionId == null)
            {
                ctx.WriteText(200, "{\"success\":false,\"msg\":\"玩家不存在\"}");
                return false;
            }

            _hub.SendTo(connectionId, "kicked", "{\"reason\":\"kicked\"}");
            _hub.Broadcast("player_list", PlayersJson());
            ctx.WriteText(200, "{\"success\":true}");
            return false;
        }

        private bool HandleRoomLock(RequestContext ctx)
        {
            if (!ctx.IsLocal) return Forbidden(ctx);

            var body = ctx.Request.BodyJson();
            object lockedValue;
            bool locked;
            if (body != null && body.TryGetValue("locked", out lockedValue) && lockedValue != null)
            {
                locked = MiniJson.GetBool(body, "locked", _settings.RoomLocked);
            }
            else
            {
                locked = !_settings.RoomLocked;
            }
            _settings.RoomLocked = locked;

            _hub.Broadcast("room_state", MiniJson.WriteObject("locked", locked));
            ctx.WriteText(200, MiniJson.WriteObject("success", true, "locked", locked));
            return false;
        }

        // ============================================================
        //  实时通道（SSE）
        // ============================================================

        private bool HandleEvents(RequestContext ctx)
        {
            var id = ctx.Request.GetQuery("id");
            if (string.IsNullOrEmpty(id)) id = Guid.NewGuid().ToString("N");

            ctx.WriteSseHeaders();

            _hub.Unsubscribe(id);
            var sub = _hub.Subscribe(id);

            try
            {
                ctx.WriteSseEvent("connected", MiniJson.WriteObject("data", "Connected to DM server", "id", id));

                while (!sub.Closed && ctx.ClientAlive)
                {
                    var message = sub.Wait(20000);
                    if (message == null)
                    {
                        if (sub.Closed) break;
                        if (!ctx.WriteSseRaw(": ping\n\n")) break;
                        continue;
                    }

                    if (!ctx.WriteSseEvent(message.Event, message.Data)) break;
                    if (message.Event == "kicked") break;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("事件流异常", ex);
            }
            finally
            {
                _hub.Unsubscribe(id);
                _game.Disconnect(id);
                _hub.Broadcast("player_list", PlayersJson());
            }
            return false;
        }

        private bool HandleLogin(RequestContext ctx)
        {
            var body = ctx.Request.BodyJson();
            if (body == null)
            {
                ctx.WriteText(200, "{\"ok\":false,\"msg\":\"缺少参数\",\"code\":\"" + LoginCodes.EmptyName + "\"}");
                return false;
            }

            var connectionId = MiniJson.GetString(body, "id") ?? string.Empty;
            var name = MiniJson.GetString(body, "name");
            var token = MiniJson.GetString(body, "token");

            var result = _game.Login(connectionId, name, token, ctx.Request.RemoteIp);

            if (!result.Success)
            {
                if (connectionId.Length > 0)
                {
                    _hub.SendTo(connectionId, "login_failed",
                        MiniJson.WriteObject("msg", result.Message, "code", result.Code));
                }
                ctx.WriteText(200, MiniJson.WriteObject(
                    "ok", false, "msg", result.Message, "code", result.Code));
                return false;
            }

            if (!string.IsNullOrEmpty(result.DisplacedConnectionId) &&
                !string.Equals(result.DisplacedConnectionId, connectionId, StringComparison.Ordinal))
            {
                _hub.SendTo(result.DisplacedConnectionId, "kicked", "{\"reason\":\"duplicate\"}");
            }

            _hub.Broadcast("player_list", PlayersJson());

            if (connectionId.Length > 0)
            {
                _hub.SendTo(connectionId, "login_success", MiniJson.WriteObject(
                    "msg", result.Message,
                    "name", result.Name,
                    "token", result.Token,
                    "isNewToken", result.IsNewToken,
                    "roomLocked", _settings.RoomLocked));
            }

            ctx.WriteText(200, MiniJson.WriteObject(
                "ok", true,
                "msg", result.Message,
                "name", result.Name,
                "token", result.Token,
                "isNewToken", result.IsNewToken));
            return false;
        }

        private bool HandleRequestMap(RequestContext ctx)
        {
            var body = ctx.Request.BodyJson();
            var id = MiniJson.GetString(body, "id");

            var current = _maps.CurrentMap;
            // 没有底图时 name 传空串即可：url 会自动变成空串，其余字段（含网格颜色）
            // 和正常情况完全一致，玩家端只要清掉图片、保留网格就行。
            var payload = BuildMapPayload(current, _maps.FitMode, _maps.Rotation);

            if (!string.IsNullOrEmpty(id)) _hub.SendTo(id, "map_change", payload);
            ctx.WriteText(200, "{\"ok\":true}");
            return false;
        }

        private bool HandlePing(RequestContext ctx)
        {
            var body = ctx.Request.BodyJson();
            var id = MiniJson.GetString(body, "id");
            if (!string.IsNullOrEmpty(id))
            {
                _hub.SendTo(id, "pong", MiniJson.WriteObject(
                    "t", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds));
            }
            ctx.WriteText(200, "{\"ok\":true}");
            return false;
        }

        // ============================================================
        //  辅助
        // ============================================================

        private string BuildMapPayload(string name, string mode, int rotation)
        {
            // 一次取一份快照，四个数一定自洽；取不到就发 0，
            // 玩家端见 0 会退回"贴左上角的等距网格"（老行为），而不是画出一个畸形的锚点。
            //
            // 没有底图时**直接发 0**，不去看画布：画布这时算出来的"锚点"是
            // "底图铺满整个视口"（ComputeImageRect 在没有图时的兜底），
            // 那是描述"没有底图"的一种写法，不是真能用来对齐的几何。
            // 明写成 0，载荷就自洽了 —— 不需要等防抖重播来纠正，
            // 撤掉底图那一刻播出去的第一条就是对的。
            var anchor = string.IsNullOrEmpty(name) ? null : CurrentAnchor();
            var cellsX = anchor != null && anchor.Valid ? anchor.CellsX : 0.0;
            var cellsY = anchor != null && anchor.Valid ? anchor.CellsY : 0.0;
            var halfW = anchor != null && anchor.Valid ? anchor.HalfCellsW : 0.0;
            var halfH = anchor != null && anchor.Valid ? anchor.HalfCellsH : 0.0;

            // drawingVersion 一定要带上：手机端加入房间时只发一次 /api/request_map，
            // 拿到的就是这份 payload。若没有这个字段，新加入的手机在 DM 再次落笔之前
            // 看不到任何已画好的地形（player.js 见版本号落后才去拉 /api/drawing）。
            return MiniJson.WriteObject(
                // image 写成空串而不是 JSON null：这个字段一直是字符串，
                // 没有底图时给 null 会让玩家端多一次类型判断（player.js 只读 url，
                // 但载荷本身保持"同一个字段只有一种类型"更不容易踩坑）。
                "image", name ?? string.Empty,
                "url", string.IsNullOrEmpty(name) ? string.Empty : MapStore.BuildUrl(name),
                "mode", mode,
                // 玩家端要在此基础上再顺时针加 90°（手机"上端"对应电脑"左端"）
                "rotation", rotation,
                // 网格线颜色已经由服务端判定完，玩家端照着画就行
                "gridColor", _maps.ResolveGridColor(),
                "gridMode", _maps.GridMode,
                "drawingVersion", _drawings == null ? 0 : _drawings.Version,
                // ---- 网格与底图的绑定（手机端复原位置用，见 GridAnchor）----
                // 少了这几个数，手机就只能按它自己的尺寸另起一套网格，
                // 画好的地形贴上去就会和底图错位。
                "gridCellsX", cellsX,
                "gridCellsY", cellsY,
                "gridHalfW", halfW,
                "gridHalfH", halfH);
        }

        private GridAnchor CurrentAnchor()
        {
            if (_gridAnchorProvider == null) return null;
            try { return _gridAnchorProvider(); }
            catch { return null; }
        }

        /// <summary>
        /// 下发给玩家端的地图载荷。界面层（MainForm）的广播也走这里，
        /// 保证"面板操作"和"HTTP 操作"播出去的东西**一模一样**。
        /// </summary>
        public string MapPayload(string name, string mode)
        {
            return BuildMapPayload(name, mode, _maps.Rotation);
        }

        private static bool Json(RequestContext ctx, string json)
        {
            ctx.WriteText(200, json);
            return false;
        }

        private static bool NotFound(RequestContext ctx)
        {
            ctx.WriteText(404, "{\"ok\":false,\"msg\":\"not found\"}");
            return false;
        }

        private static bool Forbidden(RequestContext ctx)
        {
            // 与 MAUI 版同一套响应形状：{success:false, message}。
            // 这些接口都只允许本机调用，玩家端永远碰不到。
            ctx.WriteText(403, MiniJson.WriteObject("success", false, "message", "仅本机可操作"));
            return false;
        }

        private static string GetMultipartBoundary(string contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return null;
            if (contentType.IndexOf("multipart/form-data", StringComparison.OrdinalIgnoreCase) < 0) return null;

            foreach (var part in contentType.Split(';'))
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed.Substring("boundary=".Length).Trim();
                    if (value.StartsWith("\"") && value.EndsWith("\"") && value.Length > 1)
                        value = value.Substring(1, value.Length - 2);
                    return value;
                }
            }
            return null;
        }

        /// <summary>
        /// 极简 multipart 解析：只取第一个带 filename 的文件部分。
        /// 桌面端上传走的是直接写文件，这里主要为了和 MAUI 版的接口保持一致。
        /// </summary>
        private static bool TryParseSingleFile(byte[] body, string boundary, out string fileName, out byte[] content)
        {
            fileName = null;
            content = null;
            if (body == null || body.Length == 0) return false;

            var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
            var headerEndToken = Encoding.ASCII.GetBytes("\r\n\r\n");

            var position = IndexOf(body, delimiter, 0);
            while (position >= 0)
            {
                var partStart = position + delimiter.Length;
                // 结束标记 "--boundary--"
                if (partStart + 1 < body.Length && body[partStart] == (byte)'-' && body[partStart + 1] == (byte)'-')
                    return false;

                // 跳过 CRLF
                if (partStart + 1 < body.Length && body[partStart] == (byte)'\r' && body[partStart + 1] == (byte)'\n')
                    partStart += 2;

                var headerEnd = IndexOf(body, headerEndToken, partStart);
                if (headerEnd < 0) return false;

                var headerText = Encoding.ASCII.GetString(body, partStart, headerEnd - partStart);
                var dataStart = headerEnd + headerEndToken.Length;
                var nextDelimiter = IndexOf(body, delimiter, dataStart);
                if (nextDelimiter < 0) return false;

                var dataEnd = nextDelimiter;
                if (dataEnd >= 2 && body[dataEnd - 2] == (byte)'\r' && body[dataEnd - 1] == (byte)'\n')
                    dataEnd -= 2;

                var name = ExtractFileName(headerText);
                if (!string.IsNullOrEmpty(name))
                {
                    var length = dataEnd - dataStart;
                    if (length < 0) return false;

                    fileName = name;
                    content = new byte[length];
                    Buffer.BlockCopy(body, dataStart, content, 0, length);
                    return true;
                }

                position = IndexOf(body, delimiter, nextDelimiter + 1);
            }
            return false;
        }

        private static string ExtractFileName(string headerText)
        {
            var marker = headerText.IndexOf("filename=", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return null;

            var start = marker + "filename=".Length;
            if (start >= headerText.Length) return null;

            var quote = headerText[start];
            if (quote == '"')
            {
                var end = headerText.IndexOf('"', start + 1);
                if (end < 0) return null;
                return headerText.Substring(start + 1, end - start - 1);
            }

            var stop = headerText.IndexOfAny(new[] { ';', '\r', '\n' }, start);
            if (stop < 0) stop = headerText.Length;
            return headerText.Substring(start, stop - start).Trim();
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            if (needle.Length == 0) return -1;
            var limit = haystack.Length - needle.Length;
            for (var i = Math.Max(0, start); i <= limit; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }
    }
}
