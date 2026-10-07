using System;
using System.Collections.Generic;
using System.Text;

namespace TrpgMaps
{
    /// <summary>
    /// 纯托管 QR 码生成器，替代原 Python 项目的 qrcode 依赖。
    /// 支持版本 1-10、字节(UTF-8)模式、纠错等级 M，输出模块矩阵（true = 深色）。
    /// </summary>
    internal static class QrCodeGenerator
    {
        /// <summary>单个版本的纠错参数。</summary>
        private sealed class VersionSpec
        {
            public readonly int TotalCodewords;
            public readonly int DataCodewords;
            public readonly int EccPerBlock;
            public readonly int Group1Blocks;
            public readonly int Group2Blocks;

            public VersionSpec(int total, int data, int ecc, int g1, int g2)
            {
                TotalCodewords = total;
                DataCodewords = data;
                EccPerBlock = ecc;
                Group1Blocks = g1;
                Group2Blocks = g2;
            }
        }

        // 每个版本 4 组参数，顺序固定为 L, M, Q, H（与纠错等级索引一致）
        private static readonly VersionSpec[][] Specs =
        {
            /* v1  */ new VersionSpec[] { new VersionSpec(26, 19, 7, 1, 0),   new VersionSpec(26, 16, 10, 1, 0),  new VersionSpec(26, 13, 13, 1, 0),  new VersionSpec(26, 9, 17, 1, 0) },
            /* v2  */ new VersionSpec[] { new VersionSpec(44, 34, 10, 1, 0),  new VersionSpec(44, 28, 16, 1, 0),  new VersionSpec(44, 22, 22, 1, 0),  new VersionSpec(44, 16, 28, 1, 0) },
            /* v3  */ new VersionSpec[] { new VersionSpec(70, 55, 15, 1, 0),  new VersionSpec(70, 44, 26, 1, 0),  new VersionSpec(70, 34, 18, 2, 0),  new VersionSpec(70, 26, 22, 2, 0) },
            /* v4  */ new VersionSpec[] { new VersionSpec(100, 80, 20, 1, 0), new VersionSpec(100, 64, 18, 2, 0), new VersionSpec(100, 48, 26, 2, 0), new VersionSpec(100, 36, 16, 4, 0) },
            /* v5  */ new VersionSpec[] { new VersionSpec(134, 108, 26, 1, 0), new VersionSpec(134, 86, 24, 2, 0), new VersionSpec(134, 62, 18, 2, 2), new VersionSpec(134, 46, 22, 2, 2) },
            /* v6  */ new VersionSpec[] { new VersionSpec(172, 136, 18, 2, 0), new VersionSpec(172, 108, 16, 4, 0), new VersionSpec(172, 76, 24, 4, 0), new VersionSpec(172, 60, 28, 4, 0) },
            /* v7  */ new VersionSpec[] { new VersionSpec(196, 156, 20, 2, 0), new VersionSpec(196, 124, 18, 4, 0), new VersionSpec(196, 88, 18, 2, 4), new VersionSpec(196, 66, 26, 4, 1) },
            /* v8  */ new VersionSpec[] { new VersionSpec(242, 194, 24, 2, 0), new VersionSpec(242, 154, 22, 4, 0), new VersionSpec(242, 110, 22, 2, 4), new VersionSpec(242, 86, 26, 4, 2) },
            /* v9  */ new VersionSpec[] { new VersionSpec(292, 232, 30, 2, 0), new VersionSpec(292, 182, 14, 3, 1), new VersionSpec(292, 132, 20, 4, 4), new VersionSpec(292, 100, 18, 4, 4) },
            /* v10 */ new VersionSpec[] { new VersionSpec(346, 274, 18, 2, 2), new VersionSpec(346, 216, 30, 4, 1), new VersionSpec(346, 154, 28, 6, 2), new VersionSpec(346, 122, 24, 6, 2) },
        };

        /// <summary>版本 -> 校正图形中心坐标。</summary>
        private static readonly int[][] AlignmentPositions =
        {
            new int[0],          // v1 无校正图形
            new int[] { 6, 18 },
            new int[] { 6, 22 },
            new int[] { 6, 26 },
            new int[] { 6, 30 },
            new int[] { 6, 34 },
            new int[] { 6, 22, 38 },
            new int[] { 6, 24, 42 },
            new int[] { 6, 26, 46 },
            new int[] { 6, 28, 50 },
        };

