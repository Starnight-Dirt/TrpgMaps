using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;      // DeflateStream：.NET 2.0 起就有，在 System.dll 里
using System.Text;

namespace TrpgMaps
{
    /// <summary>ZIP 里的一个条目（只读解包用，所以只留必要字段）。</summary>
    internal sealed class ZipEntry
    {
        public string Name;
        public int Method;              // 0 = 存储，8 = deflate
        public long CompressedSize;
        public long UncompressedSize;
        public long LocalHeaderOffset;
    }

    /// <summary>
    /// 极简 ZIP 读取器（**只解包，不打包**）。
    ///
    /// 为什么要自己写：
    ///  * 目标是 .NET Framework 3.5，而 `System.IO.Compression.ZipFile`/`ZipArchive`
    ///    是 4.5 才有的，3.5 里根本没有；
    ///  * `System.IO.Packaging.ZipPackage` 虽然 3.0 就有，但它要求包里带
    ///    `[Content_Types].xml`，拿它开一个普通 zip 会直接抛异常；
    ///  * 本工程本来就是"零第三方运行期 DLL"路线（见 README），再拖一个
    ///    SharpZipLib/DotNetZip 进来不合适。
    ///
    /// 够用的范围：存储(0) 与 deflate(8) 两种方法、非 zip64、UTF-8 文件名。
    /// 精确到"自己用 Compress-Archive 打出来的包能原样解开"即可。
    /// deflate 用 3.5 自带的 <see cref="DeflateStream"/> —— 注意它是**裸 deflate**，
    /// 不带 zlib 那两字节头，正好就是 zip 需要的格式。
    /// </summary>
    internal static class MiniZip
    {
        private const uint SigLocal = 0x04034B50;
        private const uint SigCentral = 0x02014B50;
        private const uint SigEnd = 0x06054B50;

        /// <summary>读中央目录，列出全部条目。</summary>
        public static List<ZipEntry> ReadEntries(byte[] data)
        {
            if (data == null || data.Length < 22) throw new InvalidDataException("不是有效的 ZIP：文件太短。");

            var end = FindEndOfCentralDirectory(data);
            if (end < 0) throw new InvalidDataException("不是有效的 ZIP：找不到中央目录结束记录。");

            long total = BitConverter.ToUInt16(data, end + 10);
            long size = BitConverter.ToUInt32(data, end + 12);
            long offset = BitConverter.ToUInt32(data, end + 16);

            if (total == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF)
                throw new InvalidDataException("不支持 zip64 格式的压缩包（本程序的包不会超过 4GB）。");

            var list = new List<ZipEntry>();

            var pos = (int)offset;
            for (long i = 0; i < total; i++)
            {
                if (pos + 46 > data.Length || BitConverter.ToUInt32(data, pos) != SigCentral)
                    throw new InvalidDataException("ZIP 中央目录损坏（第 " + i + " 条）。");

                var flags = BitConverter.ToUInt16(data, pos + 8);
                var method = BitConverter.ToUInt16(data, pos + 10);
                var compSize = BitConverter.ToUInt32(data, pos + 20);
                var rawSize = BitConverter.ToUInt32(data, pos + 24);
                var nameLen = BitConverter.ToUInt16(data, pos + 28);
                var extraLen = BitConverter.ToUInt16(data, pos + 30);
                var commentLen = BitConverter.ToUInt16(data, pos + 32);
                var localOffset = BitConverter.ToUInt32(data, pos + 42);

                var nameBytes = new byte[nameLen];
                Array.Copy(data, pos + 46, nameBytes, 0, nameLen);

                // bit 11 置位说明文件名是 UTF-8。自己打的包一定置位；
                // 没置位的先按 UTF-8 试（现代工具多数也这么写），解不出再退到 ANSI。
                var name = ((flags & 0x0800) != 0) ? Encoding.UTF8.GetString(nameBytes) : DecodeName(nameBytes);

                list.Add(new ZipEntry
                {
                    Name = name.Replace('\\', '/'),
                    Method = method,
                    CompressedSize = compSize,
                    UncompressedSize = rawSize,
                    LocalHeaderOffset = localOffset
                });

                pos += 46 + nameLen + extraLen + commentLen;
            }

            return list;
        }

