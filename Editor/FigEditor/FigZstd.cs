using System;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// A small, self-contained Zstandard decoder (RFC 8878). Newer Figma files compress the
    /// <c>canvas.fig</c> message chunk with zstd, and neither Unity nor .NET ship a decoder, so this
    /// one covers what those files use: raw / RLE / compressed blocks, Huffman literals (1 or 4
    /// streams, treeless reuse), FSE sequences (predefined / RLE / compressed / repeat tables) and
    /// skippable frames. Dictionaries are not supported (Figma doesn't use them). The checksum is
    /// skipped, not verified.
    ///
    /// Every read is bounds-checked; malformed input throws <see cref="FormatException"/>.
    /// </summary>
    public static class FigZstd
    {
        private const uint FrameMagic = 0xFD2FB528;
        private const uint SkippableMagicMask = 0xFFFFFFF0;
        private const uint SkippableMagic = 0x184D2A50;
        private const int MaxBlockSize = 128 * 1024;

        private static readonly short[] LlDefault =
        {
            4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1, -1, -1, -1, -1,
        };

        private static readonly short[] MlDefault =
        {
            1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, -1,
        };

        private static readonly short[] OfDefault =
        {
            1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1,
        };

        private static readonly int[] LlBase =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
            16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536,
        };

        private static readonly byte[] LlBits =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        };

        private static readonly int[] MlBase =
        {
            3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
            35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051, 4099, 8195, 16387, 32771, 65539,
        };

        private static readonly byte[] MlBits =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        };

        // Predefined tables are immutable once built, so they are shared between calls.
        private static FseTable llPredefined;
        private static FseTable mlPredefined;
        private static FseTable ofPredefined;

        /// <summary>Whether <paramref name="data"/> starts with the zstd frame magic number.</summary>
        public static bool IsZstd(byte[] data)
        {
            return data != null && data.Length >= 4 && ReadU32(data, 0) == FrameMagic;
        }

        /// <summary>
        /// Decompress every frame in <paramref name="data"/>. <paramref name="maxOutput"/> caps the
        /// decompressed size so a tiny malicious chunk can't claim gigabytes.
        /// </summary>
        public static byte[] Decompress(byte[] data, long maxOutput = 1L << 30)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            var output = new Output((int)Math.Min(Math.Max(1024L, data.Length * 4L), 64L << 20), maxOutput);
            int pos = 0;
            int end = data.Length;
            while (pos < end)
            {
                Require(pos + 4 <= end, "truncated frame");
                uint magic = ReadU32(data, pos);
                pos += 4;
                if ((magic & SkippableMagicMask) == SkippableMagic)
                {
                    Require(pos + 4 <= end, "truncated skippable frame");
                    long size = ReadU32(data, pos);
                    pos += 4;
                    Require(pos + size <= end, "truncated skippable frame");
                    pos += (int)size;
                    continue;
                }

                Require(magic == FrameMagic, "not a zstd frame");
                pos = DecodeFrame(data, pos, end, output);
            }

            return output.ToArray();
        }

        private static int DecodeFrame(byte[] data, int pos, int end, Output output)
        {
            Require(pos < end, "truncated frame header");
            int fhd = data[pos++];
            int fcsFlag = fhd >> 6;
            bool singleSegment = ((fhd >> 5) & 1) != 0;
            bool checksum = ((fhd >> 2) & 1) != 0;
            int dictFlag = fhd & 3;
            Require((fhd & 8) == 0, "reserved frame header bit set");

            if (!singleSegment)
            {
                pos++; // Window descriptor: the whole frame is decoded into one buffer, so unused.
            }

            if (dictFlag != 0)
            {
                int dictSize = dictFlag == 3 ? 4 : dictFlag;
                Require(pos + dictSize <= end, "truncated frame header");
                uint dictId = 0;
                for (int i = 0; i < dictSize; i++)
                {
                    dictId |= (uint)data[pos + i] << (8 * i);
                }

                pos += dictSize;
                Require(dictId == 0, "zstd dictionaries are not supported");
            }

            int fcsSize = fcsFlag == 0 ? (singleSegment ? 1 : 0) : 1 << fcsFlag;
            pos += fcsSize;
            Require(pos <= end, "truncated frame header");

            var state = new FrameState();
            while (true)
            {
                Require(pos + 3 <= end, "truncated block header");
                int header = data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16);
                pos += 3;
                bool last = (header & 1) != 0;
                int type = (header >> 1) & 3;
                int size = header >> 3;
                switch (type)
                {
                    case 0:
                        Require(pos + size <= end, "truncated raw block");
                        output.Append(data, pos, size);
                        pos += size;
                        break;
                    case 1:
                        Require(pos < end, "truncated RLE block");
                        output.AppendRepeat(data[pos], size);
                        pos += 1;
                        break;
                    case 2:
                        Require(size <= MaxBlockSize && pos + size <= end, "truncated compressed block");
                        DecodeBlock(data, pos, pos + size, state, output);
                        pos += size;
                        break;
                    default:
                        throw new FormatException("zstd: reserved block type");
                }

                if (last)
                {
                    break;
                }
            }

            if (checksum)
            {
                pos += 4;
                Require(pos <= end, "truncated frame checksum");
            }

            return pos;
        }

        // ─── Blocks ──────────────────────────────────────────────────────────────

        private sealed class FrameState
        {
            public readonly int[] Reps = { 1, 4, 8 };
            public FseTable LiteralLengths;
            public FseTable Offsets;
            public FseTable MatchLengths;
            public HuffmanTable Huffman;
        }

        private static void DecodeBlock(byte[] data, int pos, int end, FrameState state, Output output)
        {
            byte[] literals = DecodeLiterals(data, ref pos, end, state, out int literalCount);
            Require(pos < end, "truncated sequences header");

            int b0 = data[pos++];
            int sequenceCount;
            if (b0 == 0)
            {
                output.Append(literals, 0, literalCount);
                return;
            }

            if (b0 < 128)
            {
                sequenceCount = b0;
            }
            else if (b0 < 255)
            {
                Require(pos < end, "truncated sequences header");
                sequenceCount = ((b0 - 128) << 8) + data[pos++];
            }
            else
            {
                Require(pos + 2 <= end, "truncated sequences header");
                sequenceCount = data[pos] + (data[pos + 1] << 8) + 0x7F00;
                pos += 2;
            }

            Require(pos < end, "truncated sequences header");
            int modes = data[pos++];
            state.LiteralLengths = ReadSequenceTable(data, ref pos, end, (modes >> 6) & 3, LlDefault, 6, 35, 9, state.LiteralLengths, ref llPredefined);
            state.Offsets = ReadSequenceTable(data, ref pos, end, (modes >> 4) & 3, OfDefault, 5, 31, 8, state.Offsets, ref ofPredefined);
            state.MatchLengths = ReadSequenceTable(data, ref pos, end, (modes >> 2) & 3, MlDefault, 6, 52, 9, state.MatchLengths, ref mlPredefined);

            FseTable ll = state.LiteralLengths;
            FseTable of = state.Offsets;
            FseTable ml = state.MatchLengths;
            int[] reps = state.Reps;

            var bits = new BackwardBits(data, pos, end);
            int llState = (int)bits.Read(ll.Log);
            int ofState = (int)bits.Read(of.Log);
            int mlState = (int)bits.Read(ml.Log);
            int literalPos = 0;

            for (int i = 0; i < sequenceCount; i++)
            {
                int ofCode = of.Symbol[ofState];
                int mlCode = ml.Symbol[mlState];
                int llCode = ll.Symbol[llState];
                Require(ofCode <= 31 && mlCode < MlBase.Length && llCode < LlBase.Length, "bad sequence code");

                long offsetValue = (1L << ofCode) + bits.Read(ofCode);
                int matchLength = MlBase[mlCode] + (int)bits.Read(MlBits[mlCode]);
                int literalLength = LlBase[llCode] + (int)bits.Read(LlBits[llCode]);

                int offset;
                if (offsetValue > 3)
                {
                    offset = (int)Math.Min(offsetValue - 3, int.MaxValue);
                    reps[2] = reps[1];
                    reps[1] = reps[0];
                    reps[0] = offset;
                }
                else
                {
                    int index = (int)offsetValue - 1 + (literalLength == 0 ? 1 : 0);
                    switch (index)
                    {
                        case 0:
                            offset = reps[0];
                            break;
                        case 1:
                            offset = reps[1];
                            reps[1] = reps[0];
                            reps[0] = offset;
                            break;
                        case 2:
                            offset = reps[2];
                            reps[2] = reps[1];
                            reps[1] = reps[0];
                            reps[0] = offset;
                            break;
                        default:
                            offset = reps[0] - 1;
                            reps[2] = reps[1];
                            reps[1] = reps[0];
                            reps[0] = offset;
                            break;
                    }
                }

                if (i < sequenceCount - 1)
                {
                    llState = ll.Base[llState] + (int)bits.Read(ll.Bits[llState]);
                    mlState = ml.Base[mlState] + (int)bits.Read(ml.Bits[mlState]);
                    ofState = of.Base[ofState] + (int)bits.Read(of.Bits[ofState]);
                }

                Require(literalLength <= literalCount - literalPos, "sequence reads past the literals");
                output.Append(literals, literalPos, literalLength);
                literalPos += literalLength;

                Require(offset > 0 && offset <= output.Length, "bad match offset");
                output.CopyMatch(offset, matchLength);
            }

            Require(bits.Position == 0, "sequence stream not fully consumed");
            output.Append(literals, literalPos, literalCount - literalPos);
        }

        private static FseTable ReadSequenceTable(byte[] data, ref int pos, int end, int mode, short[] defaults, int defaultLog,
            int maxSymbol, int maxLog, FseTable previous, ref FseTable predefined)
        {
            switch (mode)
            {
                case 0:
                    return predefined ??= FseTable.Build(defaults, defaults.Length, defaultLog);
                case 1:
                    Require(pos < end, "truncated RLE table");
                    Require(data[pos] <= maxSymbol, "bad RLE symbol");
                    return FseTable.Rle(data[pos++]);
                case 2:
                {
                    var probs = new short[maxSymbol + 1];
                    int count = ReadFseDescription(data, ref pos, end, probs, maxSymbol, maxLog, out int log);
                    return FseTable.Build(probs, count, log);
                }
                default:
                    Require(previous != null, "repeat table without a previous one");
                    return previous;
            }
        }

        // ─── Literals ────────────────────────────────────────────────────────────

        private static byte[] DecodeLiterals(byte[] data, ref int pos, int end, FrameState state, out int count)
        {
            Require(pos < end, "truncated literals header");
            int b0 = data[pos];
            int type = b0 & 3;
            int sizeFormat = (b0 >> 2) & 3;

            if (type == 0 || type == 1)
            {
                int size;
                switch (sizeFormat)
                {
                    case 0:
                    case 2:
                        size = b0 >> 3;
                        pos += 1;
                        break;
                    case 1:
                        Require(pos + 2 <= end, "truncated literals header");
                        size = (b0 >> 4) + (data[pos + 1] << 4);
                        pos += 2;
                        break;
                    default:
                        Require(pos + 3 <= end, "truncated literals header");
                        size = (b0 >> 4) + (data[pos + 1] << 4) + (data[pos + 2] << 12);
                        pos += 3;
                        break;
                }

                Require(size <= MaxBlockSize, "literals too large");
                var raw = new byte[size];
                count = size;
                if (type == 0)
                {
                    Require(pos + size <= end, "truncated raw literals");
                    Buffer.BlockCopy(data, pos, raw, 0, size);
                    pos += size;
                }
                else
                {
                    Require(pos < end, "truncated RLE literals");
                    byte value = data[pos++];
                    for (int i = 0; i < size; i++)
                    {
                        raw[i] = value;
                    }
                }

                return raw;
            }

            int regenerated;
            int compressed;
            int streams;
            if (sizeFormat <= 1)
            {
                Require(pos + 3 <= end, "truncated literals header");
                int c = b0 | (data[pos + 1] << 8) | (data[pos + 2] << 16);
                regenerated = (c >> 4) & 0x3FF;
                compressed = (c >> 14) & 0x3FF;
                streams = sizeFormat == 0 ? 1 : 4;
                pos += 3;
            }
            else if (sizeFormat == 2)
            {
                Require(pos + 4 <= end, "truncated literals header");
                uint c = (uint)(b0 | (data[pos + 1] << 8) | (data[pos + 2] << 16)) | ((uint)data[pos + 3] << 24);
                regenerated = (int)((c >> 4) & 0x3FFF);
                compressed = (int)((c >> 18) & 0x3FFF);
                streams = 4;
                pos += 4;
            }
            else
            {
                Require(pos + 5 <= end, "truncated literals header");
                ulong c = (ulong)(uint)(b0 | (data[pos + 1] << 8) | (data[pos + 2] << 16)) | ((ulong)data[pos + 3] << 24) | ((ulong)data[pos + 4] << 32);
                regenerated = (int)((c >> 4) & 0x3FFFF);
                compressed = (int)((c >> 22) & 0x3FFFF);
                streams = 4;
                pos += 5;
            }

            Require(regenerated <= MaxBlockSize, "literals too large");
            int literalsEnd = pos + compressed;
            Require(literalsEnd <= end, "truncated compressed literals");

            if (type == 2)
            {
                state.Huffman = HuffmanTable.Read(data, ref pos, literalsEnd);
            }
            else
            {
                Require(state.Huffman != null, "treeless literals without a previous table");
            }

            var literals = new byte[regenerated];
            count = regenerated;
            if (streams == 1)
            {
                state.Huffman.DecodeStream(data, pos, literalsEnd, literals, 0, regenerated);
            }
            else
            {
                Require(pos + 6 <= literalsEnd, "truncated jump table");
                int s1 = data[pos] | (data[pos + 1] << 8);
                int s2 = data[pos + 2] | (data[pos + 3] << 8);
                int s3 = data[pos + 4] | (data[pos + 5] << 8);
                int p = pos + 6;
                int b1 = p + s1;
                int b2 = b1 + s2;
                int b3 = b2 + s3;
                Require(b3 <= literalsEnd, "bad jump table");
                int per = (regenerated + 3) / 4;
                int last = regenerated - 3 * per;
                Require(last >= 0, "bad literals size");
                state.Huffman.DecodeStream(data, p, b1, literals, 0, per);
                state.Huffman.DecodeStream(data, b1, b2, literals, per, per);
                state.Huffman.DecodeStream(data, b2, b3, literals, 2 * per, per);
                state.Huffman.DecodeStream(data, b3, literalsEnd, literals, 3 * per, last);
            }

            pos = literalsEnd;
            return literals;
        }

        private sealed class HuffmanTable
        {
            private int log;
            private byte[] symbol;
            private byte[] bits;

            public static HuffmanTable Read(byte[] data, ref int pos, int end)
            {
                Require(pos < end, "truncated Huffman header");
                int header = data[pos++];
                var weights = new byte[256];
                int weightCount = 0;

                if (header >= 128)
                {
                    int n = header - 127;
                    int bytes = (n + 1) / 2;
                    Require(pos + bytes <= end, "truncated Huffman weights");
                    for (int i = 0; i < n; i++)
                    {
                        int b = data[pos + i / 2];
                        weights[weightCount++] = (byte)(i % 2 == 0 ? b >> 4 : b & 15);
                    }

                    pos += bytes;
                }
                else
                {
                    Require(pos + header <= end, "truncated Huffman weights");
                    int weightsEnd = pos + header;
                    var probs = new short[256];
                    int p = pos;
                    int probCount = ReadFseDescription(data, ref p, weightsEnd, probs, 255, 6, out int fseLog);
                    FseTable table = FseTable.Build(probs, probCount, fseLog);
                    var bitsIn = new BackwardBits(data, p, weightsEnd);
                    int s1 = (int)bitsIn.Read(fseLog);
                    int s2 = (int)bitsIn.Read(fseLog);
                    while (true)
                    {
                        Require(weightCount < 255, "too many Huffman weights");
                        weights[weightCount++] = table.Symbol[s1];
                        s1 = table.Base[s1] + (int)bitsIn.Read(table.Bits[s1]);
                        if (bitsIn.Position < 0)
                        {
                            weights[weightCount++] = table.Symbol[s2];
                            break;
                        }

                        Require(weightCount < 255, "too many Huffman weights");
                        weights[weightCount++] = table.Symbol[s2];
                        s2 = table.Base[s2] + (int)bitsIn.Read(table.Bits[s2]);
                        if (bitsIn.Position < 0)
                        {
                            weights[weightCount++] = table.Symbol[s1];
                            break;
                        }
                    }

                    pos = weightsEnd;
                }

                long total = 0;
                for (int i = 0; i < weightCount; i++)
                {
                    Require(weights[i] <= 11, "Huffman weight too large");
                    if (weights[i] > 0)
                    {
                        total += 1L << (weights[i] - 1);
                    }
                }

                Require(total > 0, "empty Huffman table");
                int maxBits = HighBit(total) + 1;
                long left = (1L << maxBits) - total;
                Require((left & (left - 1)) == 0, "bad Huffman weights");
                Require(weightCount < 256, "too many Huffman weights");
                weights[weightCount++] = (byte)(HighBit(left) + 1);
                Require(maxBits <= 11, "Huffman table too deep");

                var t = new HuffmanTable { log = maxBits };
                int size = 1 << maxBits;
                t.symbol = new byte[size];
                t.bits = new byte[size];
                var rankCount = new int[maxBits + 2];
                for (int i = 0; i < weightCount; i++)
                {
                    Require(weights[i] <= maxBits, "bad Huffman weights");
                    rankCount[weights[i]]++;
                }

                var rankStart = new int[maxBits + 2];
                int next = 0;
                for (int w = 1; w <= maxBits; w++)
                {
                    rankStart[w] = next;
                    next += rankCount[w] << (w - 1);
                }

                Require(next == size, "bad Huffman weights");
                for (int s = 0; s < weightCount; s++)
                {
                    int w = weights[s];
                    if (w == 0)
                    {
                        continue;
                    }

                    int length = 1 << (w - 1);
                    int start = rankStart[w];
                    byte nb = (byte)(maxBits + 1 - w);
                    for (int i = start; i < start + length; i++)
                    {
                        t.symbol[i] = (byte)s;
                        t.bits[i] = nb;
                    }

                    rankStart[w] += length;
                }

                return t;
            }

            public void DecodeStream(byte[] data, int start, int end, byte[] output, int offset, int count)
            {
                if (count == 0 && start == end)
                {
                    return;
                }

                var bitsIn = new BackwardBits(data, start, end);
                for (int i = 0; i < count; i++)
                {
                    int entry = (int)bitsIn.Peek(log);
                    output[offset + i] = symbol[entry];
                    bitsIn.Skip(bits[entry]);
                }

                Require(bitsIn.Position == 0, "Huffman stream not fully consumed");
            }
        }

        // ─── FSE ─────────────────────────────────────────────────────────────────

        private sealed class FseTable
        {
            public int Log;
            public byte[] Symbol;
            public byte[] Bits;
            public int[] Base;

            public static FseTable Rle(byte symbol)
            {
                return new FseTable { Log = 0, Symbol = new[] { symbol }, Bits = new byte[1], Base = new int[1] };
            }

            public static FseTable Build(short[] probs, int count, int log)
            {
                int size = 1 << log;
                var t = new FseTable { Log = log, Symbol = new byte[size], Bits = new byte[size], Base = new int[size] };
                var next = new int[count];
                int high = size - 1;
                for (int s = 0; s < count; s++)
                {
                    if (probs[s] == -1)
                    {
                        Require(high >= 0, "bad FSE distribution");
                        t.Symbol[high--] = (byte)s;
                        next[s] = 1;
                    }
                    else
                    {
                        next[s] = probs[s];
                    }
                }

                int step = (size >> 1) + (size >> 3) + 3;
                int mask = size - 1;
                int pos = 0;
                for (int s = 0; s < count; s++)
                {
                    for (int i = 0; i < probs[s]; i++)
                    {
                        t.Symbol[pos] = (byte)s;
                        do
                        {
                            pos = (pos + step) & mask;
                        }
                        while (pos > high);
                    }
                }

                Require(pos == 0, "bad FSE distribution");
                for (int u = 0; u < size; u++)
                {
                    int s = t.Symbol[u];
                    Require(s < count, "bad FSE distribution");
                    int state = next[s]++;
                    Require(state > 0, "bad FSE distribution");
                    int nb = log - HighBit(state);
                    t.Bits[u] = (byte)nb;
                    t.Base[u] = (state << nb) - size;
                }

                return t;
            }
        }

        /// <summary>Reads an FSE table description into <paramref name="probs"/>; returns the symbol count.</summary>
        private static int ReadFseDescription(byte[] data, ref int pos, int end, short[] probs, int maxSymbol, int maxLog, out int log)
        {
            long bitPos = 0;
            int start = pos;
            log = (int)GetBits(data, start, end, bitPos, 4) + 5;
            bitPos += 4;
            Require(log <= maxLog, "FSE accuracy too large");

            int remaining = (1 << log) + 1;
            int threshold = 1 << log;
            int nbBits = log + 1;
            int count = 0;
            bool previousZero = false;

            while (remaining > 1 && count <= maxSymbol)
            {
                if (previousZero)
                {
                    int zeroEnd = count;
                    while (true)
                    {
                        int repeat = (int)GetBits(data, start, end, bitPos, 2);
                        bitPos += 2;
                        zeroEnd += repeat;
                        if (repeat != 3)
                        {
                            break;
                        }
                    }

                    Require(zeroEnd <= maxSymbol + 1, "FSE zero run too long");
                    while (count < zeroEnd)
                    {
                        probs[count++] = 0;
                    }

                    if (count > maxSymbol)
                    {
                        break;
                    }
                }

                int max = (2 * threshold - 1) - remaining;
                int value = (int)GetBits(data, start, end, bitPos, nbBits);
                int low = value & (threshold - 1);
                int c;
                if (low < max)
                {
                    c = low;
                    bitPos += nbBits - 1;
                }
                else
                {
                    c = value & (2 * threshold - 1);
                    if (c >= threshold)
                    {
                        c -= max;
                    }

                    bitPos += nbBits;
                }

                c--;
                remaining -= c < 0 ? -c : c;
                probs[count++] = (short)c;
                previousZero = c == 0;
                while (remaining < threshold)
                {
                    nbBits--;
                    threshold >>= 1;
                }
            }

            Require(remaining == 1, "bad FSE description");
            int consumed = (int)((bitPos + 7) >> 3);
            Require(start + consumed <= end, "truncated FSE description");
            pos = start + consumed;
            return count;
        }

        // ─── Bit reading ─────────────────────────────────────────────────────────

        /// <summary>
        /// A bitstream read from its end towards its start, as zstd writes them. The last byte's
        /// highest set bit marks where the data begins; reading past the start yields zeros and
        /// drives <see cref="Position"/> negative (the FSE decoders use that as "overflowed").
        /// </summary>
        private struct BackwardBits
        {
            private readonly byte[] data;
            private readonly int start;
            private readonly int end;
            public long Position;

            public BackwardBits(byte[] data, int start, int end)
            {
                Require(end > start, "empty bitstream");
                byte last = data[end - 1];
                Require(last != 0, "bitstream has no end marker");
                this.data = data;
                this.start = start;
                this.end = end;
                Position = (long)(end - start - 1) * 8 + HighBit(last);
            }

            public long Read(int n)
            {
                Position -= n;
                return GetBits(data, start, end, Position, n);
            }

            public long Peek(int n)
            {
                return GetBits(data, start, end, Position - n, n);
            }

            public void Skip(int n)
            {
                Position -= n;
            }
        }

        /// <summary>Bits [pos, pos + n) of data[start..end) as a little-endian bit string (n ≤ 32).</summary>
        private static long GetBits(byte[] data, int start, int end, long pos, int n)
        {
            if (n == 0)
            {
                return 0;
            }

            if (pos < 0)
            {
                if (n + pos <= 0)
                {
                    return 0;
                }

                return GetBits(data, start, end, 0, (int)(n + pos)) << (int)-pos;
            }

            long index = start + (pos >> 3);
            ulong v = 0;
            for (int k = 0; k < 5; k++)
            {
                long i = index + k;
                if (i < end)
                {
                    v |= (ulong)data[i] << (8 * k);
                }
            }

            return (long)((v >> (int)(pos & 7)) & ((1UL << n) - 1));
        }

        private static int HighBit(long v)
        {
            int r = -1;
            while (v > 0)
            {
                v >>= 1;
                r++;
            }

            return r;
        }

        private static uint ReadU32(byte[] data, int pos)
        {
            return (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16)) | ((uint)data[pos + 3] << 24);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new FormatException("zstd: " + message);
            }
        }

        // ─── Output buffer ───────────────────────────────────────────────────────

        private sealed class Output
        {
            private byte[] buffer;
            private readonly long max;
            public int Length;

            public Output(int capacity, long max)
            {
                buffer = new byte[capacity];
                this.max = max;
            }

            private void Ensure(int extra)
            {
                long needed = (long)Length + extra;
                Require(needed <= max && needed <= int.MaxValue - 64, "decompressed data too large");
                if (needed <= buffer.Length)
                {
                    return;
                }

                long size = Math.Max(needed, Math.Min((long)buffer.Length * 2, int.MaxValue - 64));
                Array.Resize(ref buffer, (int)size);
            }

            public void Append(byte[] source, int offset, int count)
            {
                if (count <= 0)
                {
                    return;
                }

                Ensure(count);
                Buffer.BlockCopy(source, offset, buffer, Length, count);
                Length += count;
            }

            public void AppendRepeat(byte value, int count)
            {
                Ensure(count);
                for (int i = 0; i < count; i++)
                {
                    buffer[Length + i] = value;
                }

                Length += count;
            }

            public void CopyMatch(int offset, int length)
            {
                Ensure(length);
                int src = Length - offset;
                if (offset >= length)
                {
                    Buffer.BlockCopy(buffer, src, buffer, Length, length);
                }
                else
                {
                    // Overlapping copy: each byte may come from what this match just wrote.
                    for (int i = 0; i < length; i++)
                    {
                        buffer[Length + i] = buffer[src + i];
                    }
                }

                Length += length;
            }

            public byte[] ToArray()
            {
                var result = new byte[Length];
                Buffer.BlockCopy(buffer, 0, result, 0, Length);
                return result;
            }
        }
    }
}
