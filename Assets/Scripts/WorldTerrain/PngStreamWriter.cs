using System;
using System.IO;
using System.IO.Compression;

namespace WorldTerrain
{
    /// <summary>
    /// PNG 写入器：逐行喂入 RGBA8，**行序由调用方决定**（第 0 行走在最前 = 图像顶部）。
    ///
    /// 为什么不用 <c>Texture2D.EncodeToPNG</c>：
    ///   - 贴图行序被着色器钉死（第 0 行 = 北极，因为 uv.y=0 在北极），而 PNG 的第一行是图像顶部，
    ///     所以 EncodeToPNG 写出的文件必然南北颠倒；
    ///   - 它还要一张完整的可读贴图（8192 档 128 MB）与一次整幅编码。
    ///
    /// 这里把扫描线直接做 zlib 压缩后在内存里攒成 IDAT，再落盘：
    ///   - 内存 = 一条带的行缓冲 + 压缩后的 IDAT（比整幅 RGBA 小一个数量级，且与 EncodeToPNG 的返回量级相同）
    ///   - 输出方向与渲染方向一致（上北下南）
    ///   - 不再需要为编码单独建一张贴图
    /// </summary>
    public sealed class PngStreamWriter : IDisposable
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        // CRC-32 查表（IEEE 802.3 多项式，位反转形式）。旧实现每个字节跑 8 次
        // 位循环，8192×4096 的 RGBA 图约 1.3 亿次迭代；查表后每字节一次查表。
        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        // zlib 规范要求每 5552 字节对 adler 取一次模，避免 uint 溢出。
        private const int AdlerBlock = 5552;

        private readonly int _width;
        private readonly int _height;
        private int _rowsWritten;
        private bool _finished;

        private readonly MemoryStream _idat = new MemoryStream();
        private readonly DeflateStream _deflate;

        // zlib 的 adler32 校验（覆盖未压缩数据）
        private uint _adlerA = 1;
        private uint _adlerB = 0;

        public PngStreamWriter(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));

            _width = width;
            _height = height;

            // zlib 头：0x78 0x9C（deflate, 默认压缩）
            _idat.WriteByte(0x78);
            _idat.WriteByte(0x9C);

            _deflate = new DeflateStream(_idat, CompressionLevel.Optimal, leaveOpen: true);
        }

        /// <summary>写入若干行。每行 <paramref name="width"/> 个 RGBA 像素。</summary>
        public void WriteRow(byte[] rgba, int offset = 0)
        {
            if (_finished)
                throw new InvalidOperationException("已结束写入");
            if (_rowsWritten >= _height)
                throw new InvalidOperationException($"行数超出图像高度 {_height}");

            // 过滤器类型 0（None）
            _deflate.WriteByte(0);
            // 字节 0：adler 的 s1 不变，s2 累加 s1（等价于对值 0 做一次 UpdateAdler）
            _adlerB += _adlerA;

            int need = _width * 4;
            _deflate.Write(rgba, offset, need);

            // adler32：s1 = Σbytes + 1, s2 = Σs1。按块取模，避免逐字节 % 65521。
            int remaining = need;
            int at = offset;
            while (remaining > 0)
            {
                int take = remaining < AdlerBlock ? remaining : AdlerBlock;
                for (int i = 0; i < take; i++)
                {
                    _adlerA += rgba[at++];
                    _adlerB += _adlerA;
                }
                _adlerA %= 65521u;
                _adlerB %= 65521u;
                remaining -= take;
            }

            _rowsWritten++;
        }

        /// <summary>结束并写盘。返回写入的字节数；行数不足则返回 -1 且不写文件。</summary>
        public long Finish(string path)
        {
            if (_finished)
                throw new InvalidOperationException("重复结束");
            _finished = true;

            _deflate.Dispose();

            if (_rowsWritten != _height)
                return -1;

            // adler32 尾（大端）
            uint adler = (_adlerB << 16) | _adlerA;
            _idat.WriteByte((byte)(adler >> 24));
            _idat.WriteByte((byte)(adler >> 16));
            _idat.WriteByte((byte)(adler >> 8));
            _idat.WriteByte((byte)adler);

            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write,
                                             FileShare.None, 1 << 16))
            {
                file.Write(Signature, 0, Signature.Length);

                WriteIhdr(file);

                // IDAT 可能几十 MB，ToArray() 会再整份拷贝一次。改为直接把
                // MemoryStream 的内部缓冲交给 WriteChunk，按长度写出，省一份峰值内存。
                WriteChunk(file, "IDAT", _idat.GetBuffer(), (int)_idat.Length);

                WriteChunk(file, "IEND", Array.Empty<byte>());

                return file.Length;
            }
        }

        private void WriteIhdr(Stream file)
        {
            var ihdr = new byte[13];
            WriteInt(ihdr, 0, _width);
            WriteInt(ihdr, 4, _height);
            ihdr[8] = 8;    // bit depth
            ihdr[9] = 6;    // color type RGBA
            ihdr[10] = 0;   // compression
            ihdr[11] = 0;   // filter
            ihdr[12] = 0;   // interlace
            WriteChunk(file, "IHDR", ihdr);
        }

        private static void WriteInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        /// <summary>写一个 chunk（长度 + type + data + CRC）。</summary>
        private static void WriteChunk(Stream file, string type, byte[] data)
            => WriteChunk(file, type, data, data.Length);

        /// <summary>
        /// 写一个 chunk，允许只取 <paramref name="data"/> 的前 <paramref name="length"/> 字节。
        /// 供 IDAT 直接复用 MemoryStream 内部缓冲，避免整份拷贝。
        /// </summary>
        private static void WriteChunk(Stream file, string type, byte[] data, int length)
        {
            var header = new byte[8];
            WriteInt(header, 0, length);
            for (int i = 0; i < 4; i++)
                header[4 + i] = (byte)type[i];
            file.Write(header, 0, header.Length);
            file.Write(data, 0, length);

            // CRC 覆盖 chunk type + data（不含长度字段）
            uint crc = 0xFFFFFFFFu;
            for (int i = 4; i < 8; i++)
                crc = CrcTable[(byte)(crc ^ header[i])] ^ (crc >> 8);
            for (int i = 0; i < length; i++)
                crc = CrcTable[(byte)(crc ^ data[i])] ^ (crc >> 8);
            crc ^= 0xFFFFFFFFu;

            var tail = new byte[4];
            WriteInt(tail, 0, unchecked((int)crc));
            file.Write(tail, 0, 4);
        }

        public void Dispose()
        {
            if (!_finished)
            {
                _finished = true;
                _deflate.Dispose();
            }
        }
    }
}
