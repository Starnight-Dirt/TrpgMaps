using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TrpgMaps
{
    /// <summary>
    /// 极简 JSON 读写。.NET Framework 4.0 没有 System.Text.Json，
    /// 而本项目的 JSON 结构很简单，自己实现可以完全避免第三方依赖。
    ///
    /// 解析结果：object → Dictionary&lt;string,object&gt;，array → List&lt;object&gt;，
    /// 其余为 string / double / bool / null。
    /// </summary>
    internal static class MiniJson
    {
        // ---------------- 写 ----------------

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        /// <summary>写一个 JSON 对象（键值对按传入顺序）。</summary>
        public static string WriteObject(params object[] keyValuePairs)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            for (var i = 0; i + 1 < keyValuePairs.Length; i += 2)
            {
                if (i > 0) sb.Append(',');
                WriteString(sb, Convert.ToString(keyValuePairs[i], CultureInfo.InvariantCulture));
                sb.Append(':');
                WriteValue(sb, keyValuePairs[i + 1]);
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }

            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is bool) { sb.Append(((bool)value) ? "true" : "false"); return; }

            if (value is int) { sb.Append(((int)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is long) { sb.Append(((long)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is float) { sb.Append(((float)value).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is double) { sb.Append(((double)value).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is decimal) { sb.Append(((decimal)value).ToString(CultureInfo.InvariantCulture)); return; }

            var dict = value as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                var first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, entry.Value);
                }
                sb.Append('}');
                return;
            }

            var list = value as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                var first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, value.ToString());
        }

        private static void WriteString(StringBuilder sb, string text)
        {
            if (text == null) { sb.Append("null"); return; }

            sb.Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------- 读 ----------------

        /// <summary>解析 JSON；失败返回 null。</summary>
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                var index = 0;
                var value = ParseValue(text, ref index);
                SkipWhitespace(text, ref index);
                return value;
            }
            catch (Exception ex)
            {
                AppLog.Write("解析 JSON 失败：" + Shorten(text), ex);
                return null;
            }
        }

        /// <summary>解析成对象；不是对象则返回 null。</summary>
        public static Dictionary<string, object> ParseObject(string text)
        {
            return Parse(text) as Dictionary<string, object>;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) return null;

            var c = s[i];
            if (c == '{') return ParseObjectBody(s, ref i);
            if (c == '[') return ParseArrayBody(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObjectBody(string s, ref int i)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // '{'
            SkipWhitespace(s, ref i);

            if (i < s.Length && s[i] == '}') { i++; return result; }

            while (i < s.Length)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("对象键必须是字符串");
                var key = ParseString(s, ref i);

                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("缺少冒号");
                i++;

                var value = ParseValue(s, ref i);
                result[key] = value;

                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return result; }
                throw new FormatException("对象格式错误");
            }
            throw new FormatException("对象没有闭合");
        }

        private static List<object> ParseArrayBody(string s, ref int i)
        {
            var result = new List<object>();
            i++; // '['
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return result; }

            while (i < s.Length)
            {
                result.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return result; }
                throw new FormatException("数组格式错误");
            }
            throw new FormatException("数组没有闭合");
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // 开引号
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"') return sb.ToString();

                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) break;
                var esc = s[i++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 <= s.Length)
                        {
                            var code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(esc); break;
                }
            }
            throw new FormatException("字符串没有闭合");
        }

        private static double ParseNumber(string s, ref int i)
        {
            var start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' ||
                                    s[i] == 'e' || s[i] == 'E'))
            {
                i++;
            }
            if (i == start) throw new FormatException("不是合法数字");
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new FormatException("期望 " + literal);
            i += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static string Shorten(string text)
        {
            if (text == null) return string.Empty;
            return text.Length <= 200 ? text : text.Substring(0, 200) + "...";
        }

        // ---------------- 取值辅助 ----------------

        public static string GetString(Dictionary<string, object> obj, string key)
        {
            if (obj == null) return null;
            object value;
            if (!obj.TryGetValue(key, out value) || value == null) return null;
            return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static int GetInt(Dictionary<string, object> obj, string key, int fallback)
        {
            if (obj == null) return fallback;
            object value;
            if (!obj.TryGetValue(key, out value) || value == null) return fallback;
            if (value is double) return (int)(double)value;
            int parsed;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        public static bool GetBool(Dictionary<string, object> obj, string key, bool fallback)
        {
            if (obj == null) return fallback;
            object value;
            if (!obj.TryGetValue(key, out value) || value == null) return fallback;
            if (value is bool) return (bool)value;
            bool parsed;
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }
    }
}