        /// <summary>把入口名字节解成字符串：先 UTF-8，失败再退到系统 ANSI 代码页。</summary>
        private static string DecodeName(byte[] bytes)
        {
            try
            {
                var strict = new UTF8Encoding(false, true);   // 遇到非法字节就抛
                return strict.GetString(bytes);
            }
            catch
            {
                return Encoding.Default.GetString(bytes);
            }
        }

        private static int FindEndOfCentralDirectory(byte[] data)
        {
            // 中央目录结束记录后面最多跟 65535 字节的整包注释，从尾部往前扫。
            var start = data.Length - 22;
            var limit = Math.Max(0, data.Length - 22 - 65535);
            for (var i = start; i >= limit; i--)
            {
                if (BitConverter.ToUInt32(data, i) == SigEnd) return i;
            }
            return -1;
        }

        /// <summary>取出某个条目的原始字节。</summary>
        public static byte[] ReadEntry(byte[] data, ZipEntry entry)
        {
            var local = (int)entry.LocalHeaderOffset;
            if (local + 30 > data.Length || BitConverter.ToUInt32(data, local) != SigLocal)
                throw new InvalidDataException("ZIP 本地文件头损坏：" + entry.Name);

            var nameLen = BitConverter.ToUInt16(data, local + 26);
            var extraLen = BitConverter.ToUInt16(data, local + 28);
            var start = local + 30 + nameLen + extraLen;
            var length = (int)entry.CompressedSize;

            if (start + length > data.Length)
                throw new InvalidDataException("ZIP 数据被截断：" + entry.Name);

            if (entry.Method == 0)
            {
                var raw = new byte[length];
                Array.Copy(data, start, raw, 0, length);
                return raw;
            }

            if (entry.Method != 8)
                throw new InvalidDataException("不支持的压缩方法 " + entry.Method + "（" + entry.Name + "）。");

            using (var input = new MemoryStream(data, start, length, false))
            using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream(entry.UncompressedSize > 0 ? (int)entry.UncompressedSize : 0))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = inflate.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        /// <summary>
        /// 把整个包解到目标目录，返回解出来的相对路径列表。
        ///
        /// 目录条目（以 / 结尾的）只建目录。文件名一律过一遍消毒：
        /// 绝对路径、盘符、`..` 全拒掉 —— 这是 zip-slip 攻击的标准入口，
        /// 升级包又恰好是"从网上下下来的"，不能不查。
        /// </summary>
        public static List<string> ExtractTo(byte[] data, string targetDir, bool overwrite)
        {
            var entries = ReadEntries(data);
            var written = new List<string>();

            foreach (var entry in entries)
            {
                var relative = Sanitize(entry.Name);
                if (relative == null) continue;

                var full = Path.Combine(targetDir, relative.Replace('/', Path.DirectorySeparatorChar));

                if (entry.Name.EndsWith("/", StringComparison.Ordinal) ||
                    entry.Name.EndsWith("\\", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(full);
                    continue;
                }

                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                if (File.Exists(full) && !overwrite) continue;

                var bytes = ReadEntry(data, entry);
                using (var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                }
                written.Add(relative);
            }

            return written;
        }

        /// <summary>路径消毒：拒绝绝对路径 / 盘符 / 任何 `..` 段。合法时返回规范化后的相对路径。</summary>
        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var normalized = name.Replace('\\', '/').TrimStart('/');

            // "C:/foo" 这类盘符路径
            if (normalized.Length >= 2 && normalized[1] == ':') return null;

            var parts = normalized.Split('/');
            var kept = new List<string>();
            foreach (var part in parts)
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..") return null;
                kept.Add(part);
            }

            if (kept.Count == 0) return null;
            return string.Join("/", kept.ToArray());
        }
    }
}