        private const int LevelL = 0;
        private const int LevelM = 1;
        private const int LevelQ = 2;
        private const int LevelH = 3;

        /// <summary>格式信息的 15 位编码表（索引 = 纠错等级 * 8 + 掩码）。</summary>
        private static readonly ushort[] FormatInfoTable =
        {
            0x77C4, 0x72F3, 0x7DAA, 0x789D, 0x662F, 0x6318, 0x6C41, 0x6976, // L
            0x5412, 0x5125, 0x5E7C, 0x5B4B, 0x45F9, 0x40CE, 0x4F97, 0x4AA0, // M
            0x355F, 0x3068, 0x3F31, 0x3A06, 0x24B4, 0x2183, 0x2EDA, 0x2BED, // Q
            0x1689, 0x13BE, 0x1CE7, 0x19D0, 0x0762, 0x0255, 0x0D0C, 0x083B, // H
        };

        /// <summary>生成二维码模块矩阵。true 表示深色模块。</summary>
        public static bool[,] Encode(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            var payload = Encoding.UTF8.GetBytes(text);

            const int level = LevelM;
            var version = -1;
            for (var v = 1; v <= Specs.Length; v++)
            {
                var spec = Specs[v - 1][level];
                var countBits = v <= 9 ? 8 : 16;
                if (4 + countBits + payload.Length * 8 <= spec.DataCodewords * 8)
                {
                    version = v;
                    break;
                }
            }
            if (version < 0)
                throw new ArgumentException("内容过长，无法生成二维码（" + payload.Length + " 字节）");

            var versionSpec = Specs[version - 1][level];
            var codewords = BuildCodewords(payload, version, versionSpec);
            var finalCodewords = AddErrorCorrection(codewords, versionSpec);

            var size = 17 + 4 * version;
            var matrix = new bool[size, size];
            var reserved = new bool[size, size];
            DrawFunctionPatterns(matrix, reserved, version);
            PlaceData(matrix, reserved, finalCodewords);

            // 评估 8 种掩码，选罚分最低的
            var bestMask = 0;
            var bestScore = int.MaxValue;
            for (var mask = 0; mask < 8; mask++)
            {
                var score = EvaluatePenalty(matrix, reserved, mask, level);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestMask = mask;
                }
            }

            ApplyMask(matrix, reserved, bestMask);
            DrawFormatInfo(matrix, level, bestMask);
            return matrix;
        }

        private static int EvaluatePenalty(bool[,] source, bool[,] sourceReserved, int mask, int level)
        {
            var copy = (bool[,])source.Clone();
            var reservedCopy = (bool[,])sourceReserved.Clone();
            ApplyMask(copy, reservedCopy, mask);
            DrawFormatInfo(copy, level, mask);
            return ComputePenalty(copy);
        }

        /// <summary>渲染为 PNG 字节流（8 位灰度）。</summary>
        public static byte[] RenderPng(bool[,] matrix, int scale, int quietZone)
        {
            var modules = matrix.GetLength(0);
            var size = (modules + quietZone * 2) * scale;

            var stride = size + 1;
            var raw = new byte[stride * size];
            for (var i = 0; i < raw.Length; i++) raw[i] = 255;   // 默认全白
            for (var y = 0; y < size; y++) raw[y * stride] = 0;  // filter = None

            for (var my = 0; my < modules; my++)
            {
                for (var mx = 0; mx < modules; mx++)
                {
                    if (!matrix[my, mx]) continue;
                    for (var dy = 0; dy < scale; dy++)
                    {
                        var rowStart = ((my + quietZone) * scale + dy) * stride + 1 + (mx + quietZone) * scale;
                        for (var dx = 0; dx < scale; dx++) raw[rowStart + dx] = 0;
                    }
                }
            }

            return PngWriter.EncodeGrayscale(raw, size, size);
        }

        /// <summary>生成 data:image/png;base64,... 形式的二维码。</summary>
        public static string RenderDataUrl(string text, int scale, int quietZone)
        {
            var matrix = Encode(text);
            var png = RenderPng(matrix, scale, quietZone);
            return "data:image/png;base64," + Convert.ToBase64String(png);
        }

        // ---------------- 数据码字 ----------------

