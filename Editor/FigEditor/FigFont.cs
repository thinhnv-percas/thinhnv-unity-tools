using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// One face of an installed font file (TrueType or OpenType/CFF, also inside .ttc collections):
    /// its names and metrics, character map, advances, kerning (the <c>kern</c> table, or GPOS pair
    /// adjustments of the <c>kern</c> feature) and glyph outlines in font units (y up).
    /// Thread-safe once loaded; outlines are cached.
    /// </summary>
    public sealed class FigFontFace
    {
        public string Path;
        public int Index;
        public string Family;
        public string Style;
        public string PostScript;
        public int Weight = 400;
        public bool Italic;
        public bool Variable;

        public int UnitsPerEm = 1000;

        /// <summary>Vertical metrics in font units (descent positive, below the baseline).</summary>
        public float Ascent, Descent, LineGap;

        public float UnderlinePosition, UnderlineThickness, StrikePosition, StrikeThickness;

        private byte[] data;
        private readonly Dictionary<string, int[]> tables = new();
        private int numGlyphs;
        private int numHMetrics;
        private bool longLoca;
        private Dictionary<int, int> cmap;
        private readonly Dictionary<int, FigPath> outlines = new();
        private readonly Dictionary<uint, float> kernCache = new();
        private Dictionary<uint, short> kernPairs;
        private List<int> gposPairTables;
        private Cff cff;
        private readonly object sync = new();

        public override string ToString() => Family + " " + Style;

        // ─── Reading ─────────────────────────────────────────────────────────────

        /// <summary>The faces of a font file (one, or several for a collection), with names and metrics only.</summary>
        public static List<FigFontFace> ReadFile(string path, byte[] bytes)
        {
            var faces = new List<FigFontFace>();
            if (bytes == null || bytes.Length < 12)
            {
                return faces;
            }

            if (Tag(bytes, 0) == "ttcf")
            {
                int count = (int)U32(bytes, 8);
                for (int i = 0; i < count && i < 256; i++)
                {
                    FigFontFace f = Parse(path, bytes, (int)U32(bytes, 12 + i * 4), i);
                    if (f != null)
                    {
                        faces.Add(f);
                    }
                }
            }
            else
            {
                FigFontFace f = Parse(path, bytes, 0, 0);
                if (f != null)
                {
                    faces.Add(f);
                }
            }

            return faces;
        }

        private static readonly string[] IndexTables = { "head", "hhea", "maxp", "OS/2", "post", "name" };

        /// <summary>
        /// The faces of a font file for the index, reading only the table directory and the small
        /// tables with names and metrics (system font folders can hold a gigabyte of fonts).
        /// </summary>
        public static List<FigFontFace> ReadIndex(string path)
        {
            var faces = new List<FigFontFace>();
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] head = Read(fs, 0, 12);
                if (head == null)
                {
                    return faces;
                }

                var offsets = new List<int>();
                if (Tag(head, 0) == "ttcf")
                {
                    int count = (int)Math.Min(256, U32(head, 8));
                    byte[] list = Read(fs, 12, count * 4);
                    for (int i = 0; list != null && i < count; i++)
                    {
                        offsets.Add((int)U32(list, i * 4));
                    }
                }
                else
                {
                    offsets.Add(0);
                }

                for (int i = 0; i < offsets.Count; i++)
                {
                    byte[] dirHead = Read(fs, offsets[i], 12);
                    if (dirHead == null)
                    {
                        continue;
                    }

                    int n = U16(dirHead, 4);
                    byte[] dir = Read(fs, offsets[i] + 12, n * 16);
                    if (dir == null)
                    {
                        continue;
                    }

                    var face = new FigFontFace { Path = path, Index = i };
                    var compact = new MemoryStream();
                    for (int t = 0; t < n; t++)
                    {
                        string tag = Tag(dir, t * 16);
                        int off = (int)U32(dir, t * 16 + 8), len = (int)U32(dir, t * 16 + 12);
                        if (Array.IndexOf(IndexTables, tag) >= 0 && len > 0 && len < (16 << 20))
                        {
                            byte[] table = Read(fs, off, len);
                            if (table == null)
                            {
                                continue;
                            }

                            face.tables[tag] = new[] { (int)compact.Length, len };
                            compact.Write(table, 0, table.Length);
                        }
                        else
                        {
                            // Present, not read: enough to know what kind of font it is.
                            face.tables[tag] = new[] { -1, 0 };
                        }
                    }

                    if (!face.tables.ContainsKey("head") || face.tables["head"][0] < 0 || !face.tables.ContainsKey("cmap") ||
                        !(face.tables.ContainsKey("glyf") || face.tables.ContainsKey("CFF ")))
                    {
                        continue;
                    }

                    face.data = compact.ToArray();
                    face.ReadNamesAndMetrics();
                    face.data = null;
                    faces.Add(face);
                }
            }
            catch (Exception)
            {
                // Unreadable or damaged: skipped.
            }

            return faces;
        }

        private static byte[] Read(FileStream fs, long offset, int length)
        {
            if (offset < 0 || length < 0 || offset + length > fs.Length)
            {
                return null;
            }

            var buffer = new byte[length];
            fs.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < length)
            {
                int r = fs.Read(buffer, read, length - read);
                if (r <= 0)
                {
                    return null;
                }

                read += r;
            }

            return buffer;
        }

        private static FigFontFace Parse(string path, byte[] b, int offset, int index)
        {
            try
            {
                string version = Tag(b, offset);
                if (version != "\0\u0001\0\0" && version != "OTTO" && version != "true")
                {
                    return null;
                }

                var face = new FigFontFace { Path = path, Index = index, data = b };
                int n = U16(b, offset + 4);
                for (int i = 0; i < n; i++)
                {
                    int rec = offset + 12 + i * 16;
                    face.tables[Tag(b, rec)] = new[] { (int)U32(b, rec + 8), (int)U32(b, rec + 12) };
                }

                if (!face.tables.ContainsKey("head") || !face.tables.ContainsKey("cmap") || !(face.tables.ContainsKey("glyf") || face.tables.ContainsKey("CFF ")))
                {
                    return null;
                }

                face.ReadNamesAndMetrics();
                return face;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private int Table(string tag)
        {
            return tables.TryGetValue(tag, out int[] t) ? t[0] : -1;
        }

        private bool HasTable(string tag) => tables.ContainsKey(tag);

        private void ReadNamesAndMetrics()
        {
            byte[] b = data;
            int head = Table("head");
            UnitsPerEm = Math.Max(16, (int)U16(b, head + 18));
            int macStyle = U16(b, head + 44);
            longLoca = S16(b, head + 50) != 0;

            int maxp = Table("maxp");
            numGlyphs = maxp >= 0 ? U16(b, maxp + 4) : 0;

            int hhea = Table("hhea");
            float hAsc = 0, hDesc = 0, hGap = 0;
            if (hhea >= 0)
            {
                hAsc = S16(b, hhea + 4);
                hDesc = -S16(b, hhea + 6);
                hGap = S16(b, hhea + 8);
                numHMetrics = U16(b, hhea + 34);
            }

            int os2 = Table("OS/2");
            Ascent = hAsc;
            Descent = hDesc;
            LineGap = hGap;
            StrikeThickness = UnitsPerEm * 0.05f;
            StrikePosition = UnitsPerEm * 0.25f;
            if (os2 >= 0 && tables["OS/2"][1] >= 78)
            {
                Weight = U16(b, os2 + 4);
                int fsSelection = U16(b, os2 + 62);
                Italic = (fsSelection & 1) != 0 || (fsSelection & 0x200) != 0;
                StrikeThickness = S16(b, os2 + 26);
                StrikePosition = S16(b, os2 + 28);
                // Like the shaping libraries: typographic metrics when the font asks for them,
                // else the hhea ones, else the Windows ones.
                bool useTypo = (fsSelection & 0x80) != 0;
                if (useTypo || (hAsc == 0 && hDesc == 0))
                {
                    if (useTypo)
                    {
                        Ascent = S16(b, os2 + 68);
                        Descent = -S16(b, os2 + 70);
                        LineGap = S16(b, os2 + 72);
                    }
                    else
                    {
                        Ascent = U16(b, os2 + 74);
                        Descent = U16(b, os2 + 76);
                        LineGap = 0;
                    }
                }
            }
            else
            {
                Italic = (macStyle & 2) != 0;
                Weight = (macStyle & 1) != 0 ? 700 : 400;
            }

            int post = Table("post");
            UnderlinePosition = post >= 0 ? S16(b, post + 8) : -UnitsPerEm * 0.1f;
            UnderlineThickness = post >= 0 ? Math.Max(1, (int)S16(b, post + 10)) : UnitsPerEm * 0.05f;
            Variable = HasTable("fvar");

            string family = null, subfamily = null, typoFamily = null, typoSubfamily = null;
            int name = Table("name");
            if (name >= 0)
            {
                int count = U16(b, name + 2), strings = name + U16(b, name + 4);
                int[] best = new int[26];
                for (int i = 0; i < count; i++)
                {
                    int rec = name + 6 + i * 12;
                    int platform = U16(b, rec), encoding = U16(b, rec + 2), language = U16(b, rec + 4), id = U16(b, rec + 6);
                    if (id >= best.Length)
                    {
                        continue;
                    }

                    // Windows English first, then any Windows / Unicode, then Mac Roman.
                    int rank = platform == 3 && language == 0x409 ? 3 : platform == 3 || platform == 0 ? 2 : platform == 1 && encoding == 0 ? 1 : 0;
                    if (rank <= best[id])
                    {
                        continue;
                    }

                    int length = U16(b, rec + 8), at = strings + U16(b, rec + 10);
                    if (at + length > b.Length)
                    {
                        continue;
                    }

                    string value = platform == 1 ? Latin1(b, at, length) : Encoding.BigEndianUnicode.GetString(b, at, length);
                    best[id] = rank;
                    switch (id)
                    {
                        case 1:
                            family = value;
                            break;
                        case 2:
                            subfamily = value;
                            break;
                        case 6:
                            PostScript = value;
                            break;
                        case 16:
                            typoFamily = value;
                            break;
                        case 17:
                            typoSubfamily = value;
                            break;
                    }
                }
            }

            Family = typoFamily ?? family ?? System.IO.Path.GetFileNameWithoutExtension(Path);
            Style = typoSubfamily ?? subfamily ?? "Regular";
            LegacyFamily = family;
        }

        /// <summary>The name ID 1 family ("Inter SemiBold" for some fonts), matched as well.</summary>
        public string LegacyFamily;

        /// <summary>Drop the file bytes of a face read only for its names (the index keeps the rest).</summary>
        public void ReleaseData()
        {
            data = null;
        }

        /// <summary>Read the file again for drawing (a face from the index has names only).</summary>
        public bool EnsureData(Func<string, byte[]> read)
        {
            lock (sync)
            {
                if (data == null)
                {
                    byte[] bytes = read(Path);
                    if (bytes == null)
                    {
                        return false;
                    }

                    data = bytes;
                    int offset = Tag(bytes, 0) == "ttcf" ? (int)U32(bytes, 12 + Index * 4) : 0;
                    tables.Clear();
                    int n = U16(bytes, offset + 4);
                    for (int i = 0; i < n; i++)
                    {
                        int rec = offset + 12 + i * 16;
                        tables[Tag(bytes, rec)] = new[] { (int)U32(bytes, rec + 8), (int)U32(bytes, rec + 12) };
                    }

                    ReadNamesAndMetrics();
                }

                if (cmap == null)
                {
                    cmap = ReadCmap();
                    if (tables.ContainsKey("CFF "))
                    {
                        try
                        {
                            cff = new Cff(data, Table("CFF "));
                        }
                        catch (Exception)
                        {
                            cff = null;
                        }
                    }
                }

                return true;
            }
        }

        // ─── Character map and metrics ───────────────────────────────────────────

        public int GlyphIndex(int codepoint)
        {
            return cmap != null && cmap.TryGetValue(codepoint, out int g) ? g : 0;
        }

        public bool HasGlyph(int codepoint)
        {
            return GlyphIndex(codepoint) != 0;
        }

        /// <summary>Advance width in font units.</summary>
        public float Advance(int glyph)
        {
            int hmtx = Table("hmtx");
            if (hmtx < 0 || numHMetrics == 0)
            {
                return UnitsPerEm * 0.5f;
            }

            int i = Math.Min(glyph, numHMetrics - 1);
            return U16(data, hmtx + i * 4);
        }

        private Dictionary<int, int> ReadCmap()
        {
            var map = new Dictionary<int, int>();
            byte[] b = data;
            int cmapOff = Table("cmap");
            int count = U16(b, cmapOff + 2);
            int best = -1, bestRank = -1;
            bool symbol = false;
            for (int i = 0; i < count; i++)
            {
                int rec = cmapOff + 4 + i * 8;
                int platform = U16(b, rec), encoding = U16(b, rec + 2);
                int sub = cmapOff + (int)U32(b, rec + 4);
                int format = U16(b, sub);
                int rank = platform == 3 && encoding == 10 && format == 12 ? 6 :
                    platform == 0 && format == 12 ? 5 :
                    platform == 3 && encoding == 1 && format == 4 ? 4 :
                    platform == 0 && format == 4 ? 3 :
                    platform == 3 && encoding == 0 && format == 4 ? 2 :
                    format == 6 || format == 0 ? 1 : -1;
                if (rank > bestRank)
                {
                    bestRank = rank;
                    best = sub;
                    symbol = platform == 3 && encoding == 0;
                }
            }

            if (best < 0)
            {
                return map;
            }

            switch (U16(b, best))
            {
                case 0:
                    for (int c = 0; c < 256; c++)
                    {
                        if (b[best + 6 + c] != 0)
                        {
                            map[c] = b[best + 6 + c];
                        }
                    }

                    break;
                case 4:
                {
                    int segX2 = U16(b, best + 6);
                    int ends = best + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
                    for (int s = 0; s < segX2 / 2; s++)
                    {
                        int end = U16(b, ends + s * 2), start = U16(b, starts + s * 2);
                        int delta = S16(b, deltas + s * 2), rangeAt = ranges + s * 2, range = U16(b, rangeAt);
                        if (start == 0xFFFF)
                        {
                            continue;
                        }

                        for (int c = start; c <= end; c++)
                        {
                            int g;
                            if (range == 0)
                            {
                                g = (c + delta) & 0xFFFF;
                            }
                            else
                            {
                                int at = rangeAt + range + (c - start) * 2;
                                g = at + 1 < b.Length ? U16(b, at) : 0;
                                if (g != 0)
                                {
                                    g = (g + delta) & 0xFFFF;
                                }
                            }

                            if (g != 0)
                            {
                                map[c] = g;
                                if (symbol && c >= 0xF000 && c <= 0xF0FF && !map.ContainsKey(c - 0xF000))
                                {
                                    // Symbol fonts map their glyphs at U+F0xx.
                                    map[c - 0xF000] = g;
                                }
                            }
                        }
                    }

                    break;
                }
                case 6:
                {
                    int first = U16(b, best + 6), n = U16(b, best + 8);
                    for (int i = 0; i < n; i++)
                    {
                        int g = U16(b, best + 10 + i * 2);
                        if (g != 0)
                        {
                            map[first + i] = g;
                        }
                    }

                    break;
                }
                case 12:
                {
                    uint groups = U32(b, best + 12);
                    for (uint i = 0; i < groups && i < 200000; i++)
                    {
                        int rec = best + 16 + (int)i * 12;
                        uint start = U32(b, rec), end = U32(b, rec + 4), glyph = U32(b, rec + 8);
                        for (uint c = start; c <= end && c <= 0x10FFFF && end - start < 0x20000; c++)
                        {
                            map[(int)c] = (int)(glyph + c - start);
                        }
                    }

                    break;
                }
            }

            return map;
        }

        // ─── Kerning ─────────────────────────────────────────────────────────────

        /// <summary>The kerning between two glyphs in font units (GPOS <c>kern</c> feature, else the kern table).</summary>
        public float Kerning(int left, int right)
        {
            if (left == 0 || right == 0)
            {
                return 0;
            }

            uint key = (uint)left << 16 | (uint)(right & 0xFFFF);
            lock (sync)
            {
                if (kernCache.TryGetValue(key, out float k))
                {
                    return k;
                }

                if (gposPairTables == null && kernPairs == null)
                {
                    ReadKerning();
                }

                k = 0;
                if (gposPairTables != null && gposPairTables.Count > 0)
                {
                    foreach (int sub in gposPairTables)
                    {
                        if (PairAdjustment(sub, left, right, out float adjust))
                        {
                            k = adjust;
                            break;
                        }
                    }
                }
                else if (kernPairs != null && kernPairs.TryGetValue(key, out short pair))
                {
                    k = pair;
                }

                if (kernCache.Count > 20000)
                {
                    kernCache.Clear();
                }

                kernCache[key] = k;
                return k;
            }
        }

        private void ReadKerning()
        {
            gposPairTables = new List<int>();
            kernPairs = new Dictionary<uint, short>();
            byte[] b = data;
            try
            {
                int gpos = Table("GPOS");
                if (gpos >= 0)
                {
                    int features = gpos + U16(b, gpos + 6), lookups = gpos + U16(b, gpos + 8);
                    var lookupIndices = new SortedSet<int>();
                    int fc = U16(b, features);
                    for (int i = 0; i < fc; i++)
                    {
                        int rec = features + 2 + i * 6;
                        if (Tag(b, rec) != "kern")
                        {
                            continue;
                        }

                        int feature = features + U16(b, rec + 4);
                        int lc = U16(b, feature + 2);
                        for (int j = 0; j < lc; j++)
                        {
                            lookupIndices.Add(U16(b, feature + 4 + j * 2));
                        }
                    }

                    foreach (int li in lookupIndices)
                    {
                        if (li >= U16(b, lookups))
                        {
                            continue;
                        }

                        int lookup = lookups + U16(b, lookups + 2 + li * 2);
                        int type = U16(b, lookup), subCount = U16(b, lookup + 4);
                        for (int s = 0; s < subCount; s++)
                        {
                            int sub = lookup + U16(b, lookup + 6 + s * 2);
                            int subType = type;
                            if (type == 9)
                            {
                                subType = U16(b, sub + 2);
                                sub += (int)U32(b, sub + 4);
                            }

                            if (subType == 2)
                            {
                                gposPairTables.Add(sub);
                            }
                        }
                    }
                }

                int kern = Table("kern");
                if (gposPairTables.Count == 0 && kern >= 0 && U16(b, kern) == 0)
                {
                    int n = U16(b, kern + 2), at = kern + 4;
                    for (int t = 0; t < n; t++)
                    {
                        int length = U16(b, at + 2), coverage = U16(b, at + 4);
                        if ((coverage >> 8) == 0 && (coverage & 1) != 0)
                        {
                            int pairs = U16(b, at + 6);
                            for (int p = 0; p < pairs; p++)
                            {
                                int rec = at + 14 + p * 6;
                                kernPairs[(uint)U16(b, rec) << 16 | (uint)U16(b, rec + 2)] = S16(b, rec + 4);
                            }
                        }

                        at += length;
                    }
                }
            }
            catch (Exception)
            {
                // A broken kerning table: no kerning.
            }
        }

        private bool PairAdjustment(int sub, int left, int right, out float value)
        {
            byte[] b = data;
            value = 0;
            int format = U16(b, sub);
            int coverageIndex = Coverage(sub + U16(b, sub + 2), left);
            if (coverageIndex < 0)
            {
                return false;
            }

            int vf1 = U16(b, sub + 4), vf2 = U16(b, sub + 6);
            int size1 = ValueSize(vf1), size2 = ValueSize(vf2);
            if (format == 1)
            {
                int setCount = U16(b, sub + 8);
                if (coverageIndex >= setCount)
                {
                    return false;
                }

                int set = sub + U16(b, sub + 10 + coverageIndex * 2);
                int count = U16(b, set), recSize = 2 + size1 + size2;
                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    int rec = set + 2 + mid * recSize;
                    int second = U16(b, rec);
                    if (second == right)
                    {
                        value = XAdvance(rec + 2, vf1);
                        return true;
                    }

                    if (second < right)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }

                return false;
            }

            if (format == 2)
            {
                int c1 = ClassOf(sub + U16(b, sub + 8), left), c2 = ClassOf(sub + U16(b, sub + 10), right);
                int class1Count = U16(b, sub + 12), class2Count = U16(b, sub + 14);
                if (c1 >= class1Count || c2 >= class2Count)
                {
                    return false;
                }

                int rec = sub + 16 + (c1 * class2Count + c2) * (size1 + size2);
                value = XAdvance(rec, vf1);
                // A class pair is matched once the first glyph is covered, even with no adjustment.
                return true;
            }

            return false;
        }

        private static int ValueSize(int format)
        {
            int n = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((format & (1 << bit)) != 0)
                {
                    n++;
                }
            }

            return n * 2;
        }

        private float XAdvance(int record, int format)
        {
            if ((format & 4) == 0)
            {
                return 0;
            }

            int skip = ((format & 1) != 0 ? 2 : 0) + ((format & 2) != 0 ? 2 : 0);
            return S16(data, record + skip);
        }

        private int Coverage(int table, int glyph)
        {
            byte[] b = data;
            int format = U16(b, table), count = U16(b, table + 2);
            if (format == 1)
            {
                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2, g = U16(b, table + 4 + mid * 2);
                    if (g == glyph)
                    {
                        return mid;
                    }

                    if (g < glyph)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }
            }
            else if (format == 2)
            {
                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2, rec = table + 4 + mid * 6;
                    int start = U16(b, rec), end = U16(b, rec + 2);
                    if (glyph < start)
                    {
                        hi = mid - 1;
                    }
                    else if (glyph > end)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        return U16(b, rec + 4) + glyph - start;
                    }
                }
            }

            return -1;
        }

        private int ClassOf(int table, int glyph)
        {
            byte[] b = data;
            int format = U16(b, table);
            if (format == 1)
            {
                int start = U16(b, table + 2), count = U16(b, table + 4);
                return glyph >= start && glyph < start + count ? U16(b, table + 6 + (glyph - start) * 2) : 0;
            }

            if (format == 2)
            {
                int count = U16(b, table + 2), lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2, rec = table + 4 + mid * 6;
                    int start = U16(b, rec), end = U16(b, rec + 2);
                    if (glyph < start)
                    {
                        hi = mid - 1;
                    }
                    else if (glyph > end)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        return U16(b, rec + 4);
                    }
                }
            }

            return 0;
        }

        // ─── Outlines ────────────────────────────────────────────────────────────

        /// <summary>The glyph's outline in font units (y up), or an empty path.</summary>
        public FigPath Outline(int glyph)
        {
            lock (sync)
            {
                if (outlines.TryGetValue(glyph, out FigPath cached))
                {
                    return cached;
                }

                var builder = new FigPath.Builder();
                try
                {
                    if (cff != null)
                    {
                        cff.Glyph(glyph, builder);
                    }
                    else
                    {
                        Glyf(glyph, builder, FigAffine.Identity, 0);
                    }
                }
                catch (Exception)
                {
                    builder = new FigPath.Builder();
                }

                FigPath path = builder.Build(false);
                outlines[glyph] = path;
                return path;
            }
        }

        private void Glyf(int glyph, FigPath.Builder builder, FigAffine t, int depth)
        {
            byte[] b = data;
            int loca = Table("loca"), glyf = Table("glyf");
            if (glyph < 0 || glyph >= numGlyphs || loca < 0 || glyf < 0 || depth > 8)
            {
                return;
            }

            int start = longLoca ? (int)U32(b, loca + glyph * 4) : U16(b, loca + glyph * 2) * 2;
            int end = longLoca ? (int)U32(b, loca + glyph * 4 + 4) : U16(b, loca + glyph * 2 + 2) * 2;
            if (end <= start)
            {
                return;
            }

            int at = glyf + start;
            int contours = S16(b, at);
            if (contours >= 0)
            {
                SimpleGlyph(at, contours, builder, t);
                return;
            }

            // Composite: components placed by offsets and an optional scale or 2×2 matrix.
            int p = at + 10;
            while (true)
            {
                int flags = U16(b, p), component = U16(b, p + 2);
                p += 4;
                float dx, dy;
                if ((flags & 1) != 0)
                {
                    dx = (flags & 2) != 0 ? S16(b, p) : U16(b, p);
                    dy = (flags & 2) != 0 ? S16(b, p + 2) : U16(b, p + 2);
                    p += 4;
                }
                else
                {
                    dx = (flags & 2) != 0 ? (int)(sbyte)b[p] : b[p];
                    dy = (flags & 2) != 0 ? (int)(sbyte)b[p + 1] : b[p + 1];
                    p += 2;
                }

                if ((flags & 2) == 0)
                {
                    // Point-matched placement: rare, placed at the origin.
                    dx = dy = 0;
                }

                float a = 1, bb = 0, c = 0, d = 1;
                if ((flags & 8) != 0)
                {
                    a = d = F2Dot14(b, p);
                    p += 2;
                }
                else if ((flags & 0x40) != 0)
                {
                    a = F2Dot14(b, p);
                    d = F2Dot14(b, p + 2);
                    p += 4;
                }
                else if ((flags & 0x80) != 0)
                {
                    a = F2Dot14(b, p);
                    bb = F2Dot14(b, p + 2);
                    c = F2Dot14(b, p + 4);
                    d = F2Dot14(b, p + 6);
                    p += 8;
                }

                var local = new FigAffine { A = a, B = c, C = dx, D = bb, E = d, F = dy };
                Glyf(component, builder, t.Then(local), depth + 1);
                if ((flags & 0x20) == 0)
                {
                    break;
                }
            }
        }

        private void SimpleGlyph(int at, int contours, FigPath.Builder builder, FigAffine t)
        {
            byte[] b = data;
            var ends = new int[contours];
            int p = at + 10;
            for (int i = 0; i < contours; i++)
            {
                ends[i] = U16(b, p);
                p += 2;
            }

            int count = contours > 0 ? ends[contours - 1] + 1 : 0;
            p += 2 + U16(b, p);
            var flags = new byte[count];
            for (int i = 0; i < count;)
            {
                byte f = b[p++];
                flags[i++] = f;
                if ((f & 8) != 0)
                {
                    int repeat = b[p++];
                    for (int r = 0; r < repeat && i < count; r++)
                    {
                        flags[i++] = f;
                    }
                }
            }

            var xs = new float[count];
            var ys = new float[count];
            int v = 0;
            for (int i = 0; i < count; i++)
            {
                byte f = flags[i];
                if ((f & 2) != 0)
                {
                    v += (f & 0x10) != 0 ? b[p] : -b[p];
                    p++;
                }
                else if ((f & 0x10) == 0)
                {
                    v += S16(b, p);
                    p += 2;
                }

                xs[i] = v;
            }

            v = 0;
            for (int i = 0; i < count; i++)
            {
                byte f = flags[i];
                if ((f & 4) != 0)
                {
                    v += (f & 0x20) != 0 ? b[p] : -b[p];
                    p++;
                }
                else if ((f & 0x20) == 0)
                {
                    v += S16(b, p);
                    p += 2;
                }

                ys[i] = v;
            }

            int first = 0;
            for (int c = 0; c < contours; c++)
            {
                int last = ends[c];
                int n = last - first + 1;
                if (n >= 2)
                {
                    Contour(xs, ys, flags, first, n, builder, t);
                }

                first = last + 1;
            }
        }

        /// <summary>One quadratic contour: on-curve points joined by lines or curves through the off-curve ones.</summary>
        private static void Contour(float[] xs, float[] ys, byte[] flags, int first, int n, FigPath.Builder builder, FigAffine t)
        {
            var pts = new Vector2[n];
            var on = new bool[n];
            int start = -1;
            for (int k = 0; k < n; k++)
            {
                pts[k] = t.Apply(xs[first + k], ys[first + k]);
                on[k] = (flags[first + k] & 1) != 0;
                if (on[k] && start < 0)
                {
                    start = k;
                }
            }

            // Start on an on-curve point, or between the last and first off-curve points.
            Vector2 begin = start >= 0 ? pts[start] : (pts[n - 1] + pts[0]) * 0.5f;
            builder.MoveTo(begin.x, begin.y);
            Vector2 control = default;
            bool hasControl = false;
            int count = start >= 0 ? n - 1 : n;
            for (int i = 0; i <= count; i++)
            {
                Vector2 p;
                bool isOn;
                if (i == count)
                {
                    p = begin;
                    isOn = true;
                }
                else
                {
                    int k = start >= 0 ? (start + 1 + i) % n : i;
                    p = pts[k];
                    isOn = on[k];
                }

                if (isOn)
                {
                    if (hasControl)
                    {
                        builder.QuadTo(control.x, control.y, p.x, p.y);
                        hasControl = false;
                    }
                    else
                    {
                        builder.LineTo(p.x, p.y);
                    }
                }
                else
                {
                    if (hasControl)
                    {
                        Vector2 mid = (control + p) * 0.5f;
                        builder.QuadTo(control.x, control.y, mid.x, mid.y);
                    }

                    control = p;
                    hasControl = true;
                }
            }

            builder.Close();
        }

        // ─── CFF ─────────────────────────────────────────────────────────────────

        /// <summary>The CFF (Type 2 charstring) outlines of an OpenType font.</summary>
        private sealed class Cff
        {
            private readonly byte[] b;
            private readonly int[] charStrings;
            private readonly int[] globalSubrs;
            private readonly int[] localSubrs;
            private readonly int[][] fdSubrs;
            private readonly byte[] fdSelect;

            public Cff(byte[] data, int start)
            {
                b = data;
                int hdrSize = data[start + 2];
                int p = start + hdrSize;
                p = ReadIndex(p, out int[] names);
                p = ReadIndex(p, out int[] topDicts);
                p = ReadIndex(p, out int[] strings);
                ReadIndex(p, out globalSubrs);
                Dictionary<int, List<float>> top = Dict(topDicts[0], topDicts[1]);
                if (!top.TryGetValue(17, out List<float> cs))
                {
                    throw new FormatException("CFF without charstrings");
                }

                ReadIndex(start + (int)cs[0], out charStrings);
                if (top.TryGetValue(18, out List<float> priv) && priv.Count >= 2)
                {
                    localSubrs = PrivateSubrs(start, (int)priv[0], (int)priv[1]);
                }

                if (top.TryGetValue(1236, out List<float> fdArray) && top.TryGetValue(1237, out List<float> select))
                {
                    ReadIndex(start + (int)fdArray[0], out int[] fds);
                    fdSubrs = new int[Math.Max(0, fds.Length - 1)][];
                    for (int i = 0; i + 1 < fds.Length; i++)
                    {
                        Dictionary<int, List<float>> fd = Dict(fds[i], fds[i + 1]);
                        fdSubrs[i] = fd.TryGetValue(18, out List<float> fp) && fp.Count >= 2 ? PrivateSubrs(start, (int)fp[0], (int)fp[1]) : Array.Empty<int>();
                    }

                    int glyphs = charStrings.Length - 1;
                    fdSelect = new byte[Math.Max(0, glyphs)];
                    int s = start + (int)select[0];
                    if (data[s] == 0)
                    {
                        Array.Copy(data, s + 1, fdSelect, 0, glyphs);
                    }
                    else if (data[s] == 3)
                    {
                        int ranges = U16(data, s + 1);
                        for (int r = 0; r < ranges; r++)
                        {
                            int first = U16(data, s + 3 + r * 3), fdi = data[s + 5 + r * 3], next = U16(data, s + 6 + r * 3);
                            for (int g = first; g < next && g < glyphs; g++)
                            {
                                fdSelect[g] = (byte)fdi;
                            }
                        }
                    }
                }

                GC.KeepAlive(names);
                GC.KeepAlive(strings);
            }

            private int[] PrivateSubrs(int cffStart, int size, int offset)
            {
                int privStart = cffStart + offset;
                Dictionary<int, List<float>> priv = Dict(privStart, privStart + size);
                if (priv.TryGetValue(19, out List<float> subrs) && subrs.Count > 0)
                {
                    ReadIndex(privStart + (int)subrs[0], out int[] index);
                    return index;
                }

                return Array.Empty<int>();
            }

            /// <summary>An INDEX at <paramref name="p"/>: its element boundaries (count + 1 absolute offsets); returns the end.</summary>
            private int ReadIndex(int p, out int[] bounds)
            {
                int count = U16(b, p);
                if (count == 0)
                {
                    bounds = new int[0];
                    return p + 2;
                }

                int offSize = b[p + 2];
                int offsets = p + 3;
                int dataStart = offsets + (count + 1) * offSize - 1;
                bounds = new int[count + 1];
                for (int i = 0; i <= count; i++)
                {
                    int v = 0;
                    for (int k = 0; k < offSize; k++)
                    {
                        v = v << 8 | b[offsets + i * offSize + k];
                    }

                    bounds[i] = dataStart + v;
                }

                return bounds[count];
            }

            private Dictionary<int, List<float>> Dict(int start, int end)
            {
                var dict = new Dictionary<int, List<float>>();
                var operands = new List<float>();
                int p = start;
                while (p < end)
                {
                    int b0 = b[p];
                    if (b0 <= 21)
                    {
                        int op = b0;
                        p++;
                        if (b0 == 12)
                        {
                            op = 1200 + b[p++];
                        }

                        dict[op] = operands;
                        operands = new List<float>();
                    }
                    else if (b0 == 28)
                    {
                        operands.Add((short)(b[p + 1] << 8 | b[p + 2]));
                        p += 3;
                    }
                    else if (b0 == 29)
                    {
                        operands.Add(b[p + 1] << 24 | b[p + 2] << 16 | b[p + 3] << 8 | b[p + 4]);
                        p += 5;
                    }
                    else if (b0 == 30)
                    {
                        // Real number: nibbles until 0xf.
                        p++;
                        var sb = new StringBuilder();
                        bool done = false;
                        while (!done && p < end)
                        {
                            int v = b[p++];
                            foreach (int nib in new[] { v >> 4, v & 15 })
                            {
                                if (nib <= 9)
                                {
                                    sb.Append((char)('0' + nib));
                                }
                                else if (nib == 0xa)
                                {
                                    sb.Append('.');
                                }
                                else if (nib == 0xb)
                                {
                                    sb.Append('E');
                                }
                                else if (nib == 0xc)
                                {
                                    sb.Append("E-");
                                }
                                else if (nib == 0xe)
                                {
                                    sb.Append('-');
                                }
                                else if (nib == 0xf)
                                {
                                    done = true;
                                    break;
                                }
                            }
                        }

                        float.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float real);
                        operands.Add(real);
                    }
                    else if (b0 >= 32 && b0 <= 246)
                    {
                        operands.Add(b0 - 139);
                        p++;
                    }
                    else if (b0 >= 247 && b0 <= 250)
                    {
                        operands.Add((b0 - 247) * 256 + b[p + 1] + 108);
                        p += 2;
                    }
                    else if (b0 >= 251 && b0 <= 254)
                    {
                        operands.Add(-(b0 - 251) * 256 - b[p + 1] - 108);
                        p += 2;
                    }
                    else
                    {
                        p++;
                    }
                }

                return dict;
            }

            // Charstring interpreter state.
            private readonly float[] stack = new float[64];
            private int sp;
            private int stems;
            private bool widthDone;
            private bool open;
            private float x, y;
            private FigPath.Builder path;
            private int[] subrs;

            public void Glyph(int glyph, FigPath.Builder builder)
            {
                if (glyph < 0 || glyph + 1 >= charStrings.Length)
                {
                    return;
                }

                path = builder;
                sp = 0;
                stems = 0;
                widthDone = false;
                open = false;
                x = y = 0;
                subrs = fdSubrs != null && fdSelect != null && glyph < fdSelect.Length && fdSelect[glyph] < fdSubrs.Length ? fdSubrs[fdSelect[glyph]] : localSubrs ?? Array.Empty<int>();
                Run(charStrings[glyph], charStrings[glyph + 1], 0);
                if (open)
                {
                    path.Close();
                }

                path = null;
            }

            private static int Bias(int[] index)
            {
                int count = Math.Max(0, index.Length - 1);
                return count < 1240 ? 107 : count < 33900 ? 1131 : 32768;
            }

            private void Width(bool odd)
            {
                if (!widthDone)
                {
                    widthDone = true;
                    if (odd && sp > 0)
                    {
                        Array.Copy(stack, 1, stack, 0, sp - 1);
                        sp--;
                    }
                }
            }

            private void MoveTo(float dx, float dy)
            {
                if (open)
                {
                    path.Close();
                }

                x += dx;
                y += dy;
                path.MoveTo(x, y);
                open = true;
            }

            private void LineTo(float dx, float dy)
            {
                x += dx;
                y += dy;
                path.LineTo(x, y);
            }

            private void CurveTo(float dx1, float dy1, float dx2, float dy2, float dx3, float dy3)
            {
                float x1 = x + dx1, y1 = y + dy1, x2 = x1 + dx2, y2 = y1 + dy2;
                x = x2 + dx3;
                y = y2 + dy3;
                path.CubicTo(x1, y1, x2, y2, x, y);
            }

            /// <summary>Runs charstring bytes [p, end); returns false when the glyph ended.</summary>
            private bool Run(int p, int end, int depth)
            {
                if (depth > 10)
                {
                    return false;
                }

                while (p < end)
                {
                    int b0 = b[p++];
                    if (b0 >= 32 || b0 == 28)
                    {
                        float v;
                        if (b0 == 28)
                        {
                            v = (short)(b[p] << 8 | b[p + 1]);
                            p += 2;
                        }
                        else if (b0 <= 246)
                        {
                            v = b0 - 139;
                        }
                        else if (b0 <= 250)
                        {
                            v = (b0 - 247) * 256 + b[p++] + 108;
                        }
                        else if (b0 <= 254)
                        {
                            v = -(b0 - 251) * 256 - b[p++] - 108;
                        }
                        else
                        {
                            v = (b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]) / 65536f;
                            p += 4;
                        }

                        if (sp < stack.Length)
                        {
                            stack[sp++] = v;
                        }

                        continue;
                    }

                    switch (b0)
                    {
                        case 1:
                        case 3:
                        case 18:
                        case 23:
                            Width(sp % 2 == 1);
                            stems += sp / 2;
                            sp = 0;
                            break;
                        case 19:
                        case 20:
                            Width(sp % 2 == 1);
                            stems += sp / 2;
                            sp = 0;
                            p += (stems + 7) / 8;
                            break;
                        case 21:
                            Width(sp > 2);
                            MoveTo(stack[0], stack[1]);
                            sp = 0;
                            break;
                        case 22:
                            Width(sp > 1);
                            MoveTo(stack[0], 0);
                            sp = 0;
                            break;
                        case 4:
                            Width(sp > 1);
                            MoveTo(0, stack[0]);
                            sp = 0;
                            break;
                        case 5:
                            for (int i = 0; i + 1 < sp; i += 2)
                            {
                                LineTo(stack[i], stack[i + 1]);
                            }

                            sp = 0;
                            break;
                        case 6:
                        case 7:
                        {
                            bool horizontal = b0 == 6;
                            for (int i = 0; i < sp; i++, horizontal = !horizontal)
                            {
                                if (horizontal)
                                {
                                    LineTo(stack[i], 0);
                                }
                                else
                                {
                                    LineTo(0, stack[i]);
                                }
                            }

                            sp = 0;
                            break;
                        }
                        case 8:
                            for (int i = 0; i + 5 < sp; i += 6)
                            {
                                CurveTo(stack[i], stack[i + 1], stack[i + 2], stack[i + 3], stack[i + 4], stack[i + 5]);
                            }

                            sp = 0;
                            break;
                        case 24:
                        {
                            int i = 0;
                            for (; i + 7 < sp; i += 6)
                            {
                                CurveTo(stack[i], stack[i + 1], stack[i + 2], stack[i + 3], stack[i + 4], stack[i + 5]);
                            }

                            LineTo(stack[i], stack[i + 1]);
                            sp = 0;
                            break;
                        }
                        case 25:
                        {
                            int i = 0;
                            for (; i + 7 < sp; i += 2)
                            {
                                LineTo(stack[i], stack[i + 1]);
                            }

                            CurveTo(stack[i], stack[i + 1], stack[i + 2], stack[i + 3], stack[i + 4], stack[i + 5]);
                            sp = 0;
                            break;
                        }
                        case 26:
                        {
                            int i = 0;
                            float dx1 = 0;
                            if (sp % 2 == 1)
                            {
                                dx1 = stack[0];
                                i = 1;
                            }

                            for (; i + 3 < sp; i += 4)
                            {
                                CurveTo(dx1, stack[i], stack[i + 1], stack[i + 2], 0, stack[i + 3]);
                                dx1 = 0;
                            }

                            sp = 0;
                            break;
                        }
                        case 27:
                        {
                            int i = 0;
                            float dy1 = 0;
                            if (sp % 2 == 1)
                            {
                                dy1 = stack[0];
                                i = 1;
                            }

                            for (; i + 3 < sp; i += 4)
                            {
                                CurveTo(stack[i], dy1, stack[i + 1], stack[i + 2], stack[i + 3], 0);
                                dy1 = 0;
                            }

                            sp = 0;
                            break;
                        }
                        case 30:
                        case 31:
                        {
                            bool horizontal = b0 == 31;
                            int i = 0;
                            while (i + 3 < sp)
                            {
                                bool last = i + 4 >= sp - 1;
                                float extra = last && sp - i == 5 ? stack[i + 4] : 0;
                                if (horizontal)
                                {
                                    CurveTo(stack[i], 0, stack[i + 1], stack[i + 2], extra, stack[i + 3]);
                                }
                                else
                                {
                                    CurveTo(0, stack[i], stack[i + 1], stack[i + 2], stack[i + 3], extra);
                                }

                                horizontal = !horizontal;
                                i += 4;
                            }

                            sp = 0;
                            break;
                        }
                        case 10:
                        case 29:
                        {
                            if (sp == 0)
                            {
                                break;
                            }

                            int[] index = b0 == 10 ? subrs : globalSubrs;
                            int n = (int)stack[--sp] + Bias(index);
                            if (index != null && n >= 0 && n + 1 < index.Length && !Run(index[n], index[n + 1], depth + 1))
                            {
                                return false;
                            }

                            break;
                        }
                        case 11:
                            return true;
                        case 14:
                            Width(sp == 1 || sp == 5);
                            sp = 0;
                            return false;
                        case 12:
                        {
                            int op = b[p++];
                            switch (op)
                            {
                                case 35:
                                    CurveTo(stack[0], stack[1], stack[2], stack[3], stack[4], stack[5]);
                                    CurveTo(stack[6], stack[7], stack[8], stack[9], stack[10], stack[11]);
                                    break;
                                case 34:
                                    CurveTo(stack[0], 0, stack[1], stack[2], stack[3], 0);
                                    CurveTo(stack[4], 0, stack[5], -stack[2], stack[6], 0);
                                    break;
                                case 36:
                                    CurveTo(stack[0], stack[1], stack[2], stack[3], stack[4], 0);
                                    CurveTo(stack[5], 0, stack[6], stack[7], stack[8], -(stack[1] + stack[3] + stack[7]));
                                    break;
                                case 37:
                                {
                                    float dx = stack[0] + stack[2] + stack[4] + stack[6] + stack[8];
                                    float dy = stack[1] + stack[3] + stack[5] + stack[7] + stack[9];
                                    bool horizontal = Math.Abs(dx) > Math.Abs(dy);
                                    CurveTo(stack[0], stack[1], stack[2], stack[3], stack[4], stack[5]);
                                    CurveTo(stack[6], stack[7], stack[8], stack[9], horizontal ? stack[10] : -dx, horizontal ? -dy : stack[10]);
                                    break;
                                }
                            }

                            sp = 0;
                            break;
                        }
                        default:
                            sp = 0;
                            break;
                    }
                }

                return true;
            }
        }

        // ─── Bytes ───────────────────────────────────────────────────────────────

        private static string Latin1(byte[] b, int at, int length)
        {
            var chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                chars[i] = (char)b[at + i];
            }

            return new string(chars);
        }

        private static int U16(byte[] b, int p) => b[p] << 8 | b[p + 1];

        private static short S16(byte[] b, int p) => (short)(b[p] << 8 | b[p + 1]);

        private static uint U32(byte[] b, int p) => (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]);

        private static float F2Dot14(byte[] b, int p) => S16(b, p) / 16384f;

        private static string Tag(byte[] b, int p) => p + 4 <= b.Length ? Encoding.ASCII.GetString(b, p, 4) : "";
    }

    /// <summary>
    /// The installed fonts: the system font folders and the project's <c>Assets</c>, indexed by
    /// family and style, matched the way Figma names fonts (family, style, PostScript name), with
    /// fallbacks for missing fonts and missing characters.
    /// </summary>
    public static class FigFonts
    {
        private static readonly object sync = new();
        private static List<FigFontFace> faces;
        private static Dictionary<string, List<FigFontFace>> byFamily;
        private static Dictionary<string, FigFontFace> byPostScript;
        private static readonly Dictionary<string, byte[]> fileCache = new();
        private static List<FigFontFace> fallbackChain;

        /// <summary>Extra folders to search (the project's Assets); set from the main thread before the first lookup.</summary>
        public static readonly List<string> ExtraFolders = new();

        /// <summary>Where the index is cached between script reloads (null: not cached).</summary>
        public static string CacheFile;

        private static readonly string[] Fallbacks =
        {
            "Inter", "Roboto", "Segoe UI", "SF Pro Text", "SF Pro", "Helvetica Neue", "Helvetica", "Arial", "Noto Sans", "Liberation Sans",
            "DejaVu Sans", "FreeSans", "Segoe UI Symbol", "Segoe UI Emoji", "Microsoft YaHei", "PingFang SC", "Hiragino Sans", "Noto Sans CJK SC",
            "Noto Sans CJK JP", "Malgun Gothic", "Apple SD Gothic Neo", "WenQuanYi Zen Hei", "Arial Unicode MS", "Tahoma", "Nirmala UI",
        };

        public static int Count
        {
            get
            {
                EnsureIndex();
                return faces.Count;
            }
        }

        /// <summary>Forget the index (fonts were installed); the next lookup scans again.</summary>
        public static void Reset()
        {
            lock (sync)
            {
                faces = null;
                fallbackChain = null;
                fileCache.Clear();
                if (CacheFile != null && File.Exists(CacheFile))
                {
                    File.Delete(CacheFile);
                }
            }
        }

        /// <summary>The installed font family names, sorted.</summary>
        public static List<string> Families()
        {
            EnsureIndex();
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FigFontFace f in faces)
            {
                names.Add(f.Family);
            }

            return new List<string>(names);
        }

        /// <summary>
        /// The face for Figma's font name; <paramref name="exact"/> says whether its family is installed
        /// (else a fallback family of the same weight and slant was picked). Null when no font is installed at all.
        /// </summary>
        public static FigFontFace Find(string family, string style, string postscript, out bool exact)
        {
            EnsureIndex();
            exact = false;
            lock (sync)
            {
                if (!string.IsNullOrEmpty(postscript) && byPostScript.TryGetValue(Key(postscript), out FigFontFace ps) && Ready(ps))
                {
                    exact = true;
                    return ps;
                }

                int weight = StyleWeight(style);
                bool italic = StyleItalic(style);
                if (!string.IsNullOrEmpty(family) && byFamily.TryGetValue(Key(family), out List<FigFontFace> list))
                {
                    FigFontFace best = Best(list, style, weight, italic);
                    if (best != null)
                    {
                        exact = true;
                        return best;
                    }
                }

                foreach (string fb in Fallbacks)
                {
                    if (byFamily.TryGetValue(Key(fb), out List<FigFontFace> l))
                    {
                        FigFontFace best = Best(l, style, weight, italic);
                        if (best != null)
                        {
                            return best;
                        }
                    }
                }

                foreach (FigFontFace f in faces)
                {
                    if (Ready(f))
                    {
                        return f;
                    }
                }

                return null;
            }
        }

        /// <summary>A face that has <paramref name="codepoint"/>, for characters the chosen font lacks.</summary>
        public static FigFontFace FallbackFor(int codepoint, FigFontFace preferred)
        {
            EnsureIndex();
            lock (sync)
            {
                if (fallbackChain == null)
                {
                    fallbackChain = new List<FigFontFace>();
                    foreach (string fb in Fallbacks)
                    {
                        if (byFamily.TryGetValue(Key(fb), out List<FigFontFace> l))
                        {
                            FigFontFace f = Best(l, "Regular", 400, false);
                            if (f != null)
                            {
                                fallbackChain.Add(f);
                            }
                        }
                    }
                }

                foreach (FigFontFace f in fallbackChain)
                {
                    if (f != preferred && f.HasGlyph(codepoint))
                    {
                        return f;
                    }
                }

                return null;
            }
        }

        private static FigFontFace Best(List<FigFontFace> list, string style, int weight, bool italic)
        {
            string styleKey = Key(style ?? "");
            var ranked = new List<KeyValuePair<int, FigFontFace>>();
            foreach (FigFontFace f in list)
            {
                int score = (f.Italic != italic ? 1000 : 0) + Math.Abs(f.Weight - weight) + (f.Variable ? 1 : 0);
                if (styleKey.Length > 0 && Key(f.Style) == styleKey)
                {
                    score -= 10000;
                }

                // Ties keep the folder order.
                ranked.Add(new KeyValuePair<int, FigFontFace>(score * 4096 + Math.Min(ranked.Count, 4095), f));
            }

            ranked.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (KeyValuePair<int, FigFontFace> r in ranked)
            {
                // Only the chosen face's file is read.
                if (Ready(r.Value))
                {
                    return r.Value;
                }
            }

            return null;
        }

        /// <summary>The face's file is read (once per file) and parsed for drawing.</summary>
        private static bool Ready(FigFontFace f)
        {
            return f.EnsureData(ReadFile);
        }

        private static byte[] ReadFile(string path)
        {
            if (fileCache.TryGetValue(path, out byte[] bytes))
            {
                return bytes;
            }

            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception)
            {
                bytes = null;
            }

            fileCache[path] = bytes;
            return bytes;
        }

        // ─── Style names ─────────────────────────────────────────────────────────

        public static int StyleWeight(string style)
        {
            string s = Key(style ?? "");
            if (s.Contains("thin") || s.Contains("hairline"))
            {
                return 100;
            }

            if (s.Contains("extralight") || s.Contains("ultralight"))
            {
                return 200;
            }

            if (s.Contains("semibold") || s.Contains("demibold"))
            {
                return 600;
            }

            if (s.Contains("extrabold") || s.Contains("ultrabold"))
            {
                return 800;
            }

            if (s.Contains("black") || s.Contains("heavy"))
            {
                return 900;
            }

            if (s.Contains("light"))
            {
                return 300;
            }

            if (s.Contains("medium"))
            {
                return 500;
            }

            if (s.Contains("bold"))
            {
                return 700;
            }

            return 400;
        }

        public static bool StyleItalic(string style)
        {
            string s = Key(style ?? "");
            return s.Contains("italic") || s.Contains("oblique");
        }

        private static string Key(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c != ' ' && c != '-' && c != '_')
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }

            return sb.ToString();
        }

        // ─── Index ───────────────────────────────────────────────────────────────

        private static void EnsureIndex()
        {
            lock (sync)
            {
                if (faces != null)
                {
                    return;
                }

                faces = new List<FigFontFace>();
                Dictionary<string, List<FigFontFace>> cached = LoadCache();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var newCache = new StringBuilder();
                foreach (string file in FontFiles())
                {
                    if (!seen.Add(file))
                    {
                        continue;
                    }

                    string stamp;
                    try
                    {
                        var info = new FileInfo(file);
                        stamp = info.Length + ":" + info.LastWriteTimeUtc.Ticks;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    List<FigFontFace> fileFaces;
                    if (cached != null && cached.TryGetValue(file + "|" + stamp, out List<FigFontFace> fromCache))
                    {
                        fileFaces = fromCache;
                    }
                    else
                    {
                        fileFaces = FigFontFace.ReadIndex(file);
                    }

                    foreach (FigFontFace f in fileFaces)
                    {
                        faces.Add(f);
                        newCache.Append(file).Append('\t').Append(stamp).Append('\t').Append(f.Index).Append('\t').Append(f.Family).Append('\t')
                            .Append(f.Style).Append('\t').Append(f.PostScript).Append('\t').Append(f.Weight).Append('\t').Append(f.Italic ? 1 : 0).Append('\t')
                            .Append(f.Variable ? 1 : 0).Append('\t').Append(f.LegacyFamily).Append('\n');
                    }
                }

                SaveCache(newCache.ToString());
                byFamily = new Dictionary<string, List<FigFontFace>>();
                byPostScript = new Dictionary<string, FigFontFace>();
                foreach (FigFontFace f in faces)
                {
                    Add(Key(f.Family), f);
                    if (!string.IsNullOrEmpty(f.LegacyFamily) && Key(f.LegacyFamily) != Key(f.Family))
                    {
                        Add(Key(f.LegacyFamily), f);
                    }

                    if (!string.IsNullOrEmpty(f.PostScript))
                    {
                        byPostScript[Key(f.PostScript)] = f;
                    }
                }
            }
        }

        private static void Add(string key, FigFontFace f)
        {
            if (!byFamily.TryGetValue(key, out List<FigFontFace> list))
            {
                byFamily[key] = list = new List<FigFontFace>();
            }

            list.Add(f);
        }

        private static IEnumerable<string> FontFiles()
        {
            var folders = new List<string>();
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (!string.IsNullOrEmpty(windows))
            {
                folders.Add(windows);
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(local))
            {
                folders.Add(System.IO.Path.Combine(local, "Microsoft", "Windows", "Fonts"));
            }

            folders.Add("/System/Library/Fonts");
            folders.Add("/Library/Fonts");
            folders.Add("/usr/share/fonts");
            folders.Add("/usr/local/share/fonts");
            if (!string.IsNullOrEmpty(home))
            {
                folders.Add(System.IO.Path.Combine(home, "Library", "Fonts"));
                folders.Add(System.IO.Path.Combine(home, ".fonts"));
                folders.Add(System.IO.Path.Combine(home, ".local", "share", "fonts"));
            }

            folders.AddRange(ExtraFolders);
            foreach (string folder in folders)
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories);
                }
                catch (Exception)
                {
                    continue;
                }

                var list = new List<string>();
                try
                {
                    foreach (string f in files)
                    {
                        string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                        if (ext == ".ttf" || ext == ".otf" || ext == ".ttc" || ext == ".otc")
                        {
                            list.Add(f);
                        }
                    }
                }
                catch (Exception)
                {
                    // Unreadable folder: keep what was found.
                }

                foreach (string f in list)
                {
                    yield return f;
                }
            }
        }

        private static Dictionary<string, List<FigFontFace>> LoadCache()
        {
            if (CacheFile == null || !File.Exists(CacheFile))
            {
                return null;
            }

            try
            {
                var map = new Dictionary<string, List<FigFontFace>>();
                foreach (string line in File.ReadAllLines(CacheFile))
                {
                    string[] p = line.Split('\t');
                    if (p.Length < 10)
                    {
                        continue;
                    }

                    var f = new FigFontFace
                    {
                        Path = p[0],
                        Index = int.Parse(p[2]),
                        Family = p[3],
                        Style = p[4],
                        PostScript = p[5],
                        Weight = int.Parse(p[6]),
                        Italic = p[7] == "1",
                        Variable = p[8] == "1",
                        LegacyFamily = p[9],
                    };

                    string key = p[0] + "|" + p[1];
                    if (!map.TryGetValue(key, out List<FigFontFace> list))
                    {
                        map[key] = list = new List<FigFontFace>();
                    }

                    list.Add(f);
                }

                return map;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void SaveCache(string text)
        {
            if (CacheFile == null)
            {
                return;
            }

            try
            {
                File.WriteAllText(CacheFile, text);
            }
            catch (Exception)
            {
                // The cache is only an optimisation.
            }
        }
    }
}
