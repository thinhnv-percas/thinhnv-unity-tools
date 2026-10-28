using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>One text style: the layer's own, or a per-character override merged over it.</summary>
    public sealed class FigTextStyle
    {
        public string Family = "Inter";
        public string Style = "Regular";
        public string PostScript;
        public float Size = 12f;

        /// <summary>0 auto (the font's own), 1 pixels, 2 percent of the font size.</summary>
        public int LineUnits;

        public float LineValue;
        public bool LetterPercent;
        public float Letter;
        public string Case = "ORIGINAL";
        public string Decoration = "NONE";

        public FigTextStyle Copy() => (FigTextStyle)MemberwiseClone();

        /// <summary>Read the text fields present on <paramref name="v"/> (a layer, or an override row) over this style.</summary>
        public FigTextStyle With(Func<string, KiwiValue> get)
        {
            FigTextStyle s = Copy();
            KiwiValue font = get("fontName");
            if (font != null)
            {
                s.Family = font.Get("family")?.String ?? s.Family;
                s.Style = font.Get("style")?.String ?? s.Style;
                string ps = font.Get("postscript")?.String;
                s.PostScript = string.IsNullOrEmpty(ps) ? null : ps;
            }

            KiwiValue size = get("fontSize");
            if (size != null)
            {
                s.Size = Mathf.Clamp(size.AsFloat, 0.5f, 2000f);
            }

            KiwiValue lh = get("lineHeight");
            if (lh != null)
            {
                float value = lh.Get("value")?.AsFloat ?? 100f;
                string units = lh.Get("units")?.EnumName ?? "PERCENT";
                // Like figcraft: 100 % is "auto"; other percents are of the font size; raw values are multipliers.
                if (units == "PIXELS")
                {
                    s.LineUnits = 1;
                    s.LineValue = value;
                }
                else if (units == "PERCENT" && Mathf.Abs(value - 100f) < 1e-4f)
                {
                    s.LineUnits = 0;
                }
                else
                {
                    s.LineUnits = 2;
                    s.LineValue = units == "PERCENT" ? value : value * 100f;
                }
            }

            KiwiValue ls = get("letterSpacing");
            if (ls != null)
            {
                s.Letter = ls.Get("value")?.AsFloat ?? 0f;
                s.LetterPercent = (ls.Get("units")?.EnumName ?? "PERCENT") == "PERCENT";
            }

            s.Case = get("textCase")?.EnumName ?? s.Case;
            s.Decoration = get("textDecoration")?.EnumName ?? s.Decoration;
            return s;
        }

        public void Key(StringBuilder sb)
        {
            sb.Append(Family).Append('|').Append(Style).Append('|').Append(PostScript).Append('|').Append(Size.ToString("R", CultureInfo.InvariantCulture))
                .Append('|').Append(LineUnits).Append(LineValue.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(LetterPercent)
                .Append(Letter.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(Case).Append('|').Append(Decoration);
        }
    }

    /// <summary>What a text layer needs laid out.</summary>
    public sealed class FigTextSpec
    {
        public string Text = "";

        /// <summary>A style id per UTF-16 unit (missing or 0: the layer's own style).</summary>
        public uint[] StyleIds;

        public FigTextStyle Base = new();
        public Dictionary<uint, FigTextStyle> Overrides = new();
        public string AlignH = "LEFT";
        public string AlignV = "TOP";
        public string AutoResize = "NONE";
        public float Width;
        public float Height;
        public float ParagraphSpacing;
        public float ParagraphIndent;
        public int MaxLines;
        public bool Truncate;

        public string Key()
        {
            var sb = new StringBuilder(Text.Length + 128);
            sb.Append(Text).Append('\u0001');
            if (StyleIds != null)
            {
                foreach (uint id in StyleIds)
                {
                    sb.Append(id).Append(',');
                }
            }

            sb.Append('\u0001');
            Base.Key(sb);
            foreach (KeyValuePair<uint, FigTextStyle> pair in Overrides)
            {
                sb.Append('\u0002').Append(pair.Key).Append(':');
                pair.Value.Key(sb);
            }

            sb.Append('\u0001').Append(AlignH).Append(AlignV).Append(AutoResize).Append('|').Append(Width.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(Height.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(ParagraphSpacing).Append('|').Append(ParagraphIndent).Append('|')
                .Append(MaxLines).Append(Truncate);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Lays text out with the installed fonts, the way figcraft does (<c>crates/text/src/layout.rs</c>):
    /// per-character styles, kerning, letter spacing, wrapping at break opportunities, line heights
    /// in whole pixels (auto = the font's ascent + descent + gap), paragraph spacing and indent,
    /// horizontal alignment with justification, vertical alignment in the box, decorations and
    /// end truncation. Characters a font lacks come from fallback fonts. Output is glyph outlines in
    /// the layer's space, grouped by style id.
    /// </summary>
    public static class FigTextLayout
    {
        private const float AdvanceGrid = 1024f;
        private const float SynthItalicShear = 0.2f;
        private const int MaxCache = 4000;

        private static readonly Dictionary<string, Result> cache = new();
        private static readonly object sync = new();

        public sealed class Glyph
        {
            public uint Style;
            public FigPath Path;
            public Vector2 Origin;
        }

        public sealed class Result
        {
            public readonly List<Glyph> Glyphs = new();

            /// <summary>Stroke width (layer units) that thickens a style drawn with a lighter face than asked for.</summary>
            public readonly Dictionary<uint, float> Embolden = new();

            public float Width;
            public float Height;

            /// <summary>Fonts that weren't installed and what drew them instead ("X Bold → Arial Bold").</summary>
            public string Substitutions;
        }

        private struct Item
        {
            public int Codepoint;
            public uint Style;
            public FigFontFace Face;
            public int Gid;
            public float Adv;
            public float Spacing;
            public float Size;
            public bool Space;
            public bool Break;
        }

        private sealed class Resolved
        {
            public FigTextStyle Style;
            public FigFontFace Face;
            public float Size;
            public float Spacing;
            public float Shear;
            public float Embolden;
            public float Asc, Desc, LineHeight;
        }

        public static void ClearCache()
        {
            lock (sync)
            {
                cache.Clear();
            }
        }

        /// <summary>The laid-out glyphs, or null when no font is installed at all.</summary>
        public static Result Layout(FigTextSpec spec)
        {
            string key = spec.Key();
            lock (sync)
            {
                if (cache.TryGetValue(key, out Result hit))
                {
                    return hit;
                }
            }

            Result result = Run(spec);
            lock (sync)
            {
                if (cache.Count >= MaxCache)
                {
                    cache.Clear();
                }

                cache[key] = result;
            }

            return result;
        }

        private static Result Run(FigTextSpec spec)
        {
            var resolved = new Dictionary<uint, Resolved>();
            var missing = new List<string>();
            string text = spec.Text ?? "";
            var items = new List<Item>(text.Length);

            // Characters → glyphs, with the case transform, fallback fonts, kerning and spacing.
            bool wordStart = true;
            for (int i = 0; i < text.Length; i++)
            {
                int cp = text[i];
                int unit = i;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    cp = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }

                uint id = spec.StyleIds != null && unit < spec.StyleIds.Length ? spec.StyleIds[unit] : 0;
                Resolved r = ResolveStyle(spec, id, resolved, missing);
                if (r == null)
                {
                    return null;
                }

                if (cp == '\n' || cp == 0x2028 || cp == '\r')
                {
                    if (cp == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        continue;
                    }

                    items.Add(new Item { Codepoint = '\n', Style = id, Face = r.Face, Break = true, Size = r.Size });
                    wordStart = true;
                    continue;
                }

                float scale = 1f;
                cp = CaseOf(cp, r.Style.Case, wordStart, ref scale);
                bool space = cp == ' ' || cp == '\t' || cp == 0xA0 || cp == 0x3000 || (cp >= 0x2000 && cp <= 0x200B);
                wordStart = space;
                FigFontFace face = r.Face;
                int gid = face.GlyphIndex(cp == '\t' ? ' ' : cp);
                if (gid == 0 && !space)
                {
                    FigFontFace fb = FigFonts.FallbackFor(cp, face);
                    if (fb != null)
                    {
                        face = fb;
                        gid = fb.GlyphIndex(cp);
                    }
                }

                float size = r.Size * scale;
                float upm = face.UnitsPerEm;
                float adv = OnGrid(face.Advance(gid), upm) * size;
                if (cp == '\t')
                {
                    adv *= 4;
                }

                // Kerning with the previous glyph of the same face and size.
                if (items.Count > 0)
                {
                    Item prev = items[items.Count - 1];
                    if (!prev.Break && prev.Face == face && Mathf.Approximately(prev.Size, size))
                    {
                        prev.Adv += face.Kerning(prev.Gid, gid) * size / upm;
                        items[items.Count - 1] = prev;
                    }
                }

                items.Add(new Item { Codepoint = cp, Style = id, Face = face, Gid = gid, Adv = adv + r.Spacing, Spacing = r.Spacing, Size = size, Space = space });
            }

            // Line breaking.
            bool wrap = spec.AutoResize != "WIDTH_AND_HEIGHT" && spec.Width > 0;
            float maxWidth = spec.Width;
            var lines = new List<int[]>(); // start, end, paragraph end (0/1), first line of paragraph (0/1)
            int lineStart = 0, lastBreak = -1;
            float x = 0;
            bool paraFirst = true;
            for (int i = 0; i < items.Count;)
            {
                Item it = items[i];
                if (it.Break)
                {
                    lines.Add(new[] { lineStart, i + 1, 1, paraFirst ? 1 : 0 });
                    i++;
                    lineStart = i;
                    x = 0;
                    lastBreak = -1;
                    paraFirst = true;
                    continue;
                }

                float indent = paraFirst && spec.AlignH == "LEFT" ? spec.ParagraphIndent : 0f;
                if (wrap && x + it.Adv - it.Spacing > maxWidth - indent + 1e-3f && i > lineStart && !it.Space)
                {
                    int brk = lastBreak >= lineStart ? lastBreak + 1 : i;
                    lines.Add(new[] { lineStart, brk, 0, paraFirst ? 1 : 0 });
                    lineStart = brk;
                    i = brk;
                    x = 0;
                    lastBreak = -1;
                    paraFirst = false;
                    continue;
                }

                x += it.Adv;
                if (i + 1 < items.Count && BreakBetween(it.Codepoint, items[i + 1].Codepoint))
                {
                    lastBreak = i;
                }

                i++;
            }

            if (lineStart < items.Count || lines.Count == 0 || lines[lines.Count - 1][2] == 1)
            {
                lines.Add(new[] { lineStart, items.Count, 1, paraFirst ? 1 : 0 });
            }

            // Lines: metrics, baselines, alignment.
            var result = new Result();
            if (missing.Count > 0)
            {
                result.Substitutions = string.Join(", ", missing);
            }

            foreach (KeyValuePair<uint, Resolved> pair in resolved)
            {
                if (pair.Value.Embolden > 0)
                {
                    result.Embolden[pair.Key] = pair.Value.Embolden;
                }
            }

            int maxLines = spec.MaxLines > 0 && spec.Truncate ? spec.MaxLines : int.MaxValue;
            float y = 0;
            var placed = new List<KeyValuePair<int, float[]>>(); // line index → (top, height, baseline, width, start glyph)
            var lineGlyphs = new List<List<Glyph>>();
            var lineWidths = new List<float>();
            bool truncated = false;
            for (int li = 0; li < lines.Count; li++)
            {
                if (li >= maxLines)
                {
                    truncated = true;
                    break;
                }

                int s = lines[li][0], e = lines[li][1];
                bool paraStart = lines[li][3] == 1;
                float asc = 0, desc = 0, lh = 0;
                bool any = false;
                for (int k = s; k < e; k++)
                {
                    Resolved r = resolved[items[k].Style];
                    asc = Mathf.Max(asc, r.Asc);
                    desc = Mathf.Max(desc, r.Desc);
                    lh = Mathf.Max(lh, r.LineHeight);
                    any = true;
                }

                if (!any)
                {
                    // An empty line (after a final line break, or an empty text): the style before it.
                    Resolved r = ResolveStyle(spec, s > 0 ? items[s - 1].Style : 0, resolved, missing);
                    if (r == null)
                    {
                        return null;
                    }

                    asc = r.Asc;
                    desc = r.Desc;
                    lh = r.LineHeight;
                }

                if (paraStart && li > 0)
                {
                    y += Mathf.Max(0, spec.ParagraphSpacing);
                }

                float baseline = y + (lh - (asc + desc)) / 2f + asc;
                int visEnd = e;
                while (visEnd > s && (items[visEnd - 1].Space || items[visEnd - 1].Break))
                {
                    visEnd--;
                }

                float width = 0;
                for (int k = s; k < visEnd; k++)
                {
                    width += items[k].Adv;
                }

                if (visEnd > s)
                {
                    width -= items[visEnd - 1].Spacing;
                }

                float indent = paraStart && spec.AlignH == "LEFT" ? spec.ParagraphIndent : 0f;
                int gaps = 0;
                for (int k = s; k < visEnd; k++)
                {
                    if (items[k].Space)
                    {
                        gaps++;
                    }
                }

                float justify = spec.AlignH == "JUSTIFIED" && wrap && lines[li][2] == 0 && gaps > 0 ? Mathf.Max(0, (maxWidth - indent - width) / gaps) : 0f;
                var glyphs = new List<Glyph>();
                float pen = indent;
                for (int k = s; k < e; k++)
                {
                    Item it = items[k];
                    if (it.Break)
                    {
                        continue;
                    }

                    if (!it.Space && it.Gid != 0)
                    {
                        AddGlyph(glyphs, it, resolved[it.Style], pen, baseline);
                    }
                    else if (!it.Space && it.Gid == 0)
                    {
                        // The font's "missing glyph" box, like Figma shows.
                        AddGlyph(glyphs, it, resolved[it.Style], pen, baseline);
                    }

                    pen += it.Adv + (it.Space ? justify : 0f);
                }

                Decorate(glyphs, items, s, visEnd, resolved, indent, baseline, justify);
                lineGlyphs.Add(glyphs);
                lineWidths.Add(width + indent + justify * gaps);
                placed.Add(new KeyValuePair<int, float[]>(li, new[] { y, lh, baseline }));
                y += lh;
            }

            float contentHeight = y;
            // Truncation by height: keep the lines that fit, end the last one with an ellipsis.
            if (spec.Truncate && spec.AutoResize != "WIDTH_AND_HEIGHT" && spec.Height > 0)
            {
                int keep = 0;
                foreach (KeyValuePair<int, float[]> p in placed)
                {
                    if (p.Value[0] + p.Value[1] <= spec.Height + 0.5f)
                    {
                        keep++;
                    }
                }

                keep = Mathf.Max(1, keep);
                if (keep < lineGlyphs.Count)
                {
                    lineGlyphs.RemoveRange(keep, lineGlyphs.Count - keep);
                    lineWidths.RemoveRange(keep, lineWidths.Count - keep);
                    placed.RemoveRange(keep, placed.Count - keep);
                    contentHeight = placed[keep - 1].Value[0] + placed[keep - 1].Value[1];
                    truncated = true;
                }
            }

            if (truncated && lineGlyphs.Count > 0)
            {
                Ellipsis(lineGlyphs[lineGlyphs.Count - 1], lines[placed.Count - 1], items, resolved, placed[placed.Count - 1].Value[2], wrap ? maxWidth : float.MaxValue,
                    lineWidths, lineGlyphs.Count - 1);
            }

            float alignWidth = wrap ? maxWidth : 0;
            foreach (float w in lineWidths)
            {
                if (!wrap)
                {
                    alignWidth = Mathf.Max(alignWidth, w);
                }
            }

            if (spec.AutoResize == "NONE" || spec.AutoResize == "HEIGHT")
            {
                alignWidth = Mathf.Max(alignWidth, spec.Width);
            }

            float dy = spec.AlignV == "CENTER" ? (spec.Height - contentHeight) / 2f : spec.AlignV == "BOTTOM" ? spec.Height - contentHeight : 0f;
            for (int li = 0; li < lineGlyphs.Count; li++)
            {
                float dx = spec.AlignH == "CENTER" ? (alignWidth - lineWidths[li]) / 2f : spec.AlignH == "RIGHT" ? alignWidth - lineWidths[li] : 0f;
                foreach (Glyph g in lineGlyphs[li])
                {
                    if (dx != 0 || dy != 0)
                    {
                        g.Path = g.Path.Transformed(new FigAffine { A = 1, E = 1, C = dx, F = dy });
                        g.Origin += new Vector2(dx, dy);
                    }

                    result.Glyphs.Add(g);
                }

                result.Width = Mathf.Max(result.Width, lineWidths[li]);
            }

            result.Height = contentHeight;
            return result;
        }

        private static Resolved ResolveStyle(FigTextSpec spec, uint id, Dictionary<uint, Resolved> resolved, List<string> missing)
        {
            if (resolved.TryGetValue(id, out Resolved r))
            {
                return r;
            }

            FigTextStyle style = id != 0 && spec.Overrides.TryGetValue(id, out FigTextStyle o) ? o : spec.Base;
            FigFontFace face = FigFonts.Find(style.Family, style.Style, style.PostScript, out bool exact);
            if (face == null)
            {
                return null;
            }

            if (!exact)
            {
                string note = style.Family + " " + style.Style + " → " + face.Family + " " + face.Style;
                if (!missing.Contains(note))
                {
                    missing.Add(note);
                }
            }

            float size = style.Size;
            float k = size / face.UnitsPerEm;
            float natural = Mathf.Max(0.5f, (face.Ascent + face.Descent + Mathf.Max(0, face.LineGap)) / face.UnitsPerEm);
            float lineHeight = style.LineUnits == 1 ? Mathf.Max(0, style.LineValue) : style.LineUnits == 2 ? size * Mathf.Max(0, style.LineValue) / 100f : size * natural;
            int wantWeight = FigFonts.StyleWeight(style.Style);
            r = new Resolved
            {
                Style = style,
                Face = face,
                Size = size,
                Spacing = style.LetterPercent ? size * style.Letter / 100f : style.Letter,
                Asc = face.Ascent * k,
                Desc = face.Descent * k,
                LineHeight = lineHeight >= 1f ? Mathf.Floor(lineHeight + 0.5f) : lineHeight,
                // Synthesised bold and italic when the installed face is lighter or upright.
                Embolden = wantWeight - face.Weight >= 200 ? size * 0.035f * (wantWeight - face.Weight) / 300f : 0f,
                Shear = FigFonts.StyleItalic(style.Style) && !face.Italic ? SynthItalicShear : 0f,
            };

            resolved[id] = r;
            return r;
        }

        private static void AddGlyph(List<Glyph> glyphs, Item it, Resolved r, float pen, float baseline)
        {
            FigPath outline = it.Face.Outline(it.Gid);
            if (outline.IsEmpty)
            {
                return;
            }

            float s = it.Size / it.Face.UnitsPerEm;
            var t = new FigAffine { A = s, B = r.Shear * s, C = pen, D = 0, E = -s, F = baseline };
            glyphs.Add(new Glyph { Style = it.Style, Path = outline.Transformed(t), Origin = new Vector2(pen, baseline) });
        }

        /// <summary>Underlines and strike-throughs per run of one decorated style.</summary>
        private static void Decorate(List<Glyph> glyphs, List<Item> items, int s, int e, Dictionary<uint, Resolved> resolved, float pen, float baseline, float justify)
        {
            int k = s;
            float x = pen;
            while (k < e)
            {
                uint style = items[k].Style;
                Resolved r = resolved[style];
                float x0 = x;
                int j = k;
                while (j < e && items[j].Style == style)
                {
                    x += items[j].Adv + (items[j].Space ? justify : 0f);
                    j++;
                }

                string deco = r.Style.Decoration;
                if (deco == "UNDERLINE" || deco == "STRIKETHROUGH")
                {
                    FigFontFace f = r.Face;
                    float scale = r.Size / f.UnitsPerEm;
                    bool under = deco == "UNDERLINE";
                    float th = Mathf.Max(0.5f, (under ? f.UnderlineThickness : f.StrikeThickness) * scale);
                    float top = baseline - (under ? f.UnderlinePosition : f.StrikePosition) * scale - th / 2f;
                    float x1 = x - items[j - 1].Spacing;
                    if (x1 > x0)
                    {
                        glyphs.Add(new Glyph { Style = style, Path = FigPath.Rectangle(x0, top, x1 - x0, th), Origin = new Vector2(x0, baseline) });
                    }
                }

                k = j;
            }
        }

        /// <summary>End a truncated line with "…", dropping glyphs until it fits.</summary>
        private static void Ellipsis(List<Glyph> glyphs, int[] line, List<Item> items, Dictionary<uint, Resolved> resolved, float baseline, float maxWidth,
            List<float> widths, int index)
        {
            int last = line[1] - 1;
            while (last >= line[0] && (items[last].Break || items[last].Space))
            {
                last--;
            }

            uint style = last >= line[0] ? items[last].Style : (line[0] < items.Count ? items[line[0]].Style : 0);
            if (!resolved.TryGetValue(style, out Resolved r))
            {
                return;
            }

            FigFontFace face = r.Face;
            int gid = face.GlyphIndex(0x2026);
            float ellipsisWidth = gid != 0 ? OnGrid(face.Advance(gid), face.UnitsPerEm) * r.Size : 0;
            float pen = widths[index];
            while (glyphs.Count > 0 && pen + ellipsisWidth > maxWidth)
            {
                Glyph g = glyphs[glyphs.Count - 1];
                glyphs.RemoveAt(glyphs.Count - 1);
                pen = g.Origin.x;
            }

            if (gid != 0)
            {
                var it = new Item { Face = face, Gid = gid, Size = r.Size, Style = style };
                AddGlyph(glyphs, it, r, pen, baseline);
                widths[index] = pen + ellipsisWidth;
            }
        }

        private static float OnGrid(float units, float upm)
        {
            return Mathf.Round(units * AdvanceGrid / upm) / AdvanceGrid;
        }

        private static int CaseOf(int cp, string textCase, bool wordStart, ref float scale)
        {
            if (cp > 0xFFFF)
            {
                return cp;
            }

            char c = (char)cp;
            switch (textCase)
            {
                case "UPPER":
                    return char.ToUpperInvariant(c);
                case "LOWER":
                    return char.ToLowerInvariant(c);
                case "TITLE":
                    return wordStart ? char.ToUpperInvariant(c) : c;
                case "SMALL_CAPS":
                case "SMALL_CAPS_FORCED":
                    if (char.IsLower(c) || textCase == "SMALL_CAPS_FORCED")
                    {
                        scale = 0.8f;
                    }

                    return char.ToUpperInvariant(c);
                default:
                    return cp;
            }
        }

        /// <summary>Where a line may break between two characters: after spaces and hyphens, and around CJK.</summary>
        private static bool BreakBetween(int a, int b)
        {
            bool aSpace = a == ' ' || a == '\t' || a == 0x3000 || (a >= 0x2000 && a <= 0x200B);
            bool bSpace = b == ' ' || b == '\t' || b == 0x3000 || (b >= 0x2000 && b <= 0x200B);
            if (aSpace && !bSpace)
            {
                return true;
            }

            if ((a == '-' || a == 0x2013 || a == 0x2014 || a == '/') && !bSpace)
            {
                return true;
            }

            return IsCjk(a) || IsCjk(b) && !IsClosingPunctuation(b);
        }

        private static bool IsCjk(int c)
        {
            return (c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF00 && c <= 0xFFEF) || (c >= 0x20000 && c <= 0x2FFFF);
        }

        private static bool IsClosingPunctuation(int c)
        {
            return c == 0x3001 || c == 0x3002 || c == 0xFF0C || c == 0xFF0E || c == 0xFF1F || c == 0xFF01 || c == 0x300D || c == 0x300F || c == 0xFF09;
        }
    }
}
