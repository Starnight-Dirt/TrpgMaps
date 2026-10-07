using System;
using System.IO;
using System.IO.Compression;

namespace TrpgMaps
{
    /// <summary>
    /// 极简 PNG 编码器（8 位灰度），用于输出二维码图片，避免依赖 System.Drawing。
    /// 只实现生成二维码所需的最小功能：单张灰度图、filter=0、zlib(deflate) 压缩。
    /// </summary>
    internal static class PngWriter
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>把已经带 filter 字节的行数据编码为 PNG。</summary>
        public static byte[] EncodeGrayscale(byte[] raw, int width, int height)
        {
            using (var output = new MemoryStream())
            {
                output.Write(Signature, 0, Signature.Length);

                // IHDR
                var ihdr = new byte[13];
                WriteInt32BigEndian(ihdr, 0, width);
                WriteInt32BigEndian(ihdr, 4, height);
                ihdr[8] = 8;  // bit depth
                ihdr[9] = 0;  // color type: grayscale
                ihdr[10] = 0; // compression
                ihdr[11] = 0; // filter method
                ihdr[12] = 0; // interlace: none
                WriteChunk(output, "IHDR", ihdr);

                // IDAT：zlib 容器（0x78 0x01 + deflate + adler32）
                var compressed = Deflate(raw);
                var zlib = new byte[compressed.Length + 6];
                zlib[0] = 0x78;
                zlib[1] = 0x01;
                Buffer.BlockCopy(compressed, 0, zlib, 2, compressed.Length);
                WriteUInt32BigEndian(zlib, zlib.Length - 4, Adler32(raw));
                WriteChunk(output, "IDAT", zlib);

                // IEND
                WriteChunk(output, "IEND", new byte[0]);

                return output.ToArray();
            }
        }

        private static byte[] Deflate(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var deflate = new DeflateStream(ms, CompressionMode.Compress, true))
                {
                    deflate.Write(data, 0, data.Length);
                }
                return ms.ToArray();
            }
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var length = new byte[4];
            WriteInt32BigEndian(length, 0, data.Length);
            stream.Write(length, 0, 4);

            var typeBytes = new byte[4];
            for (var i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
            stream.Write(typeBytes, 0, 4);
            stream.Write(data, 0, data.Length);

            var crc = Crc32(typeBytes, data);
            var crcBytes = new byte[4];
            WriteUInt32BigEndian(crcBytes, 0, crc);
            stream.Write(crcBytes, 0, 4);
        }

        private static void WriteInt32BigEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] first, byte[] second)
        {
            var c = 0xFFFFFFFFu;
            for (var i = 0; i < first.Length; i++) c = CrcTable[(c ^ first[i]) & 0xFF] ^ (c >> 8);
            for (var i = 0; i < second.Length; i++) c = CrcTable[(c ^ second[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        private static uint Adler32(byte[] data)
        {
            const uint mod = 65521;
            uint a = 1, b = 0;
            foreach (var value in data)
            {
                a = (a + value) % mod;
                b = (b + a) % mod;
            }
            return (b << 16) | a;
        }
    }
}