        private static byte[] BuildCodewords(byte[] payload, int version, VersionSpec spec)
        {
            var bits = new BitBuffer();
            bits.Append(0x4, 4);                                     // 字节模式
            bits.Append(payload.Length, version <= 9 ? 8 : 16);      // 字符数
            foreach (var b in payload) bits.Append(b, 8);

            var capacityBits = spec.DataCodewords * 8;
            var terminator = Math.Min(4, capacityBits - bits.Length);
            if (terminator > 0) bits.Append(0, terminator);
            if (bits.Length % 8 != 0) bits.Append(0, 8 - bits.Length % 8);

            var result = new List<byte>(spec.DataCodewords);
            result.AddRange(bits.ToBytes());

            var pad = new byte[] { 0xEC, 0x11 };
            var i = 0;
            while (result.Count < spec.DataCodewords) result.Add(pad[i++ % 2]);

            return result.ToArray();
        }

        // ---------------- 纠错 ----------------

        private static byte[] AddErrorCorrection(byte[] data, VersionSpec spec)
        {
            var blocks = SplitIntoBlocks(data, spec);

            var eccBlocks = new List<byte[]>(blocks.Count);
            foreach (var block in blocks) eccBlocks.Add(ComputeEcc(block, spec.EccPerBlock));

            var result = new List<byte>(spec.TotalCodewords);

            var maxData = 0;
            foreach (var block in blocks) if (block.Length > maxData) maxData = block.Length;

            for (var i = 0; i < maxData; i++)
            {
                foreach (var block in blocks)
                {
                    if (i < block.Length) result.Add(block[i]);
                }
            }
            for (var i = 0; i < spec.EccPerBlock; i++)
            {
                foreach (var ecc in eccBlocks) result.Add(ecc[i]);
            }

            return result.ToArray();
        }

        private static List<byte[]> SplitIntoBlocks(byte[] data, VersionSpec spec)
        {
            var blockCount = spec.Group1Blocks + spec.Group2Blocks;
            var group1Length = spec.DataCodewords / blockCount;
            var group2Length = group1Length + 1;
            var group2Count = spec.DataCodewords % blockCount;

            var blocks = new List<byte[]>(blockCount);
            var offset = 0;
            for (var i = 0; i < blockCount; i++)
            {
                var isLong = i >= blockCount - group2Count;
                var length = isLong ? group2Length : group1Length;
                var block = new byte[length];
                Array.Copy(data, offset, block, 0, length);
                offset += length;
                blocks.Add(block);
            }
            return blocks;
        }

        private static byte[] ComputeEcc(byte[] data, int eccCount)
        {
            var generator = BuildGeneratorPolynomial(eccCount);
            var remainder = new byte[eccCount];

            foreach (var b in data)
            {
                var factor = (byte)(b ^ remainder[0]);
                Array.Copy(remainder, 1, remainder, 0, eccCount - 1);
                remainder[eccCount - 1] = 0;
                if (factor == 0) continue;

                for (var i = 0; i < eccCount; i++) remainder[i] ^= GfMul(generator[i], factor);
            }
            return remainder;
        }

        private static byte[] BuildGeneratorPolynomial(int degree)
        {
            var poly = new byte[] { 1 };
            for (var i = 0; i < degree; i++)
            {
                var next = new byte[poly.Length + 1];
                var root = GfPow((byte)i);
                for (var j = 0; j < poly.Length; j++)
                {
                    next[j] ^= poly[j];
                    next[j + 1] ^= GfMul(poly[j], root);
                }
                poly = next;
            }

            var result = new byte[poly.Length - 1];
            Array.Copy(poly, 1, result, 0, result.Length);
            return result;
        }

        // GF(256)，本原多项式 0x11D
        private static readonly byte[] GfExp = new byte[512];
        private static readonly byte[] GfLog = new byte[256];

        static QrCodeGenerator()
        {
            var x = 1;
            for (var i = 0; i < 255; i++)
            {
                GfExp[i] = (byte)x;
                GfLog[x] = (byte)i;
                x <<= 1;
                if (x >= 256) x ^= 0x11D;
            }
            for (var i = 255; i < 512; i++) GfExp[i] = GfExp[i - 255];
        }

        private static byte GfMul(byte a, byte b)
        {
            if (a == 0 || b == 0) return 0;
            return GfExp[GfLog[a] + GfLog[b]];
        }

        private static byte GfPow(byte exponent)
        {
            return GfExp[exponent];
        }

        // ---------------- 功能图形 ----------------

