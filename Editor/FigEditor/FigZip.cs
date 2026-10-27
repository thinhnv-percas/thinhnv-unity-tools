using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>One member of a zip archive, held in memory.</summary>
    public sealed class FigZipEntry
    {
        public string Name;
        public byte[] Data;

        public FigZipEntry(string name, byte[] data)
        {
            Name = name;
            Data = data;
        }
    }

    /// <summary>
    /// A minimal zip reader / writer: enough for <c>.fig</c> archives (stored or deflated members,
    /// Zip64 sizes on read). It only needs <see cref="DeflateStream"/>, which every Unity scripting
    /// profile ships, instead of <c>System.IO.Compression.ZipArchive</c>, which the .NET Framework
    /// profile doesn't reference by default.
    /// </summary>
    public static class FigZip
    {
        private const uint LocalHeaderSig = 0x04034b50;
        private const uint CentralHeaderSig = 0x02014b50;
        private const uint EndOfCentralSig = 0x06054b50;
        private const uint Zip64EndOfCentralSig = 0x06064b50;
        private const uint Zip64LocatorSig = 0x07064b50;

        public static bool IsZip(byte[] data)
        {
            return data != null && data.Length >= 4 && data[0] == 'P' && data[1] == 'K';
        }

        public static List<FigZipEntry> Read(byte[] data, long maxMember = 1L << 30)
        {
            int eocd = FindEndOfCentral(data);
            if (eocd < 0)
            {
                throw new FormatException("zip: no end of central directory");
            }

            long entryCount = U16(data, eocd + 10);
            long centralOffset = U32(data, eocd + 16);

            // Zip64: the locator sits right before the classic record.
            if ((entryCount == 0xFFFF || centralOffset == 0xFFFFFFFF) && eocd >= 20 && U32(data, eocd - 20) == Zip64LocatorSig)
            {
                long z64 = (long)U64(data, eocd - 20 + 8);
                Check(z64 >= 0 && z64 + 56 <= data.Length && U32(data, (int)z64) == Zip64EndOfCentralSig, "bad zip64 record");
                entryCount = (long)U64(data, (int)z64 + 32);
                centralOffset = (long)U64(data, (int)z64 + 48);
            }

            var entries = new List<FigZipEntry>();
            long pos = centralOffset;
            for (long i = 0; i < entryCount; i++)
            {
                Check(pos + 46 <= data.Length && U32(data, (int)pos) == CentralHeaderSig, "bad central directory");
                int p = (int)pos;
                int method = U16(data, p + 10);
                long compressedSize = U32(data, p + 20);
                long size = U32(data, p + 24);
                int nameLength = U16(data, p + 28);
                int extraLength = U16(data, p + 30);
                int commentLength = U16(data, p + 32);
                long localOffset = U32(data, p + 42);
                Check(p + 46 + nameLength + extraLength + commentLength <= data.Length, "bad central directory");
                string name = Encoding.UTF8.GetString(data, p + 46, nameLength);

                // Zip64 extra field: only the sizes that overflowed are present, in this order.
                int extra = p + 46 + nameLength;
                int extraEnd = extra + extraLength;
                while (extra + 4 <= extraEnd)
                {
                    int id = U16(data, extra);
                    int len = U16(data, extra + 2);
                    if (id == 0x0001)
                    {
                        int q = extra + 4;
                        if (size == 0xFFFFFFFF && q + 8 <= extraEnd)
                        {
                            size = (long)U64(data, q);
                            q += 8;
                        }

                        if (compressedSize == 0xFFFFFFFF && q + 8 <= extraEnd)
                        {
                            compressedSize = (long)U64(data, q);
                            q += 8;
                        }

                        if (localOffset == 0xFFFFFFFF && q + 8 <= extraEnd)
                        {
                            localOffset = (long)U64(data, q);
                        }
                    }

                    extra += 4 + len;
                }

                pos = p + 46 + nameLength + extraLength + commentLength;

                if (name.EndsWith("/", StringComparison.Ordinal))
                {
                    continue; // Directory entry.
                }

                Check(localOffset >= 0 && localOffset + 30 <= data.Length && U32(data, (int)localOffset) == LocalHeaderSig, $"bad local header for {name}");
                int lp = (int)localOffset;
                long dataStart = lp + 30L + U16(data, lp + 26) + U16(data, lp + 28);
                Check(compressedSize >= 0 && dataStart + compressedSize <= data.Length, $"truncated member {name}");
                Check(size <= maxMember, $"member {name} is too large");

                byte[] content;
                if (method == 0)
                {
                    content = new byte[compressedSize];
                    Buffer.BlockCopy(data, (int)dataStart, content, 0, (int)compressedSize);
                }
                else if (method == 8)
                {
                    content = FigUtil.Inflate(data, (int)dataStart, (int)compressedSize, maxMember);
                }
                else
                {
                    throw new FormatException($"zip: member {name} uses unsupported compression method {method}");
                }

                entries.Add(new FigZipEntry(name, content));
            }

            return entries;
        }

        /// <summary>Write <paramref name="entries"/> as a zip; members named in <paramref name="deflate"/> are deflated, the rest stored.</summary>
        public static byte[] Write(IList<FigZipEntry> entries, Func<string, bool> deflate = null)
        {
            using var output = new MemoryStream();
            var central = new MemoryStream();
            var w = new BinaryWriter(output);
            var c = new BinaryWriter(central);
            uint dosTime = DosTime(DateTime.Now);

            foreach (FigZipEntry entry in entries)
            {
                byte[] name = Encoding.UTF8.GetBytes(entry.Name);
                bool compress = deflate != null && deflate(entry.Name);
                byte[] payload = compress ? FigUtil.Deflate(entry.Data) : entry.Data;
                uint crc = Crc32.Compute(entry.Data);
                long offset = output.Position;
                if (offset > uint.MaxValue || payload.LongLength > uint.MaxValue)
                {
                    throw new IOException("zip: archives over 4 GB are not supported");
                }

                ushort method = compress ? (ushort)8 : (ushort)0;
                // Bit 11: names are UTF-8.
                const ushort flags = 1 << 11;

                w.Write(LocalHeaderSig);
                w.Write((ushort)20);
                w.Write(flags);
                w.Write(method);
                w.Write(dosTime);
                w.Write(crc);
                w.Write((uint)payload.Length);
                w.Write((uint)entry.Data.Length);
                w.Write((ushort)name.Length);
                w.Write((ushort)0);
                w.Write(name);
                w.Write(payload);

                c.Write(CentralHeaderSig);
                c.Write((ushort)20);
                c.Write((ushort)20);
                c.Write(flags);
                c.Write(method);
                c.Write(dosTime);
                c.Write(crc);
                c.Write((uint)payload.Length);
                c.Write((uint)entry.Data.Length);
                c.Write((ushort)name.Length);
                c.Write((ushort)0);
                c.Write((ushort)0);
                c.Write((ushort)0);
                c.Write((ushort)0);
                c.Write((uint)0);
                c.Write((uint)offset);
                c.Write(name);
            }

            c.Flush();
            long centralOffset = output.Position;
            central.Position = 0;
            central.CopyTo(output);

            w.Write(EndOfCentralSig);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)entries.Count);
            w.Write((ushort)entries.Count);
            w.Write((uint)central.Length);
            w.Write((uint)centralOffset);
            w.Write((ushort)0);
            w.Flush();
            return output.ToArray();
        }

        private static int FindEndOfCentral(byte[] data)
        {
            int min = Math.Max(0, data.Length - 22 - 0xFFFF);
            for (int i = data.Length - 22; i >= min; i--)
            {
                if (U32(data, i) == EndOfCentralSig)
                {
                    return i;
                }
            }

            return -1;
        }

        private static uint DosTime(DateTime t)
        {
            if (t.Year < 1980)
            {
                t = new DateTime(1980, 1, 1);
            }

            uint date = (uint)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day);
            uint time = (uint)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
            return (date << 16) | time;
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new FormatException("zip: " + message);
            }
        }

        private static int U16(byte[] d, int p) => d[p] | (d[p + 1] << 8);

        private static uint U32(byte[] d, int p) => (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16)) | ((uint)d[p + 3] << 24);

        private static ulong U64(byte[] d, int p) => U32(d, p) | ((ulong)U32(d, p + 4) << 32);
    }

    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            return table;
        }

        public static uint Compute(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFF;
        }
    }

    /// <summary>Small helpers shared by the .fig reader, writer and window.</summary>
    public static class FigUtil
    {
        private const string HexDigits = "0123456789abcdef";

        public static string Hex(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[2 * i] = HexDigits[bytes[i] >> 4];
                chars[2 * i + 1] = HexDigits[bytes[i] & 15];
            }

            return new string(chars);
        }

        public static byte[] FromHex(string hex)
        {
            if (hex == null || hex.Length % 2 != 0)
            {
                return null;
            }

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int hi = HexDigits.IndexOf(char.ToLowerInvariant(hex[2 * i]));
                int lo = HexDigits.IndexOf(char.ToLowerInvariant(hex[2 * i + 1]));
                if (hi < 0 || lo < 0)
                {
                    return null;
                }

                bytes[i] = (byte)((hi << 4) | lo);
            }

            return bytes;
        }

        /// <summary><c>sessionID:localID</c> of a GUID struct, or null.</summary>
        public static string GuidString(KiwiValue guid)
        {
            KiwiValue session = guid?.Get("sessionID");
            KiwiValue local = guid?.Get("localID");
            if (session == null || local == null)
            {
                return null;
            }

            return session.Uint + ":" + local.Uint;
        }

        public static void JsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char ch in s ?? "")
            {
                switch (ch)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (ch < 0x20)
                        {
                            sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(ch);
                        }

                        break;
                }
            }

            sb.Append('"');
        }

        public static byte[] Sha1(byte[] data)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            return sha.ComputeHash(data);
        }

        /// <summary>Raw-deflate decompress <paramref name="count"/> bytes at <paramref name="offset"/>.</summary>
        public static byte[] Inflate(byte[] data, int offset, int count, long max)
        {
            using var input = new MemoryStream(data, offset, count, false);
            using var inflater = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > max)
                {
                    throw new FormatException("deflate: decompressed data too large");
                }
            }

            return output.ToArray();
        }

        public static byte[] Deflate(byte[] data)
        {
            using var output = new MemoryStream();
            using (var deflater = new DeflateStream(output, CompressionMode.Compress, true))
            {
                deflater.Write(data, 0, data.Length);
            }

            return output.ToArray();
        }

        /// <summary>The file extension an image's bytes say it has (".png", ".jpg", …), or ".bin".</summary>
        public static string ImageExtension(byte[] b)
        {
            if (b == null || b.Length < 12)
            {
                return ".bin";
            }

            if (b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
            {
                return ".png";
            }

            if (b[0] == 0xFF && b[1] == 0xD8)
            {
                return ".jpg";
            }

            if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
            {
                return ".gif";
            }

            if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
            {
                return ".webp";
            }

            if (b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F')
            {
                return ".pdf";
            }

            if (b[0] == '<')
            {
                return ".svg";
            }

            return ".bin";
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024f).ToString("0.#") + " KB";
            }

            return (bytes / (1024f * 1024f)).ToString("0.#") + " MB";
        }
    }
}
