using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TrpgMaps
{
    /// <summary>一种界面语言。Code 是 **小写**的 BCP 47 标签（如 zh-cn / en-us）。</summary>
    internal sealed class LangInfo
    {
        public string Code;
        /// <summary>用这种语言自己写的语言名（下拉框里显示的就是它）。</summary>
        public string NativeName;
        /// <summary>英文名，排序用（按英文首字母）。</summary>
        public string EnglishName;
    }

    /// <summary>
    /// 界面语言的**唯一出处**：探测系统语言、读写 locales\&lt;bcp47&gt;.json、取词。
    ///
    /// 约定
    /// ----
    ///  * 语言标签一律 **小写 BCP 47**：<c>zh-cn</c> / <c>en-us</c>。比较时不区分大小写，
    ///    所以 <c>zh-CN</c> 与 <c>zh-cn</c> 等价（写文件时统一用小写）。
    ///  * 词条按语言分文件存在 exe 同级的 <c>locales\</c>：<c>zh-cn.json</c>、<c>en-us.json</c>。
    ///    文件里就是普通的「键 → 文字」对象，用户可以直接改（改完重启生效）。
    ///  * 内置一份默认词表（<see cref="Defaults"/>）：文件缺失、或用户删了某一条时兜底。
    ///    程序启动时会把**缺的键**补进文件，但**不会覆盖**用户已经改过的值。
    ///  * 素材（文件夹名）的显示语言**跟随这里的设置**，不单独去读系统语言。
    /// </summary>
    internal static class Loc
    {
        /// <summary>没有合适语言时的兜底。</summary>
        public const string Fallback = "zh-cn";

        /// <summary>当前支持的语言。新增语言只要往这里加一行 + 补词表。</summary>
        private static readonly LangInfo[] Supported = new LangInfo[]
        {
            new LangInfo { Code = "zh-cn", NativeName = "简体中文", EnglishName = "Chinese (Simplified)" },
            new LangInfo { Code = "en-us", NativeName = "English",  EnglishName = "English (United States)" }
        };

        private static Dictionary<string, string> _map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string _language = Fallback;
        private static bool _initialized;

        // ============================================================
        //  对外
        // ============================================================

        /// <summary>当前界面语言（小写 BCP 47）。</summary>
        public static string Language { get { EnsureInit(); return _language; } }

        /// <summary>支持的语言列表（顺序固定，下拉框排序由界面那边做）。</summary>
        public static LangInfo[] Languages { get { return Supported; } }

        /// <summary>
        /// 系统语言**归一到受支持的语言**（zh-* → zh-cn，en-* → en-us，其余 → zh-cn）。
        /// 关于面板里那个「推荐」项就是它。
        /// </summary>
        public static string RecommendedLanguage
        {
            get { return Normalize(CultureInfo.CurrentUICulture.Name); }
        }

        /// <summary>文件夹 locales.toml 与 locales\*.json 所在目录（不存在就建）。</summary>
        public static string LocalesFolder
        {
            get
            {
                var dir = Path.Combine(AppEnv.Root, "locales");
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    AppLog.Write("创建 locales 目录失败：" + dir, ex);
                }
                return dir;
            }
        }

        public static LangInfo Find(string code)
        {
            var normalized = Normalize(code);
            foreach (var lang in Supported)
            {
                if (string.Equals(lang.Code, normalized, StringComparison.OrdinalIgnoreCase)) return lang;
            }
            return null;
        }

        /// <summary>语言的**本语言写法**（「简体中文」/「English」）。</summary>
        public static string DisplayName(string code)
        {
            var lang = Find(code);
            return lang == null ? code : lang.NativeName;
        }

        /// <summary>取词。找不到就退回中文默认值，再退回 key 本身（界面上不会出现空白）。</summary>
        public static string T(string key)
        {
            EnsureInit();
            if (string.IsNullOrEmpty(key)) return string.Empty;

            string value;
            if (_map.TryGetValue(key, out value) && !string.IsNullOrEmpty(value)) return value;

            var index = IndexOf(_language);
            var pair = Defaults.Find(key);
            if (pair != null)
            {
                var candidate = pair[Math.Max(0, index)];
                if (!string.IsNullOrEmpty(candidate)) return candidate;
                return pair[0];
            }
            return key;
        }

        /// <summary>取词并格式化。</summary>
        public static string T(string key, params object[] args)
        {
            var text = T(key);
            if (args == null || args.Length == 0) return text;
            try
            {
                return string.Format(CultureInfo.CurrentCulture, text, args);
            }
            catch (FormatException)
            {
                return text;
            }
        }

        // ---- 自检用（只读，别在界面代码里用）----

        /// <summary>内置词表里一共有多少条。</summary>
        public static int DefaultKeyCount { get { return Defaults.Count; } }

        public static string DefaultKeyAt(int index) { return Defaults.KeyAt(index); }

        /// <summary>某一条在指定语言里的内置译文（空串 = 漏翻）。</summary>
        public static string DefaultValueAt(int index, string code)
        {
            return Defaults.ValueAt(index, IndexOf(code));
        }

        /// <summary>取**指定语言**的词（下拉框里的「（推荐）」要用各语言自己的写法）。</summary>
        public static string TextIn(string code, string key)
        {
            var pair = Defaults.Find(key);
            if (pair == null) return key;
            var index = IndexOf(Normalize(code));
            return string.IsNullOrEmpty(pair[Math.Max(0, index)]) ? pair[0] : pair[Math.Max(0, index)];
        }

        /// <summary>界面首次启动时调用：建目录、补词条、选语言。</summary>
        public static void Initialize()
        {
            EnsureFiles();

            var saved = ReadSavedLanguage();
            if (!string.IsNullOrEmpty(saved) && Find(saved) != null)
            {
                _language = Normalize(saved);
            }
            else
            {
                _language = RecommendedLanguage;
            }

            LoadMap(_language);
            _initialized = true;

            AppLog.Write("界面语言：" + _language +
                         "（系统 " + CultureInfo.CurrentUICulture.Name + "，推荐 " + RecommendedLanguage + "）");
        }

        /// <summary>
        /// 切换语言。返回 true = 成功。
        /// <paramref name="persist"/> = true 时写入 <c>locale.json</c>（下次启动沿用）。
        /// </summary>
        public static bool SetLanguage(string code, bool persist)
        {
            var lang = Find(code);
            if (lang == null) return false;

            _language = lang.Code;
            LoadMap(_language);
            _initialized = true;

            if (persist) SaveLanguage(_language);
            AppLog.Write("界面语言切换为：" + _language);
            return true;
        }

        /// <summary>
        /// 归一化：<c>zh-Hans-CN</c> / <c>zh-CN</c> / <c>zh</c> 都落到 <c>zh-cn</c>；
        /// <c>en-GB</c> 落到 <c>en-us</c>；不认识的一律 zh-cn。
        /// </summary>
        public static string Normalize(string code)
        {
            if (string.IsNullOrEmpty(code)) return Fallback;
            var value = code.Trim().Replace('_', '-').ToLowerInvariant();

            var dash = value.IndexOf('-');
            var primary = dash > 0 ? value.Substring(0, dash) : value;

            if (primary == "zh") return "zh-cn";
            if (primary == "en") return "en-us";

            foreach (var lang in Supported)
            {
                if (string.Equals(lang.Code, value, StringComparison.OrdinalIgnoreCase)) return lang.Code;
            }
            return Fallback;
        }

        // ============================================================
        //  文件
        // ============================================================

        private static string FileFor(string code)
        {
            return Path.Combine(LocalesFolder, code + ".json");
        }

        private static string SettingsPath
        {
            get { return Path.Combine(AppEnv.Root, "locale.json"); }
        }

        /// <summary>
        /// 保证 locales\ 下每种语言都有一个 json。已存在时只**补缺失的键**，
        /// 不覆盖用户改过的值 —— 升级后新增的键会自动出现，翻译不会白改。
        /// </summary>
        public static void EnsureFiles()
        {
            for (var i = 0; i < Supported.Length; i++)
            {
                var path = FileFor(Supported[i].Code);
                var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    if (File.Exists(path))
                    {
                        var obj = MiniJson.ParseObject(File.ReadAllText(path, Encoding.UTF8));
                        if (obj != null)
                        {
                            foreach (var pair in obj)
                            {
                                var text = pair.Value as string;
                                if (text != null) existing[pair.Key] = text;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("读取语言文件失败（将重建）：" + path, ex);
                }

                var missing = 0;
                for (var k = 0; k < Defaults.Count; k++)
                {
                    if (!existing.ContainsKey(Defaults.KeyAt(k))) missing++;
                }

                if (File.Exists(path) && missing == 0) continue;

                // 用户已有的值优先，缺的用内置默认补
                for (var k = 0; k < Defaults.Count; k++)
                {
                    var key = Defaults.KeyAt(k);
                    if (!existing.ContainsKey(key)) existing[key] = Defaults.ValueAt(k, i);
                }

                try
                {
                    WriteJson(path, existing);
                    AppLog.Write("已写入语言文件：" + path + "（补 " + missing + " 条）");
                }
                catch (Exception ex)
                {
                    AppLog.Write("写入语言文件失败：" + path, ex);
                }
            }
        }

        /// <summary>写成人能读的 JSON（一行一个词条，便于手工翻译）。</summary>
        private static void WriteJson(string path, Dictionary<string, string> map)
        {
            var sb = new StringBuilder();
            sb.Append("{\r\n");

            var first = true;
            var known = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            for (var k = 0; k < Defaults.Count; k++)
            {
                var key = Defaults.KeyAt(k);
                known[key] = true;
                string value;
                if (!map.TryGetValue(key, out value)) value = string.Empty;

                if (!first) sb.Append(",\r\n");
                first = false;
                sb.Append("  ").Append(MiniJson.Write(key)).Append(": ").Append(MiniJson.Write(value));
            }

            // 用户自己加的键原样保留（重写文件时不能把它们吞掉）
            foreach (var pair in map)
            {
                if (known.ContainsKey(pair.Key)) continue;
                if (!first) sb.Append(",\r\n");
                first = false;
                sb.Append("  ").Append(MiniJson.Write(pair.Key)).Append(": ")
                  .Append(MiniJson.Write(pair.Value));
            }

            sb.Append("\r\n}\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static void LoadMap(string code)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var path = FileFor(code);

            try
            {
                if (File.Exists(path))
                {
                    var obj = MiniJson.ParseObject(File.ReadAllText(path, Encoding.UTF8));
                    if (obj != null)
                    {
                        foreach (var pair in obj)
                        {
                            var text = pair.Value as string;
                            if (text != null) map[pair.Key] = text;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("读取语言文件失败（用内置默认）：" + path, ex);
            }

            _map = map;
        }

        private static string ReadSavedLanguage()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                var obj = MiniJson.ParseObject(File.ReadAllText(SettingsPath, Encoding.UTF8));
                return obj == null ? null : MiniJson.GetString(obj, "language");
            }
            catch (Exception ex)
            {
                AppLog.Write("读取语言设置失败：" + SettingsPath, ex);
                return null;
            }
        }

        private static void SaveLanguage(string code)
        {
            try
            {
                File.WriteAllText(SettingsPath, MiniJson.WriteObject("language", code),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                AppLog.Write("保存语言设置失败：" + SettingsPath, ex);
            }
        }

        private static void EnsureInit()
        {
            if (!_initialized) Initialize();
        }

        private static int IndexOf(string code)
        {
            var normalized = Normalize(code);
            for (var i = 0; i < Supported.Length; i++)
            {
                if (string.Equals(Supported[i].Code, normalized, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return 0;
        }

        // ============================================================
        //  内置默认词表：key, zh-cn, en-us
        // ============================================================

        private sealed class DefaultTable
        {
            private readonly List<string[]> _rows = new List<string[]>();
            private readonly Dictionary<string, string[]> _index =
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            public int Count { get { return _rows.Count; } }

            public void Add(string key, string zh, string en)
            {
                // 同一个键写两遍的后果：locales\*.json 里会出现两个同名键（后一条吃掉前一条），
                // "还缺几条"的统计也会算错。开发期就喊出来，别等发布后才发现。
                if (_index.ContainsKey(key))
                {
                    AppLog.Write("语言词表里有重复的键（后一条被忽略）：" + key);
                    return;
                }

                var row = new string[] { zh, en };
                _rows.Add(new string[] { key, zh, en });
                _index[key] = row;
            }

            public string KeyAt(int index) { return _rows[index][0]; }
            public string ValueAt(int index, int lang) { return _rows[index][lang + 1]; }
            public string[] Find(string key)
            {
                string[] row;
                return _index.TryGetValue(key, out row) ? row : null;
            }
        }

        private static readonly DefaultTable Defaults = BuildDefaults();

        private static DefaultTable BuildDefaults()
        {
            var t = new DefaultTable();

            // ---- 程序与侧栏 ----
            t.Add("app.title", "TrpgMaps · 跑团地图投屏", "TrpgMaps · TRPG Map Display");
            t.Add("sidebar.expand", "展开", "Expand");
            t.Add("sidebar.map", "地图设置", "Map");
            t.Add("sidebar.network", "网络", "Network");
            t.Add("sidebar.draw", "绘图", "Draw");
            t.Add("sidebar.audio", "音频", "Audio");
            t.Add("sidebar.fullscreen", "全屏", "Fullscreen");
            t.Add("sidebar.about", "关于", "About");

            // ---- 通用 ----
            t.Add("common.confirm", "确认", "Confirm");
            t.Add("common.cancel", "取消", "Cancel");
            t.Add("common.close", "关闭", "Close");

            // ---- 关闭确认 ----
            t.Add("close.title", "退出 TrpgMaps", "Quit TrpgMaps");
            t.Add("close.text", "确定要退出吗？正在投屏的地图会立即断开。",
                "Quit now? The map being displayed will disconnect immediately.");

            // ---- 语言 ----
            t.Add("lang.title", "界面语言", "Language");
            t.Add("lang.hint", "素材里的文件夹名（locales.toml）跟随这里的设置。",
                "Folder names in locales.toml follow this setting.");
            t.Add("lang.recommended", "（推荐）", " (Recommended)");
            t.Add("lang.current", "当前：{0}", "Current: {0}");
            t.Add("lang.apply", "应用", "Apply");
            t.Add("lang.changed.title", "语言已切换", "Language changed");
            t.Add("lang.changed.text", "界面已切换到「{0}」。\r\n点「取消」立即改回「{1}」，不操作则倒计时结束自动回退。",
                "The interface is now in {0}.\r\nChoose Cancel to go back to {1}; otherwise it reverts when the countdown ends.");
            t.Add("lang.keep", "保留", "Keep");
            t.Add("lang.revert", "取消", "Cancel");
            // ⚠️ 占位符顺序固定为「{0}=剩余秒数，{1}=目标语言」——ConfirmDialog 就是按这个顺序传参的。
            // 英文按语序要反过来读，但**下标不能反**（反了会显示成 "Switching back to 15 in 简体中文s"）。
            t.Add("lang.countdown", "{0} 秒后自动改回「{1}」", "Switching back to \"{1}\" in {0}s");

            // ---- 绘图面板 ----
            t.Add("draw.title", "绘图", "Draw");
            t.Add("draw.kind", "绘制分类", "Category");
            t.Add("draw.terrain", "地形", "Terrain");
            t.Add("draw.entity", "实体", "Entity");
            t.Add("draw.item", "物品", "Item");
            t.Add("draw.brushmode", "画笔模式", "Brush mode");
            t.Add("draw.manual", "手动", "Manual");
            t.Add("draw.brush", "笔刷", "Brush");
            t.Add("draw.selection", "选区方式（笔刷模式）", "Selection (brush mode)");
            t.Add("draw.strict", "严格", "Strict");
            t.Add("draw.loose", "宽泛", "Loose");
            t.Add("draw.shape", "笔刷形状（笔刷模式）", "Brush shape (brush mode)");
            t.Add("draw.line", "线形", "Line");
            t.Add("draw.cone", "锥形/扇形", "Cone / Fan");
            t.Add("draw.cube", "立方", "Cube");
            t.Add("draw.sphere", "球形/圆形", "Sphere / Circle");
            t.Add("draw.fan53", "张角 53°（D&D 喷吐）", "Angle 53° (D&D breath)");
            t.Add("draw.fan60", "张角 60°", "Angle 60°");
            t.Add("draw.fan90", "张角 90°（四分之一圆）", "Angle 90° (quarter circle)");
            t.Add("draw.fan120", "张角 120°", "Angle 120°");
            t.Add("draw.eraser", "橡皮擦", "Eraser");
            t.Add("draw.clearasset", "清空本类", "Clear asset");
            t.Add("draw.clearall", "清空全部", "Clear all");
            t.Add("draw.empty", "（「{0}」还没有素材）\r\n把图片放进 exe 同级的 {1}\\ 目录，再点「重新扫描」",
                "(No assets in \"{0}\" yet)\r\nPut images under {1}\\ next to the exe, then press Rescan");
            t.Add("draw.count", "{0}\\ 共 {1} 项", "{0}\\ — {1} item(s)");
            t.Add("draw.folder.count", "{0} 项", "{0} item(s)");
            t.Add("draw.rescan", "重新扫描", "Rescan");
            t.Add("draw.unsupported", "不支持此格式", "Unsupported format");
            t.Add("draw.noasset", "还没选素材 —— 先在上面点一项", "No asset selected — pick one above");
            t.Add("draw.current", "当前：{0}（{1}）", "Selected: {0} ({1})");
            t.Add("draw.painted", "本素材已画 {0} 格（「清空本类」只摘这一张）",
                "{0} cell(s) painted with this asset (Clear asset only removes this one)");
            t.Add("draw.erasing", "橡皮擦：擦掉「{0}」这一层", "Eraser: wipe the \"{0}\" layer");
            t.Add("draw.origin", "原点：{0} ({1},{2})", "Origin: {0} ({1},{2})");
            t.Add("draw.origin.center", "格心", "cell centre");
            t.Add("draw.origin.node", "网格点", "grid point");
            t.Add("draw.radius.fixed", "半径 {0}", "Radius {0}");
            t.Add("draw.radius.moving", "—— 移动鼠标量半径，再点一下", "— move the mouse to size, then click");
            t.Add("draw.total", "已画 {0} 格", "{0} cell(s) painted");
            t.Add("draw.strict.hint", "严格：只有格心落在形状里的格才算",
                "Strict: only cells whose centre is inside the shape");
            t.Add("draw.loose.hint", "宽泛：形状真正压到的格都算（只相切不算）",
                "Loose: every cell the shape touches (merely tangent doesn't count)");
            t.Add("draw.hint.manual", "按住左键在地图上拖动，扫过的格子覆盖素材。",
                "Hold the left button and drag on the map to paint the cells you sweep.");
            t.Add("draw.hint.brush", "先点一下放原点，再拖出半径后点第二下成形；扇形还要转好角度点第三下。右键取消。",
                "Click to place the origin, move to size, click again to commit; cones need a third click for direction. Right-click cancels.");
            t.Add("draw.trait.none", "无特殊效果", "No special effect");
            t.Add("draw.trait.hard", "困难地形", "Difficult terrain");
            t.Add("draw.trait.cover", "遮蔽", "Cover");
            t.Add("draw.trait.obstacle", "障碍", "Obstacle");
            t.Add("draw.trait.climb", "可攀爬", "Climbable");
            t.Add("draw.trait.damage", "伤害 {0}", "Damage {0}");

            t.Add("map.fit.fill", "拉伸（铺满）", "Stretch (fill)");
            t.Add("map.fit.contain", "填充（保持比例）", "Fit (keep ratio)");
            t.Add("map.rotate90", "顺时针 90°", "90° clockwise");
            t.Add("map.rotate180", "旋转 180°", "Rotate 180°");
            t.Add("map.import", "导入图片", "Import image");
            t.Add("map.refresh", "刷新", "Refresh");
            t.Add("map.display.fill", "拉伸显示", "Stretch");
            t.Add("map.display.contain", "填充显示", "Fit");
            t.Add("net.save.restart", "保存并重开服务", "Save & restart server");
            t.Add("net.fw", "放行防火墙（需管理员确认）", "Allow through firewall (admin)");
            t.Add("net.pass.save", "保存密码并刷新地址", "Save password & refresh address");
            t.Add("audio.prev", "上一曲", "Previous");
            t.Add("audio.next", "下一曲", "Next");
            t.Add("audio.dll.hint", "把 TrpgAudio.dll 放回程序目录即可恢复（它就在安装包里）。",
                "Put TrpgAudio.dll back next to the exe (it ships with the installer).");

            // ---- 地图面板 ----
            t.Add("map.title", "地图设置", "Map Settings");
            t.Add("map.gridcolor", "网格颜色：", "Grid colour:");
            t.Add("map.fit", "底图显示模式：", "Map fit:");
            t.Add("map.rotate", "底图旋转（顺时针）：", "Map rotation (clockwise):");
            t.Add("map.list", "底图列表", "Map list");
            t.Add("map.upload", "上传底图", "Upload map");
            t.Add("map.remove", "撤掉底图", "Remove map");
            t.Add("map.remove.hint", "撤掉后只剩网格（maps\\ 里的文件不删）",
                "Grid only; files stay in maps\\");
            t.Add("map.ready", "就绪", "Ready");

            // ---- 网络面板 ----
            t.Add("net.title", "网络连接", "Network");
            t.Add("net.ip", "IP 地址", "IP address");
            t.Add("net.port", "端口（1-65535）", "Port (1-65535)");
            t.Add("net.room", "房间号（每次启动都会换一个新的）", "Room number (new on every start)");
            t.Add("net.pass", "房间密码（留空 = 不需要密码）", "Room password (empty = none)");
            t.Add("net.url", "玩家地址", "Player address");
            t.Add("net.qr", "手机扫码即可加入（地址里带着房间号和密码）",
                "Scan with a phone to join (room & password are in the address)");
            t.Add("net.players", "在线玩家", "Online players");
            t.Add("net.save", "保存", "Save");

            // ---- 音频面板 ----
            t.Add("audio.title", "音乐播放", "Music");
            t.Add("audio.hint1", "音乐放在 audio 目录，可多层子文件夹",
                "Music goes in audio\\ (nested folders fine)");
            t.Add("audio.supported", "支持 {0}；同名 .lrc 当歌词",
                "Supports {0} · .lrc shows lyrics");
            t.Add("audio.none", "（还没选曲目）", "(no track selected)");
            t.Add("audio.volume", "音量", "Volume");
            t.Add("audio.tracks", "曲目列表（点一下播放）", "Tracks (click to play)");
            t.Add("audio.showlyrics", "显示歌词", "Show lyrics");
            t.Add("audio.hidelyrics", "隐藏歌词", "Hide lyrics");
            t.Add("audio.play", "播放", "Play");
            t.Add("audio.pause", "暂停", "Pause");
            t.Add("audio.mode.loop", "单曲循环", "Repeat one");
            t.Add("audio.mode.sequence", "顺序播放", "In order");
            t.Add("audio.mode.shuffle", "随机播放", "Shuffle");

            // ---- 关于面板 ----
            t.Add("about.title", "关于", "About");
            t.Add("about.tagline", "跑团地图投屏工具 · Win7 / 32 位兼容版",
                "TRPG map display · Win7 / 32-bit");
            t.Add("about.author", "作者", "Author");
            t.Add("about.repo", "源码仓库", "Source repository");
            t.Add("about.version", "版本与更新", "Version & updates");
            t.Add("about.check", "检查更新", "Check for updates");
            t.Add("about.installed", "当前为安装版", "Installed version");
            t.Add("about.portable", "当前为便携版", "Portable version");
            t.Add("about.copyright", "Copyright © {0}　保留所有权利。",
                "Copyright © {0}. All rights reserved.");

            // ---- 安装/更新弹窗（沿用现有实现，这里只加最常用的两条）----
            t.Add("update.title", "发现新版本", "Update available");
            t.Add("update.later", "下次一定", "Later");
            t.Add("update.update", "更新", "Update");

            // ============================================================
            //  v1.0.4：把「上一次漏掉的那批」补齐
            //
            //  这一段的每一条都是从代码里**搬出来**的硬编码文字（状态行、弹框、
            //  文件选择框、端口提示……）。以前它们直接写在 new Label(...) / MessageBox
            //  里，切到英文还是中文 —— 词表里根本没有这些键。
            // ============================================================

            // ---- 网格尺度 / 底图 ----
            t.Add("grid.scale", "网格尺度 (cm/格) {0}", "Grid scale (cm/cell) {0}");
            t.Add("map.rotation.now", "当前 {0}°", "Now {0}°");
            t.Add("map.grid.auto", "自动（跟随底图）", "Auto (follow map)");
            t.Add("map.grid.white", "白色", "White");
            t.Add("map.grid.black", "黑色", "Black");
            t.Add("map.grid.line.white", "白线", "white lines");
            t.Add("map.grid.line.black", "黑线", "black lines");
            t.Add("map.grid.fixed", "固定{0}", "Fixed: {0}");
            t.Add("map.grid.nomap", "自动 → 白线（未选底图，深色底）",
                "Auto → white lines\r\n(no map, dark background)");
            t.Add("map.grid.unreadable", "自动 → {0}（底图读不出来，按白线处理）",
                "Auto → {0}\r\n(map unreadable, treated as white)");
            t.Add("map.grid.measured", "自动 → {0}（底图平均亮度 {1}，{2}）",
                "Auto → {0}\r\n(mean brightness {1}, {2})");
            t.Add("map.grid.dark", "偏暗", "dark");
            t.Add("map.grid.bright", "偏亮", "bright");
            t.Add("map.pick.title", "选择底图图片", "Choose a map image");
            t.Add("map.pick.filter",
                "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|所有文件|*.*",
                "Image files|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files|*.*");
            t.Add("map.uploading", "上传中…", "Uploading…");
            t.Add("map.import.ok", "导入成功", "Imported");
            t.Add("map.import.fail", "导入失败：{0}", "Import failed: {0}");
            t.Add("map.none", "本来就没有底图", "There was no map to begin with");
            t.Add("map.removed", "已撤掉：{0}", "Removed: {0}");

            // ---- 防火墙 ----
            t.Add("fw.title", "TrpgMaps - 防火墙", "TrpgMaps - Firewall");
            t.Add("fw.label.allowed", "防火墙：已放行", "Firewall: allowed");
            t.Add("fw.label.blocked", "防火墙：被阻止，手机连不上", "Firewall: blocked — phones can't connect");
            t.Add("fw.label.norule", "防火墙：未放行，首次连接会弹系统授权框",
                "Firewall: not allowed yet — Windows will ask on first connect");
            t.Add("fw.label.unknown", "防火墙：状态未知（{0}）", "Firewall: unknown ({0})");
            t.Add("fw.blocked.title", "TrpgMaps - 局域网被防火墙挡住", "TrpgMaps - Blocked by Windows Firewall");
            t.Add("fw.blocked.text",
                "Windows 防火墙里有一条『阻止』本程序接收局域网连接的规则。\r\n" +
                "手机 / 平板扫码也打不开玩家页面，就是它造成的。\r\n\r\n" +
                "是否现在放行？（会弹一次管理员确认）",
                "Windows Firewall has a rule that BLOCKS this program from receiving LAN connections.\r\n" +
                "That is why phones/tablets can't open the player page even by scanning the QR code.\r\n\r\n" +
                "Allow it now? (One administrator prompt will follow)");
            t.Add("fw.done.text", "已放行。现在用手机连同一个 WiFi，扫码或输入上面的玩家地址即可打开。",
                "Allowed. Connect the phone to the same Wi-Fi, then scan the code or open the player address above.");
            t.Add("fw.repair.ok", "防火墙已放行，手机 / 平板现在可以打开玩家页面了。",
                "Firewall allows it — phones/tablets can open the player page now.");
            t.Add("fw.repair.fail", "修复没有成功，详情见程序目录下的 app.log。",
                "The repair didn't succeed; see app.log in the program folder.");
            t.Add("fw.cancelled", "已取消管理员授权，防火墙规则没有改动。",
                "Administrator prompt was cancelled; firewall rules were left untouched.");
            t.Add("fw.elevate.fail", "无法启动管理员进程：{0}", "Couldn't start the elevated process: {0}");
            t.Add("fw.repair.exit", "修复没有成功（退出码 {0}），详情见 app.log。",
                "The repair didn't succeed (exit code {0}); see app.log for details.");
            t.Add("fw.error.registry", "打不开防火墙规则表", "Can't open the firewall rule table");

            // ---- 网络 / 房间 ----
            t.Add("net.fw.state.allowed", "防火墙已放行，手机应该能正常访问",
                "Firewall allows it — phones should be able to connect");
            t.Add("net.fw.state.fixed", "防火墙已放行，手机现在可以访问了",
                "Firewall fixed — phones can connect now");
            t.Add("net.fw.state.fail", "防火墙放行失败：{0}", "Firewall fix failed: {0}");
            t.Add("net.pass.cleared", "已清除房间密码（任何人都能加入）",
                "Room password cleared (anyone can join)");
            t.Add("net.pass.set", "房间密码已设置（{0} 个字符）", "Room password set ({0} characters)");
            t.Add("net.room.lock", "锁定房间", "Lock room");
            t.Add("net.room.unlock", "解锁房间", "Unlock room");
            // 注：「在线玩家」已经有 net.players 这一条，这里别再写一遍 ——
            // 重复键会让 locales\*.json 里出现两个同名键。
            t.Add("net.kick", "踢出选中玩家", "Kick selected player");
            t.Add("net.kick.title", "踢出玩家", "Kick player");
            t.Add("net.kick.confirm", "确定要踢出 {0} 吗？\r\n（他带着原令牌仍可重新进入）",
                "Kick {0}?\r\n(They can still re-join with their existing token)");
            t.Add("net.left", "{0} 离开了房间", "{0} left the room");
            t.Add("net.err.ip", "请先选择一个 IP 地址", "Pick an IP address first");
            t.Add("net.err.port", "端口必须是 1-65535 之间的整数",
                "Port must be an integer between 1 and 65535");
            t.Add("net.err.save", "失败：{0}", "Failed: {0}");
            t.Add("net.restarting", "正在用新地址重开服务…", "Restarting the server on the new address…");
            t.Add("net.switched", "已切换到 {0}（底图与名册已保留）",
                "Switched to {0} (map and roster kept)");
            t.Add("net.reverted", "新端口没起来（{0}），已退回 {1}",
                "New port failed ({0}); reverted to {1}");
            t.Add("net.revert.failed", "新端口起不来（{0}），原端口也回不去了",
                "New port failed ({0}) and the old port can't be restored either");
            t.Add("net.unchanged", "配置未变化", "Nothing changed");
            t.Add("net.port.auto", "端口 {0} 不可用，已自动改用 {1}",
                "Port {0} is unavailable; switched to {1}");
            t.Add("net.port.auto.text",
                "默认端口 {0} 用不了：{1}\r\n\r\n已自动改用端口 {2}，玩家地址见「网络」面板。",
                "The default port {0} is unusable: {1}\r\n\r\nSwitched to port {2}; see the player address in the Network panel.");
            t.Add("net.port.dead", "内置服务器启动失败：找不到可用端口。\r\n{0}",
                "The built-in server couldn't start: no usable port.\r\n{0}");
            t.Add("net.reason.unknown", "未知原因", "unknown reason");

            // ---- 音频 ----
            t.Add("audio.no.tracks", "audio 目录里还没有音乐", "No music in the audio folder yet");
            t.Add("audio.track.count", "{0} 首", "{0} track(s)");
            t.Add("audio.mode.current", "播放模式：{0}", "Play mode: {0}");
            t.Add("audio.open.fail", "打不开：{0}", "Can't open: {0}");
            t.Add("audio.lyrics.missing", "没有找到同名 .lrc", "No matching .lrc found");
            t.Add("audio.lyrics.loaded", "歌词已加载", "Lyrics loaded");
            t.Add("audio.unsupported", "（不支持此格式）", " (unsupported format)");
            t.Add("audio.lyrics.empty",
                "（没有歌词：把同名的 .lrc 放在音乐文件旁边就会自动显示）",
                "(No lyrics — put a matching .lrc next to the audio file and it shows up here)");

            // ---- 更新 ----
            t.Add("update.checking", "正在检查更新…", "Checking for updates…");
            t.Add("update.version.line",
                "当前版本 {0}\r\n更新源：Gitee 优先，失败自动转 GitHub",
                "Current version {0}\r\nSource: Gitee first, then GitHub");
            t.Add("update.skipped.line", "\r\n已忽略版本：v{0}", "\r\nSkipped version: v{0}");
            t.Add("update.check.failed.line", "检查更新失败。\r\n{0}", "Update check failed.\r\n{0}");
            t.Add("update.check.failed.title", "检查更新失败", "Update check failed");
            t.Add("update.check.failed.text",
                "没能连上更新服务器。\r\n\r\n{0}\r\n\r\n" +
                "提示：老版本 Windows 7 如果从没更新过根证书，会连不上 HTTPS 站点，" +
                "装一次 Windows Update 里的根证书更新即可。也可以直接到仓库的 Releases 页手动下载。",
                "Couldn't reach the update server.\r\n\r\n{0}\r\n\r\n" +
                "Tip: an old Windows 7 install without root-certificate updates can't reach HTTPS sites; " +
                "installing the root certificate update from Windows Update fixes it. " +
                "You can also download the package manually from the repository's Releases page.");
            t.Add("update.latest.line", "已是最新版本（{0}）\r\n检查源：{1}",
                "Up to date ({0})\r\nSource: {1}");
            t.Add("update.latest.title", "已是最新版本", "Already up to date");
            t.Add("update.latest.text", "当前 {0} 已经是仓库里的最新版本了。",
                "{0} is already the latest version in the repository.");
            t.Add("update.found.line", "发现新版本 v{0}\r\n检查源：{1}",
                "New version v{0} available\r\nSource: {1}");
            t.Add("update.busy.title", "正在下载更新", "Downloading update");
            t.Add("update.busy.text", "正在连接下载升级包，请稍候…",
                "Connecting and downloading the package, please wait…");
            t.Add("update.busy.done", "下载完成（来自 {0}），准备应用更新…",
                "Downloaded from {0}; preparing to apply…");
            t.Add("update.busy.failed", "下载失败。", "Download failed.");
            t.Add("update.failed.title", "更新失败", "Update failed");
            t.Add("update.failed.download",
                "升级包下载失败。\r\n\r\n{0}\r\n\r\n也可以到仓库的 Releases 页手动下载安装包。",
                "Couldn't download the update package.\r\n\r\n{0}\r\n\r\n" +
                "You can also download the installer manually from the repository's Releases page.");
            t.Add("update.source.unknown", "未知", "unknown");
            t.Add("update.dialog.current", "当前版本 {0}   →   最新版本 v{1}",
                "Current {0}   →   latest v{1}");
            t.Add("update.dialog.notes", "仓库里有比本机更新的版本，建议更新到最新版。",
                "A newer version is available in the repository; updating is recommended.");
            t.Add("update.dialog.source",
                "更新来源：{0}　　升级包会从发布页（releases）下载。\r\n" +
                "便携版只覆盖程序文件；安装版还会顺带修正注册信息。",
                "Source: {0}. The package is downloaded from the Releases page.\r\n" +
                "Portable builds only replace program files; installed builds also fix the registry entries.");
            t.Add("update.dialog.auto", "自动", "automatic");
            t.Add("update.dialog.never", "不再提示", "Don't remind me");
            t.Add("update.error.bad.manifest", "{0}: 清单格式不对", "{0}: bad manifest");
            t.Add("update.error.no.source", "没有可用的更新源。", "No usable update source.");
            t.Add("update.error.no.updater",
                "升级包里没有找到升级程序 TrpgMapsUpdater.exe，已中止（请到发布页手动下载安装包）。",
                "The package doesn't contain TrpgMapsUpdater.exe; aborted (please download the installer from the Releases page).");
            t.Add("update.error.stage", "准备升级失败：{0}", "Failed to prepare the update: {0}");
            t.Add("update.dialog.ok", "知道了", "Got it");
            t.Add("busy.error", "出错了：{0}", "Error: {0}");

            // ---- 单位 ----
            t.Add("unit.feet", "{0} 尺", "{0} ft");
            t.Add("unit.feet.cells", "{0}（{1} 格）", "{0} ({1} cells)");

            // ---- 核心层（错误文字会直接显示在状态行 / 弹框里）----
            t.Add("core.unsupported", "不支持此格式", "Unsupported format");
            t.Add("core.lan.no", "{0} · 不可用于局域网", "{0} · not usable on the LAN");
            t.Add("core.loopback", "本机回环", "loopback");
            t.Add("core.unknown", "未知错误", "unknown error");
            t.Add("core.audio.no.dll", "缺少 TrpgAudio.dll（音频功能需要它，放在程序同目录即可）",
                "TrpgAudio.dll is missing (put it next to the exe to enable audio)");
            t.Add("core.audio.device", "音频设备打不开：{0}", "Can't open the audio device: {0}");
            t.Add("core.audio.load", "音频库加载失败：{0}", "Failed to load the audio library: {0}");
            t.Add("core.port.reserved",
                "该端口被 Windows 保留（Hyper-V / WSL / Docker 预留的端口段），没有占用者可以关闭，必须换一个端口",
                "Windows has reserved this port (for Hyper-V / WSL / Docker). Nothing can be closed to free it — pick another port.");
            t.Add("core.port.used", "该端口已被其它程序占用", "Another program is already using this port");
            t.Add("core.settings.ip.empty", "请选择一个 IP 地址", "Pick an IP address");
            t.Add("core.settings.ip.missing", "本机当前没有地址 {0}，请刷新后重新选择",
                "This machine has no address {0}; refresh and pick again");
            t.Add("core.settings.port", "端口必须在 1-65535 之间", "Port must be between 1 and 65535");
            t.Add("core.settings.saved", "已保存", "Saved");
            t.Add("core.upload.ok", "上传成功", "Uploaded");
            t.Add("core.upload.fail", "上传失败：{0}", "Upload failed: {0}");
            t.Add("core.upload.noname", "文件名为空", "Empty file name");
            t.Add("core.upload.badformat", "不支持的格式", "Unsupported format");
            t.Add("core.upload.exists", "文件名已存在", "That name already exists");
            t.Add("core.upload.dup", "图片内容重复（MD5 相同）", "Duplicate image content (same MD5)");

            // ---- 启动 / 命令行入口 ----
            t.Add("app.start.fail", "程序启动失败：\r\n{0}", "Failed to start:\r\n{0}");
            t.Add("reg.cancelled", "已取消管理员授权，注册信息没有改动。",
                "Administrator prompt cancelled; registration info unchanged.");
            t.Add("reg.done", "注册信息已刷新。\r\n\r\n安装位置：{0}\r\n版本：{1}",
                "Registration refreshed.\r\n\r\nInstalled at: {0}\r\nVersion: {1}");
            t.Add("reg.fail", "修复失败：{0}", "Repair failed: {0}");

            return t;
        }
    }
}
