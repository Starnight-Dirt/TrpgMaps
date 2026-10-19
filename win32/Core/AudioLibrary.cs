using System;
using System.Collections.Generic;
using System.IO;

namespace TrpgMaps
{
    /// <summary>一首曲子。</summary>
    internal sealed class AudioTrack
    {
        public string Path;
        public string Title;        // 文件名（不含扩展名），界面上的主标题
        public string Folder;       // 相对 audio\ 的子目录，根目录为空串
        public long SizeBytes;

        /// <summary>能不能解码。null = 还没探测过。</summary>
        public bool? Playable;

        /// <summary>列表里显示的那一行。</summary>
        public string Display
        {
            get
            {
                var name = string.IsNullOrEmpty(Folder) ? Title : (Folder + " / " + Title);
                if (Playable.HasValue && !Playable.Value)
                    name += "   " + Loc.T("core.unsupported");
                return name;
            }
        }

        public string Extension
        {
            get { return System.IO.Path.GetExtension(Path).ToLowerInvariant(); }
        }

        /// <summary>
        /// 列表里直接用这个对象当条目（ModernList 默认就是画 ToString()）。
        /// 探测结果回来后会变（多一句"不支持此格式"），所以那时要让列表重画一次。
        /// </summary>
        public override string ToString()
        {
            return Display;
        }
    }

    /// <summary>
    /// `audio\` 里的一个子文件夹。
    ///
    /// 与素材树（<see cref="AssetFolder"/>）同一套：**可以多层嵌套**，
    /// 界面上是"文件夹行 30px（▶/▼ + 名字 + 曲目数）/ 曲目行 26px"的折叠菜单。
    /// <see cref="Relative"/> 用 `/` 分隔，根节点为空串。
    /// </summary>
    internal sealed class AudioFolder
    {
        /// <summary>磁盘上的文件夹名。</summary>
        public string Name = string.Empty;
        /// <summary>相对 `audio\` 的路径（用 `/` 分隔）；根节点为空串。</summary>
        public string Relative = string.Empty;
        /// <summary>嵌套深度：根下的文件夹 = 0，根节点本身 = -1。</summary>
        public int Depth;

        public readonly List<AudioFolder> Folders = new List<AudioFolder>();
        public readonly List<AudioTrack> Tracks = new List<AudioTrack>();

        /// <summary>这个文件夹下（含所有子文件夹）一共几首。</summary>
        public int TotalCount
        {
            get
            {
                var total = Tracks.Count;
                foreach (var folder in Folders) total += folder.TotalCount;
                return total;
            }
        }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// `audio\` 目录扫描（与 `terrain\`、`maps\` 同级）。
    ///
    /// 目录结构随 DM 喜欢：直接扔 mp3 进 `audio\`，或者按用途分子目录
    /// （`audio\战斗\`、`audio\酒馆\`…）都行，而且是**多层**的 ——
    /// 界面上按折叠菜单显示（与「绘图」里的素材树同一个样子）。
    /// 最多往下 8 层，免得有人把整个音乐库塞进来扫半天。
    /// </summary>
    internal static class AudioLibrary
    {
        /// <summary>认这些扩展名。能不能真播由原生层决定（这个列表只是"值得一试"）。</summary>
        private static readonly string[] Known =
        {
            ".mp3", ".wav", ".flac", ".ogg", ".oga", ".m4a", ".aac", ".wma"
        };

        private const int MaxDepth = 8;

        public static string Root
        {
            get { return Path.Combine(AppEnv.Root, "audio"); }
        }

        public static bool IsSupportedExtension(string path)
        {
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.ToLowerInvariant();
            foreach (var item in Known)
            {
                if (item == ext) return true;
            }
            return false;
        }

        /// <summary>扩展名清单（给界面提示用，例如"支持 mp3 / wav / flac / ogg"）。</summary>
        public static string ExtensionList
        {
            get { return "mp3 / wav / flac / ogg"; }
        }

        /// <summary>
        /// 把 `audio\` 扫成一棵目录树（界面用它画折叠菜单）。
        /// 根节点的 <see cref="AudioFolder.Depth"/> = -1，根下的文件夹从 0 开始。
        /// </summary>
        public static AudioFolder ScanTree()
        {
            var root = new AudioFolder();
            root.Depth = -1;

            var dir = Root;
            if (!Directory.Exists(dir)) return root;

            try
            {
                Collect(root, dir, string.Empty, 1);
            }
            catch (Exception ex)
            {
                AppLog.Write("扫描 audio 目录失败：" + dir, ex);
            }

            SortTree(root);
            return root;
        }

        /// <summary>把树按"子目录 → 文件名"打平成一条播放列表（上一曲 / 下一曲走它）。</summary>
        public static List<AudioTrack> Flatten(AudioFolder root)
        {
            var list = new List<AudioTrack>();
            if (root != null) CollectTracks(root, list);
            list.Sort(CompareTracks);
            return list;
        }

