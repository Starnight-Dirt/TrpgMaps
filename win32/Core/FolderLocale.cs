using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TrpgMaps
{
    /// <summary>
    /// 素材文件夹的本地化显示名：每个文件夹里放一个 <c>locales.toml</c>，
    /// 形如
    /// <code>
    /// [zh-cn]
    /// foldername = "战士"
    /// [en-us]
    /// foldername = "Fighter"
    /// </code>
    ///
    /// 规则
    /// ----
    ///  * 段名是 **BCP 47 语言标签**（比较时不区分大小写，<c>zh-CN</c> 也认）。
    ///  * 键名固定为 <c>foldername</c>。
    ///  * 当前界面语言（<see cref="Loc.Language"/>）对应的段存在、且值非空 →
    ///    用这个值当显示名；否则用文件夹本名。
    ///
    /// 扫描时如果文件夹里没有 locales.toml，会**自动生成**一份（各语言都留空）。
    /// 写不进去（只读目录）只记日志，不影响素材显示。
    /// </summary>
    internal static class FolderLocale
    {
        public const string FileName = "locales.toml";
        private const string Key = "foldername";

        /// <summary>没有就生成一份模板。返回是否真的写过。</summary>
        public static bool Ensure(string folderPath)
        {
            try
            {
                if (!Directory.Exists(folderPath)) return false;
                var path = Path.Combine(folderPath, FileName);
                if (File.Exists(path)) return false;

                var sb = new StringBuilder();
                sb.Append("# TrpgMaps 素材文件夹显示名 / Folder display name\r\n");
                sb.Append("# 填好后，素材列表里会用这里的名字代替文件夹本名；留空 = 用文件夹本名。\r\n");
                sb.Append("# Leave a value empty to use the folder name itself.\r\n");
                sb.Append("# 段名是 BCP 47 语言标签，当前界面语言没有对应值时回退到文件夹名。\r\n\r\n");

                var languages = Loc.Languages;
                for (var i = 0; i < languages.Length; i++)
                {
                    sb.Append('[').Append(languages[i].Code).Append("]\r\n");
                    sb.Append(Key).Append(" = \"\"\r\n\r\n");
                }

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write("生成 locales.toml 失败：" + folderPath, ex);
                return false;
            }
        }

        /// <summary>
        /// 按当前界面语言取显示名。读不到 / 值为空 → 返回 <paramref name="fallbackName"/>。
        /// </summary>
        public static string DisplayName(string folderPath, string fallbackName)
        {
            try
            {
                var path = Path.Combine(folderPath, FileName);
                if (!File.Exists(path)) return fallbackName;

                var values = Read(path);
                string text;
                if (values.TryGetValue(Loc.Language, out text) && !string.IsNullOrEmpty(text)) return text;

                // 语言段缺失时退一步试一遍“主语言”（zh-cn ↔ zh）—— 有人会只写 [zh]
                return fallbackName;
            }
            catch (Exception ex)
            {
                AppLog.Write("读取 locales.toml 失败：" + folderPath, ex);
                return fallbackName;
            }
        }

        /// <summary>
        /// 极简 TOML 读取：只认 <c>[section]</c> 与 <c>key = "value"</c>。
        /// 足够表达这份文件，也免得为一个显示名引一整个 TOML 库。
        /// </summary>
        private static Dictionary<string, string> Read(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var section = string.Empty;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch
            {
                // 有些编辑器存成 ANSI；退回默认编码再试一次
                lines = File.ReadAllLines(path);
            }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                if (line[0] == '[')
                {
                    var end = line.IndexOf(']');
                    if (end > 1) section = line.Substring(1, end - 1).Trim();
                    continue;
                }

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line.Substring(0, eq).Trim();
                if (!string.Equals(key, Key, StringComparison.OrdinalIgnoreCase)) continue;

                var value = line.Substring(eq + 1).Trim();
                value = StripComment(value);
                value = Unquote(value);

                // 段名统一按 BCP 47 归一化后再存：[zh] / [zh-CN] / [zh-cn] 都落到 zh-cn，
                // [en-GB] 落到 en-us。没写段名的按当前语言算。
                var normalized = section.Length == 0 ? Loc.Language : Loc.Normalize(section);
                result[normalized] = value;
            }

            return result;
        }

        private static string StripComment(string value)
        {
            var quote = '\0';
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '#') return value.Substring(0, i).Trim();
            }
            return value;
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2)
            {
                var first = value[0];
                if ((first == '"' || first == '\'') && value[value.Length - 1] == first)
                {
                    var inner = value.Substring(1, value.Length - 2);
                    return inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
                }
            }
            return value;
        }
    }
}