        private static void DrawFunctionPatterns(bool[,] m, bool[,] reserved, int version)
        {
            var size = m.GetLength(0);

            DrawFinder(m, reserved, 0, 0);
            DrawFinder(m, reserved, size - 7, 0);
            DrawFinder(m, reserved, 0, size - 7);

            var positions = AlignmentPositions[version - 1];
            foreach (var cy in positions)
            {
                foreach (var cx in positions)
                {
                    if ((cx == 6 && cy == 6) ||
                        (cx == 6 && cy == size - 7) ||
                        (cx == size - 7 && cy == 6)) continue;
                    DrawAlignment(m, reserved, cx, cy);
                }
            }

            for (var i = 8; i < size - 8; i++)
            {
                Set(m, reserved, i, 6, i % 2 == 0);
                Set(m, reserved, 6, i, i % 2 == 0);
            }

            Set(m, reserved, 8, size - 8, true);

            for (var i = 0; i < 9; i++)
            {
                if (i == 6) continue;
                Reserve(reserved, i, 8);
                Reserve(reserved, 8, i);
            }
            for (var i = 0; i < 8; i++)
            {
                Reserve(reserved, size - 1 - i, 8);
                Reserve(reserved, 8, size - 1 - i);
            }

            if (version >= 7)
            {
                for (var y = 0; y < 6; y++)
                {
                    for (var x = size - 11; x < size - 8; x++)
                    {
                        Reserve(reserved, x, y);
                        Reserve(reserved, y, x);
                    }
                }
            }
        }

        private static void DrawFinder(bool[,] m, bool[,] reserved, int left, int top)
        {
            var size = m.GetLength(0);
            for (var dy = -1; dy <= 7; dy++)
            {
                for (var dx = -1; dx <= 7; dx++)
                {
                    var x = left + dx;
                    var y = top + dy;
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;

                    var dark = dx >= 0 && dx <= 6 && dy >= 0 && dy <= 6 &&
                               (dx == 0 || dx == 6 || dy == 0 || dy == 6 ||
                                (dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4));
                    Set(m, reserved, x, y, dark);
                }
            }
        }