        /// <summary>把 `audio\` 扫一遍，按"子目录 → 文件名"排好序返回。</summary>
        public static List<AudioTrack> Scan()
        {
            return Flatten(ScanTree());
        }

        private static void CollectTracks(AudioFolder node, List<AudioTrack> list)
        {
            list.AddRange(node.Tracks);
            foreach (var folder in node.Folders) CollectTracks(folder, list);
        }

        private static void Collect(AudioFolder node, string dir, string relative, int depth)
        {
            string[] files;
            string[] dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = depth <= MaxDepth ? Directory.GetDirectories(dir) : new string[0];
            }
            catch
            {
                return;         // 没权限的子目录直接跳过，不要连累整个列表
            }

            foreach (var file in files)
            {
                if (!IsSupportedExtension(file)) continue;
                var info = new FileInfo(file);
                node.Tracks.Add(new AudioTrack
                {
                    Path = file,
                    Title = Path.GetFileNameWithoutExtension(file),
                    Folder = relative,
                    SizeBytes = info.Exists ? info.Length : 0
                });
            }

            foreach (var sub in dirs)
            {
                var name = Path.GetFileName(sub);
                if (string.IsNullOrEmpty(name)) continue;
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;

                var child = new AudioFolder();
                child.Name = name;
                child.Relative = relative.Length == 0 ? name : (relative + "/" + name);
                child.Depth = node.Depth + 1;
                node.Folders.Add(child);

                Collect(child, sub, child.Relative, depth + 1);
            }
        }

        /// <summary>文件夹按名字的自然序（"第2首" 在 "第10首" 前面）；曲目同理。</summary>
        private static void SortTree(AudioFolder node)
        {
            node.Folders.Sort(delegate(AudioFolder a, AudioFolder b)
            {
                return NaturalCompare(a.Name, b.Name);
            });
            node.Tracks.Sort(delegate(AudioTrack a, AudioTrack b)
            {
                return NaturalCompare(a.Title, b.Title);
            });
            foreach (var folder in node.Folders) SortTree(folder);
        }

        /// <summary>
        /// 排序：先子目录，再文件名；文件名里的数字按**数值**比 ——
        /// 否则 "第10首" 会排在 "第2首" 前面（字符串比较的通病）。
        /// </summary>
        private static int CompareTracks(AudioTrack a, AudioTrack b)
        {
            var byFolder = string.Compare(a.Folder, b.Folder, StringComparison.Ordinal);
            if (byFolder != 0) return byFolder;
            return NaturalCompare(a.Title, b.Title);
        }

        private static int NaturalCompare(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                var ca = a[i];
                var cb = b[j];

                if (char.IsDigit(ca) && char.IsDigit(cb))
                {
                    var si = i;
                    var sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;

                    long na = 0, nb = 0;
                    long.TryParse(a.Substring(si, i - si), out na);
                    long.TryParse(b.Substring(sj, j - sj), out nb);
                    if (na != nb) return na < nb ? -1 : 1;
                    continue;
                }

                var cmp = ca.CompareTo(cb);
                if (cmp != 0) return cmp;
                i++;
                j++;
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }

        /// <summary>
        /// 逐个问原生层"这个文件能不能解"（要开一次文件，几十毫秒一首）。
        /// 交给后台线程做，界面先出列表、探测结果回来了再刷新那一列。
        /// </summary>
        public static void ProbeAll(IList<AudioTrack> tracks, Action<AudioTrack> onProbed)
        {
            foreach (var track in tracks)
            {
                if (!AudioEngine.Available)
                {
                    track.Playable = false;
                    if (onProbed != null) onProbed(track);
                    continue;
                }

                bool ok;
                try { ok = AudioEngine.CanDecode(track.Path); }
                catch { ok = false; }

                track.Playable = ok;
                if (onProbed != null) onProbed(track);
            }
        }

        /// <summary>取同名歌词文件（`xxx.lrc`），没有就返回 null。</summary>
        public static string FindLyrics(string audioPath)
        {
            try
            {
                var wanted = Path.Combine(
                    Path.GetDirectoryName(audioPath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(audioPath) + ".lrc");

                if (File.Exists(wanted)) return wanted;

                // 大小写不敏感地找一遍（U 盘/别人拷来的文件常见 .LRC）
                var dir = Path.GetDirectoryName(audioPath);
                var stem = Path.GetFileNameWithoutExtension(audioPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

                foreach (var file in Directory.GetFiles(dir, "*.lrc"))
                {
                    if (string.Equals(Path.GetFileNameWithoutExtension(file), stem,
                                      StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }
            catch
            {
                // 歌词是锦上添花，出错就当没有
            }
            return null;
        }
    }
}
