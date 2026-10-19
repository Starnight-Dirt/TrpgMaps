using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TrpgMaps
{
    /// <summary>一句歌词。</summary>
    internal sealed class LrcLine
    {
        public double Time;     // 秒
        public string Text;
    }

    /// <summary>
    /// 同名的 `.lrc` 歌词（纯本地文件，不联网）。
    ///
    /// 支持常见写法：`[mm:ss.xx]`、`[mm:ss.xxx]`、`[mm:ss:xx]`，
    /// 也支持一行挂多个时间标签（`[00:12.00][01:20.00]词` —— 副歌重复常用这种）。
    /// `[ti:]`/`[ar:]`/`[offset:]` 这类元数据标签会被忽略。
    ///
    /// ⚠️ 编码：中文 lrc 在国内大多**不是** UTF-8。没有 BOM 时先按 UTF-8 严格解一遍，
    /// 解不动再用系统 ANSI（中文系统就是 GBK）—— 直接 `Encoding.Default` 会把
    /// UTF-8 歌词读成乱码，而直接 UTF-8 又会把 GBK 歌词读成乱码，只能这么试。
    /// </summary>
    internal sealed class Lrc
    {
        public string Path = string.Empty;
        public readonly List<LrcLine> Lines = new List<LrcLine>();

        /// <summary>一句歌词都没有（文件不存在或全是元数据）。</summary>
        public bool IsEmpty
        {
            get { return Lines.Count == 0; }
        }

        private static readonly Regex TimeTag =
            new Regex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

        /// <summary>找同名 .lrc 并解析；没有就返回 null。</summary>
        public static Lrc Load(string audioPath)
        {
            try
            {
                var file = AudioLibrary.FindLyrics(audioPath);
                if (file == null) return null;

                var lrc = new Lrc();
                lrc.Path = file;
                lrc.Parse(ReadText(file));
                return lrc.IsEmpty ? null : lrc;
            }
            catch (Exception ex)
            {
                AppLog.Write("读歌词失败：" + audioPath, ex);
                return null;
            }
        }

        private void Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var seen = new Dictionary<string, bool>();
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                var matches = TimeTag.Matches(line);
                if (matches.Count == 0) continue;      // [ti:] 之类，或者纯文字，都不要

                var lyric = TimeTag.Replace(line, string.Empty).Trim();
                if (lyric.Length == 0) continue;       // `[00:12.00]` 空行 = 间奏

                foreach (Match m in matches)
                {
                    var minutes = int.Parse(m.Groups[1].Value);
                    var seconds = int.Parse(m.Groups[2].Value);
                    var fraction = 0;

                    if (m.Groups[3].Success)
                    {
                        var digits = m.Groups[3].Value;
                        fraction = int.Parse(digits);
                        // 两位 = 百分秒，三位 = 毫秒
                        if (digits.Length == 1) fraction *= 100;
                        else if (digits.Length == 2) fraction *= 10;
                    }

                    var time = minutes * 60.0 + seconds + fraction / 1000.0;

                    // 同一时间点重复的标签只留一条
                    var key = time.ToString("F3") + "|" + lyric;
                    if (seen.ContainsKey(key)) continue;
                    seen[key] = true;

                    Lines.Add(new LrcLine { Time = time, Text = lyric });
                }
            }

            Lines.Sort(delegate (LrcLine a, LrcLine b) { return a.Time.CompareTo(b.Time); });
        }

        /// <summary>当前该显示第几句（-1 = 还没到第一句）。</summary>
        public int IndexAt(double seconds)
        {
            if (Lines.Count == 0) return -1;

            var low = 0;
            var high = Lines.Count - 1;
            var found = -1;

            while (low <= high)
            {
                var mid = (low + high) / 2;
                if (Lines[mid].Time <= seconds)
                {
                    found = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }
            return found;
        }

        private static string ReadText(string file)
        {
            var bytes = File.ReadAllBytes(file);

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Default.GetString(bytes);
            }
        }
    }
}
