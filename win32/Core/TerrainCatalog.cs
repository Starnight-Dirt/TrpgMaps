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
            if (normalized == Entity) return Loc.T("draw.entity");
            if (normalized == Item) return Loc.T("draw.item");
            return Loc.T("draw.terrain");
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
        /// <summary>
        /// 相对分类目录的路径（用 <c>/</c> 分隔），例如 <c>character/战士.png</c>。
        /// 根目录下的素材就等于 <see cref="File"/>。
        /// 绘图数据里存的就是它 —— 所以子目录里的素材也能被记住、能被手机端请求到。
        /// </summary>
        public string Relative = string.Empty;
        /// <summary>所在子目录（相对分类目录，用 <c>/</c> 分隔）；根目录为空串。</summary>
        public string Folder = string.Empty;
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
            if (Disadvantageous) parts.Add(Loc.T("draw.trait.hard"));
            if (Cover) parts.Add(Loc.T("draw.trait.cover"));
            if (Obstacle) parts.Add(Loc.T("draw.trait.obstacle"));
            if (Climb) parts.Add(Loc.T("draw.trait.climb"));
            if (!string.IsNullOrEmpty(Damage)) parts.Add(Loc.T("draw.trait.damage", Damage));
            return parts.Count == 0 ? Loc.T("draw.trait.none") : string.Join("·", parts.ToArray());
        }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// 素材目录树里的一个文件夹。
    ///
    /// 只有 entity\ 与 item\ 会扫描子目录（地形仍按扁平处理）；文件夹可以多层嵌套。
    /// <see cref="DisplayName"/> 来自文件夹里的 <c>locales.toml</c>，读不到就用
    /// <see cref="Name"/>（文件夹本名）。
    /// </summary>
    internal sealed class AssetFolder
    {
        /// <summary>文件夹本名（磁盘上的名字）。</summary>
        public string Name = string.Empty;
        /// <summary>按当前界面语言显示的文件夹名。</summary>
        public string DisplayName = string.Empty;
        /// <summary>相对分类目录的路径（用 <c>/</c> 分隔）；根节点为空串。</summary>
        public string Relative = string.Empty;
        /// <summary>嵌套深度：根下的文件夹 = 0。</summary>
        public int Depth;

        public readonly List<AssetFolder> Folders = new List<AssetFolder>();
        public readonly List<TerrainAsset> Assets = new List<TerrainAsset>();

        /// <summary>这个文件夹下（含所有子文件夹）的图片总数。</summary>
        public int TotalCount
        {
            get
            {
                var total = Assets.Count;
                foreach (var folder in Folders) total += folder.TotalCount;
                return total;
            }
        }

        public override string ToString()
        {
            return DisplayName;
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
        /// <summary>文件夹最多嵌套这么多层（防止有人把整个盘符软链进来）。</summary>
        private const int MaxFolderDepth = 8;

        private readonly object _gate = new object();
        private Dictionary<string, List<TerrainAsset>> _byKind = new Dictionary<string, List<TerrainAsset>>();
        private Dictionary<string, AssetFolder> _trees = new Dictionary<string, AssetFolder>();
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

        /// <summary>
        /// 某个分类的目录树（实体 / 物品有子文件夹；地形是空的根节点 + 扁平列表）。
        /// 返回内部快照，调用方不要改。
        /// </summary>
        public AssetFolder TreeOf(string kind)
        {
            var normalized = DrawKind.Normalize(kind);
            lock (_gate)
            {
                AssetFolder root;
                if (_trees.TryGetValue(normalized, out root)) return root;
                return new AssetFolder();
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

        /// <summary>按「分类 + 相对路径」找素材；找不到返回 null。</summary>
        public TerrainAsset Find(string kind, string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            var normalized = DrawKind.Normalize(kind);
            var relative = file.Replace('\\', '/').TrimStart('/');
            lock (_gate)
            {
                TerrainAsset asset;
                if (_index.TryGetValue(normalized + "/" + relative, out asset)) return asset;

                // 兼容只给了文件名的旧数据：在同类里按 File 找第一个
                foreach (var candidate in Of(normalized))
                {
                    if (string.Equals(candidate.File, relative, StringComparison.Ordinal)) return candidate;
                }
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
            var trees = new Dictionary<string, AssetFolder>();

            foreach (var kind in DrawKind.All)
            {
                var folder = FolderFor(kind);
                var list = new List<TerrainAsset>();
                var root = new AssetFolder();
                root.Relative = string.Empty;
                root.Depth = -1;

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
                        // 地形保持扁平（历史行为，terrain_properties.json 也是扁平的）；
                        // 实体与物品支持子目录 —— 角色图本来就是按职业分文件夹的。
                        if (DrawKind.Normalize(kind) == DrawKind.Terrain)
                            ScanFlat(kind, folder, root, list, byFile);
                        else
                            ScanTree(kind, folder, string.Empty, 0, root, list, byFile);
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
                    var byFolder = string.Compare(a.Folder, b.Folder, StringComparison.CurrentCultureIgnoreCase);
                    if (byFolder != 0) return byFolder;
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                });

                SortTree(root);

                byKind[kind] = list;
                trees[kind] = root;
                foreach (var asset in list) index[kind + "/" + asset.Relative] = asset;
            }

            lock (_gate)
            {
                _byKind = byKind;
                _trees = trees;
                _index = index;
            }
        }

        /// <summary>扁平扫描（地形用）：只看分类根目录。</summary>
        private static void ScanFlat(string kind, string folder, AssetFolder root,
            List<TerrainAsset> list, Dictionary<string, TerrainAsset> byFile)
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
                asset.Relative = name;
                asset.Name = Path.GetFileNameWithoutExtension(name);
                asset.Path = file;
                asset.Url = BuildUrl(kind, name);
                root.Assets.Add(asset);
                list.Add(asset);
                byFile[name] = asset;
            }
        }

        /// <summary>递归扫描（实体 / 物品用）：文件夹变成树节点，图片挂到所属节点。</summary>
        private static void ScanTree(string kind, string abs, string rel, int depth,
            AssetFolder node, List<TerrainAsset> flat, Dictionary<string, TerrainAsset> byFile)
        {
            if (depth > MaxFolderDepth)
            {
                AppLog.Write("素材目录嵌套超过 " + MaxFolderDepth + " 层，已跳过：" + abs);
                return;
            }

            // 1) 子文件夹（先建节点，再递归）
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(abs);
            }
            catch (Exception ex)
            {
                AppLog.Write("读取子目录失败：" + abs, ex);
                dirs = new string[0];
            }
            Array.Sort(dirs, StringComparer.Ordinal);

            foreach (var dir in dirs)
            {
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.Hidden) != 0) continue;
                if (info.Name.StartsWith(".")) continue;

                var childRel = rel.Length == 0 ? info.Name : rel + "/" + info.Name;

                // 每个文件夹都默认给一份 locales.toml（已存在就不动）
                FolderLocale.Ensure(dir);

                var child = new AssetFolder();
                child.Name = info.Name;
                child.DisplayName = FolderLocale.DisplayName(dir, info.Name);
                child.Relative = childRel;
                child.Depth = depth;
                node.Folders.Add(child);

                ScanTree(kind, dir, childRel, depth + 1, child, flat, byFile);
            }

            // 2) 本层的图片
            string[] files;
            try
            {
                files = Directory.GetFiles(abs);
            }
            catch (Exception ex)
            {
                AppLog.Write("读取素材文件失败：" + abs, ex);
                files = new string[0];
            }
            Array.Sort(files, StringComparer.Ordinal);

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (!IsAllowedAsset(name)) continue;
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                var asset = new TerrainAsset();
                asset.Kind = kind;
                asset.File = name;
                asset.Folder = rel;
                asset.Relative = rel.Length == 0 ? name : rel + "/" + name;
                asset.Name = Path.GetFileNameWithoutExtension(name);
                asset.Path = file;
                asset.Url = BuildUrl(kind, asset.Relative);

                node.Assets.Add(asset);
                flat.Add(asset);
                byFile[asset.Relative] = asset;
                // 兼容旧属性文件：里面可能只写裸文件名
                if (!byFile.ContainsKey(name)) byFile[name] = asset;
            }
        }

        private static void SortTree(AssetFolder node)
        {
            node.Folders.Sort(delegate (AssetFolder a, AssetFolder b)
            {
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
            });
            node.Assets.Sort(delegate (TerrainAsset a, TerrainAsset b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            foreach (var folder in node.Folders) SortTree(folder);
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

        /// <summary>
        /// 按「分类/相对路径」解析出磁盘路径（防目录穿越）。
        /// 支持子目录：<c>entity/character/战士.png</c>。
        /// </summary>
        public static string ResolvePath(string kind, string file)
        {
            if (!DrawKind.IsValid(kind)) return null;
            if (string.IsNullOrEmpty(file)) return null;

            var relative = file.Replace('\\', '/').TrimStart('/');
            if (relative.Length == 0) return null;

            var parts = relative.Split('/');
            var safe = new List<string>();
            foreach (var part in parts)
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..") return null;                       // 目录穿越
                if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                safe.Add(part);
            }
            if (safe.Count == 0) return null;
            if (!IsAllowedAsset(safe[safe.Count - 1])) return null;

            var combined = FolderFor(kind);
            foreach (var part in safe) combined = Path.Combine(combined, part);

            try
            {
                var root = Path.GetFullPath(FolderFor(kind));
                if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    root += Path.DirectorySeparatorChar;

                var full = Path.GetFullPath(combined);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                return File.Exists(full) ? full : null;
            }
            catch
            {
                return null;
            }
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
                        "file", asset.Relative,
                        "folder", asset.Folder,
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