        private static void DrawAlignment(bool[,] m, bool[,] reserved, int cx, int cy)
        {
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    var dark = Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1;
                    Set(m, reserved, cx + dx, cy + dy, dark);
                }
            }
        }

        private static void Set(bool[,] m, bool[,] reserved, int x, int y, bool dark)
        {
            var size = m.GetLength(0);
            if (x < 0 || y < 0 || x >= size || y >= size) return;
            m[y, x] = dark;
            reserved[y, x] = true;
        }

        private static void Reserve(bool[,] reserved, int x, int y)
        {
            var size = reserved.GetLength(0);
            if (x < 0 || y < 0 || x >= size || y >= size) return;
            reserved[y, x] = true;
        }

        private static void PlaceData(bool[,] m, bool[,] reserved, byte[] codewords)
        {
            var size = m.GetLength(0);
            var bitIndex = 0;
            var totalBits = codewords.Length * 8;

            for (var right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;

                for (var vert = 0; vert < size; vert++)
                {
                    for (var j = 0; j < 2; j++)
                    {
                        var x = right - j;
                        var upward = ((right + 1) & 2) == 0;
                        var y = upward ? size - 1 - vert : vert;

                        if (reserved[y, x]) continue;

                        var dark = false;
                        if (bitIndex < totalBits)
                        {
                            var codeword = codewords[bitIndex >> 3];
                            dark = ((codeword >> (7 - (bitIndex & 7))) & 1) != 0;
                        }
                        bitIndex++;
                        m[y, x] = dark;
                    }
                }
            }
        }

        private static void ApplyMask(bool[,] m, bool[,] reserved, int mask)
        {
            var size = m.GetLength(0);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (reserved[y, x]) continue;
                    if (MaskCondition(mask, x, y)) m[y, x] = !m[y, x];
                }
            }
        }

        private static bool MaskCondition(int mask, int x, int y)
        {
            switch (mask)
            {
                case 0: return (x + y) % 2 == 0;
                case 1: return y % 2 == 0;
                case 2: return x % 3 == 0;
                case 3: return (x + y) % 3 == 0;
                case 4: return (y / 2 + x / 3) % 2 == 0;
                case 5: return x * y % 2 + x * y % 3 == 0;
                case 6: return (x * y % 2 + x * y % 3) % 2 == 0;
                case 7: return ((x + y) % 2 + x * y % 3) % 2 == 0;
                default: return false;
            }
        }

        private static void DrawFormatInfo(bool[,] m, int level, int mask)
        {
            var size = m.GetLength(0);
            var bits = FormatInfoTable[level * 8 + mask];

            for (var i = 0; i < 15; i++)
            {
                var bit = ((bits >> (14 - i)) & 1) != 0;

                if (i < 6) m[8, i] = bit;
                else if (i == 6) m[8, 7] = bit;
                else if (i == 7) m[8, 8] = bit;
                else if (i == 8) m[7, 8] = bit;
                else m[14 - i, 8] = bit;

                if (i < 8) m[8, size - 1 - i] = bit;
                else m[size - 15 + i, 8] = bit;
            }

            m[size - 8, 8] = true;
        }

        // ---------------- 掩码罚分 ----------------

        private static int ComputePenalty(bool[,] m)
        {
            var size = m.GetLength(0);
            var score = 0;

            // 规则 1：同色连续 >= 5
            for (var y = 0; y < size; y++)
            {
                var run = 1;
                for (var x = 1; x < size; x++)
                {
                    if (m[y, x] == m[y, x - 1]) run++;
                    else { if (run >= 5) score += run - 2; run = 1; }
                }
                if (run >= 5) score += run - 2;
            }
            for (var x = 0; x < size; x++)
            {
                var run = 1;
                for (var y = 1; y < size; y++)
                {
                    if (m[y, x] == m[y - 1, x]) run++;
                    else { if (run >= 5) score += run - 2; run = 1; }
                }
                if (run >= 5) score += run - 2;
            }

            // 规则 2：2x2 同色块
            for (var y = 0; y < size - 1; y++)
            {
                for (var x = 0; x < size - 1; x++)
                {
                    var v = m[y, x];
                    if (v == m[y, x + 1] && v == m[y + 1, x] && v == m[y + 1, x + 1]) score += 3;
                }
            }

            // 规则 3：类定位图形 1:1:3:1:1
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size - 10; x++)
                {
                    if (IsFinderLike(m, x, y, 1, 0)) score += 40;
                }
            }
            for (var x = 0; x < size; x++)
            {
                for (var y = 0; y < size - 10; y++)
                {
                    if (IsFinderLike(m, x, y, 0, 1)) score += 40;
                }
            }

            // 规则 4：深色比例偏离 50%
            var dark = 0;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (m[y, x]) dark++;
                }
            }
            var percent = dark * 100.0 / (size * size);
            score += (int)(Math.Abs(percent - 50) / 5) * 10;

            return score;
        }

        private static bool IsFinderLike(bool[,] m, int x, int y, int dx, int dy)
        {
            var pattern = new int[] { 1, 0, 1, 1, 1, 0, 1 };
            var size = m.GetLength(0);

            for (var i = 0; i < 7; i++)
            {
                var px = x + dx * i;
                var py = y + dy * i;
                if (px < 0 || py < 0 || px >= size || py >= size) return false;
                if (m[py, px] != (pattern[i] == 1)) return false;
            }

            for (var i = 1; i <= 4; i++)
            {
                var px = x - dx * i;
                var py = y - dy * i;
                if (px < 0 || py < 0 || px >= size || py >= size) break;
                if (m[py, px]) return false;
            }
            for (var i = 7; i < 11; i++)
            {
                var px = x + dx * i;
                var py = y + dy * i;
                if (px < 0 || py < 0 || px >= size || py >= size) break;
                if (m[py, px]) return false;
            }

            return true;
        }

        /// <summary>按位追加的缓冲区。</summary>
        private sealed class BitBuffer
        {
            private readonly List<byte> _bytes = new List<byte>();
            private int _bitLength;

            public int Length { get { return _bitLength; } }

            public void Append(int value, int bitCount)
            {
                for (var i = bitCount - 1; i >= 0; i--) AppendBit(((value >> i) & 1) != 0);
            }

            private void AppendBit(bool bit)
            {
                var index = _bitLength >> 3;
                if (index == _bytes.Count) _bytes.Add(0);
                if (bit) _bytes[index] |= (byte)(1 << (7 - (_bitLength & 7)));
                _bitLength++;
            }

            public IEnumerable<byte> ToBytes()
            {
                return _bytes;
            }
        }
    }
}
