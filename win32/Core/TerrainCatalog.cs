using System;
using System.Collections.Generic;
using System.IO;

namespace TrpgMaps
{
    /// <summary>
    /// 绘图素材的三大分类。三者的**图层关系**固定：
    ///   地形（terrain）在最下面，实体（entity）与物品（item）同级、都在地形之上。
    ///
    /// 分类名同时也是素材目录名：terrain\ / entity\ / item\。
    /// 目前只有 terrain\ 里有素材，另外两个目录会自动建出来留空。
    /// </summary>
    internal static class DrawKind
    {
        public const string Terrain = "terrain";
        public const string Entity = "entity";
        public const string Item = "item";

        public static readonly string[] All = { Terrain, Entity, Item };

        /// <summary>把任意输入规整成三个合法分类之一（默认地形）。</summary>
        public static string Normalize(string kind)
        {
            if (string.Equals(kind, Entity, StringComparison.OrdinalIgnoreCase)) return Entity;
            if (string.Equals(kind, Item, StringComparison.OrdinalIgnoreCase)) return Item;
            return Terrain;
        }

        public static bool IsValid(string kind)
        {
            return string.Equals(kind, Terrain, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, Entity, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, Item, StringComparison.OrdinalIgnoreCase);
        }

        public static string Label(string kind)
        {
            var normalized = Normalize(kind);
            if (normalized == Entity) return "实体";
            if (normalized == Item) return "物品";
            return "地形";
        }

        /// <summary>图层序号：数字越大越靠上。地形在人物/物品之下，人物与物品同级。</summary>
        public static int Layer(string kind)
        {
            return Normalize(kind) == Terrain ? 0 : 1;
        }
    }

    /// <summary>一张绘图素材（地形 / 实体 / 物品）。</summary>
    internal sealed class TerrainAsset
    {
        public string Kind = DrawKind.Terrain;
        /// <summary>显示名（一般就是去掉扩展名的文件名）。</summary>
        public string Name = string.Empty;
        /// <summary>文件名（含扩展名），也是画在格子上的唯一标识。</summary>
        public string File = string.Empty;
        public string Path = string.Empty;
        public string Url = string.Empty;

        // ---- terrain_properties.json 里的四个核心标记 ----
        /// <summary>劣势地形（困难地形，降低速度）。</summary>
        public bool Disadvantageous;
        /// <summary>提供遮蔽。</summary>
        public bool Cover;
        /// <summary>障碍，阻止实体穿越。</summary>
        public bool Obstacle;
        /// <summary>可攀爬。</summary>
        public bool Climb;

        // ---- 三个附加字段（可缺省：空串 / 1.0） ----
        /// <summary>伤害描述，例如 "2d6 火焰"。空串表示无伤害。</summary>
        public string Damage = string.Empty;
        /// <summary>贴图不透明度 0.1-1.0。雾/烟/火这类要压一点，好让底下的地形透出来。</summary>
        public double Opacity = 1.0;
        /// <summary>规则备注，给 DM 看。</summary>
        public string Note = string.Empty;

        /// <summary>界面上那一行小字，比如 "遮蔽·障碍·可攀爬"。</summary>
        public string TraitText()
        {
            var parts = new List<string>();
            if (Disadvantageous) parts.Add("困难地形");
            if (Cover) parts.Add("遮蔽");
            if (Obstacle) parts.Add("障碍");
            if (Climb) parts.Add("可攀爬");
            if (!string.IsNullOrEmpty(Damage)) parts.Add("伤害 " + Damage);
            return parts.Count == 0 ? "无特殊效果" : string.Join("·", parts.ToArray());
        }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// 绘图素材仓库：扫描 &lt;exe目录&gt;\terrain\ 等目录，并读取同目录下的
    /// &lt;分类名&gt;_properties.json 补充规则属性。
    ///
    /// 约定（与用户给的那份 terrain\terrain_properties.json 一致）：
    ///  - `file` 是**相对于本 json 所在目录**的文件名；
    ///  - `disadvantageous` 劣势地形（降低速度）、`cover` 遮蔽、
    ///    `obstacle` 障碍（阻止实体穿越）、`climb` 可攀爬；
    ///  - 额外支持 `damage`（伤害描述）、`opacity`（贴图不透明度）、`note`（说明）。
    ///
    /// **json 里没写到的图片不会消失**：磁盘上存在、json 里缺项的素材
    /// 会以「全 false」的默认属性列出来，这样直接往目录里丢一张图就能用，
    /// 不必先改 json。
    /// </summary>
    internal sealed class TerrainCatalog
    {
        private readonly object _gate = new object();
        private Dictionary<string, List<TerrainAsset>> _byKind = new Dictionary<string, List<TerrainAsset>>();
        private Dictionary<string, TerrainAsset> _index = new Dictionary<string, TerrainAsset>(StringComparer.Ordinal);

        public TerrainCatalog()
        {
            Reload();
        }

        /// <summary>某个分类的目录（不存在则创建）。</summary>
        public static string FolderFor(string kind)
        {
            return Path.Combine(AppEnv.Root, DrawKind.Normalize(kind));
        }

        /// <summary>某个分类的属性文件（与素材同目录）。</summary>
        public static string PropertiesFileFor(string kind)
        {
            var normalized = DrawKind.Normalize(kind);
            return Path.Combine(FolderFor(normalized), normalized + "_properties.json");
        }

        /// <summary>素材目录里的合法图片扩展名。</summary>
        public static bool IsAllowedAsset(string fileName)
        {
            return AppEnv.IsAllowedImage(fileName);
        }

        /// <summary>按分类取素材列表（返回内部快照，调用方不要改）。</summary>
        public List<TerrainAsset> Of(string kind)
        {
            var normalized = DrawKind.Normalize(kind);
            lock (_gate)
            {
                List<TerrainAsset> list;
                return _byKind.TryGetValue(normalized, out list) ? list : new List<TerrainAsset>();
            }
        }

        /// <summary>三个分类合起来的总数，状态行上显示用。</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    var total = 0;
                    foreach (var pair in _byKind) total += pair.Value.Count;
                    return total;
                }
            }
        }

        /// <summary>按「分类 + 文件名」找素材；找不到返回 null。</summary>
        public TerrainAsset Find(string kind, string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            var normalized = DrawKind.Normalize(kind);
            lock (_gate)
            {
                TerrainAsset asset;
                if (_index.TryGetValue(normalized + "/" + file, out asset)) return asset;
                return null;
            }
        }

        /// <summary>把任意输入（可能只有文件名）解析成素材；实在找不到返回 null。</summary>
        public TerrainAsset Resolve(string kind, string file)
        {
            var found = Find(kind, file);
            if (found != null) return found;

            // 兼容：只给了显示名、或者带上了分类前缀
            if (string.IsNullOrEmpty(file)) return null;
            var name = Path.GetFileNameWithoutExtension(file);
            var wanted = DrawKind.Normalize(kind);
            foreach (var asset in Of(wanted))
            {
                if (string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase)) return asset;
            }
            return null;
        }

        /// <summary>重新扫描全部素材目录。</summary>
        public void Reload()
        {
            var byKind = new Dictionary<string, List<TerrainAsset>>();
            var index = new Dictionary<string, TerrainAsset>(StringComparer.Ordinal);

            foreach (var kind in DrawKind.All)
            {
                var folder = FolderFor(kind);
                var list = new List<TerrainAsset>();

                try
                {
                    if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                }
                catch (Exception ex)
                {
                    AppLog.Write("创建素材目录失败：" + folder, ex);
                }

                // 1) 先按磁盘上真实存在的图片建条目
                var byFile = new Dictionary<string, TerrainAsset>(StringComparer.Ordinal);
                try
                {
                    if (Directory.Exists(folder))
                    {
                        var files = Directory.GetFiles(folder);
                        Array.Sort(files, StringComparer.Ordinal);
                        foreach (var file in files)
                        {
                            var name = Path.GetFileName(file);
                            if (!IsAllowedAsset(name)) continue;
                            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                            var asset = new TerrainAsset();
                            asset.Kind = kind;
                            asset.File = name;
                            asset.Name = Path.GetFileNameWithoutExtension(name);
                            asset.Path = file;
                            asset.Url = BuildUrl(kind, name);
                            list.Add(asset);
                            byFile[name] = asset;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("扫描素材目录失败：" + folder, ex);
                }

                // 2) 再用 properties.json 补属性
                ApplyProperties(kind, byFile);

                list.Sort(delegate (TerrainAsset a, TerrainAsset b)
                {
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                });

                byKind[kind] = list;
                foreach (var asset in list) index[kind + "/" + asset.File] = asset;
            }

            lock (_gate)
            {
                _byKind = byKind;
                _index = index;
            }
        }

        private static void ApplyProperties(string kind, Dictionary<string, TerrainAsset> byFile)
        {
            var path = PropertiesFileFor(kind);
            if (!File.Exists(path)) return;

            string text;
            try
            {
                text = File.ReadAllText(path, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLog.Write("读取素材属性失败：" + path, ex);
                return;
            }

            var parsed = MiniJson.Parse(text) as List<object>;
            if (parsed == null)
            {
                AppLog.Write("素材属性文件不是合法 JSON 数组（已忽略）：" + path);
                return;
            }

            foreach (var raw in parsed)
            {
                var obj = raw as Dictionary<string, object>;
                if (obj == null) continue;

                var file = MiniJson.GetString(obj, "file");
                if (string.IsNullOrEmpty(file)) file = MiniJson.GetString(obj, "name");
                if (string.IsNullOrEmpty(file)) continue;

                TerrainAsset asset;
                if (!byFile.TryGetValue(file, out asset))
                {
                    // json 里写了、但目录里没有这张图 —— 只记一笔日志，不算错误
                    AppLog.Write("素材属性里的文件不存在，已跳过：" + kind + "/" + file);
                    continue;
                }

                asset.Disadvantageous = MiniJson.GetBool(obj, "disadvantageous", false);
                asset.Cover = MiniJson.GetBool(obj, "cover", false);
                asset.Obstacle = MiniJson.GetBool(obj, "obstacle", false);
                asset.Climb = MiniJson.GetBool(obj, "climb", false);

                var damage = MiniJson.GetString(obj, "damage");
                if (!string.IsNullOrEmpty(damage)) asset.Damage = damage;

                var note = MiniJson.GetString(obj, "note");
                if (!string.IsNullOrEmpty(note)) asset.Note = note;

                var name = MiniJson.GetString(obj, "name");
                if (!string.IsNullOrEmpty(name)) asset.Name = name;

                object opacityValue;
                if (obj.TryGetValue("opacity", out opacityValue) && opacityValue != null)
                {
                    double opacity;
                    if (double.TryParse(Convert.ToString(opacityValue, System.Globalization.CultureInfo.InvariantCulture),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out opacity))
                    {
                        if (opacity < 0.1) opacity = 0.1;
                        if (opacity > 1.0) opacity = 1.0;
                        asset.Opacity = opacity;
                    }
                }
            }
        }

        public static string BuildUrl(string kind, string file)
        {
            return "/api/terrain/image/" + Uri.EscapeDataString(DrawKind.Normalize(kind)) +
                   "/" + Uri.EscapeDataString(file);
        }

        /// <summary>按「分类/文件名」解析出磁盘路径（防目录穿越）。</summary>
        public static string ResolvePath(string kind, string file)
        {
            if (!DrawKind.IsValid(kind)) return null;
            var safe = Path.GetFileName(file);
            if (!IsAllowedAsset(safe)) return null;
            var full = Path.Combine(FolderFor(kind), safe);
            return File.Exists(full) ? full : null;
        }

        /// <summary>全部素材列表（json 用），带三个分类。</summary>
        public string ToJson()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"kinds\":[");
            for (var k = 0; k < DrawKind.All.Length; k++)
            {
                var kind = DrawKind.All[k];
                if (k > 0) sb.Append(',');
                var list = Of(kind);
                sb.Append(MiniJson.WriteObject(
                    "kind", kind,
                    "label", DrawKind.Label(kind),
                    "count", list.Count));
            }
            sb.Append("],\"assets\":[");
            var first = true;
            foreach (var kind in DrawKind.All)
            {
                foreach (var asset in Of(kind))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(MiniJson.WriteObject(
                        "kind", asset.Kind,
                        "name", asset.Name,
                        "file", asset.File,
                        "url", asset.Url,
                        "disadvantageous", asset.Disadvantageous,
                        "cover", asset.Cover,
                        "obstacle", asset.Obstacle,
                        "climb", asset.Climb,
                        "damage", asset.Damage,
                        "opacity", asset.Opacity,
                        "note", asset.Note));
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
