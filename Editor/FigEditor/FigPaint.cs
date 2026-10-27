using System;
using System.Collections.Generic;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    public enum FigPaintKind
    {
        Solid,
        LinearGradient,
        RadialGradient,
        AngularGradient,
        DiamondGradient,
        Image,
    }

    public enum FigBlend
    {
        Normal,
        Multiply,
        Screen,
        Darken,
        Lighten,
        Overlay,
    }

    /// <summary>
    /// A fill or stroke paint read out of the file once, ready for the renderer (which runs off the
    /// main thread and never touches the decoded message). Immutable.
    /// </summary>
    public sealed class FigPaint
    {
        public FigPaintKind Kind;

        /// <summary>Straight (not premultiplied) colour of a solid paint, alpha including the paint opacity.</summary>
        public Color Color;

        public float Opacity = 1f;
        public FigBlend Blend;

        /// <summary>Maps the layer's unit box to paint space (gradients; STRETCH images).</summary>
        public FigAffine Transform = FigAffine.Identity;

        /// <summary>256 premultiplied RGBA entries for gradients.</summary>
        public float[] Lut;

        public string ImageHash;
        public string ScaleMode = "FILL";
        public float ImageScale = 1f;
        public float ImageRotation;

        public bool IsGradient => Kind != FigPaintKind.Solid && Kind != FigPaintKind.Image;

        public static FigPaint Read(KiwiValue p, float layerOpacity)
        {
            if (p == null || (p.Get("visible") is KiwiValue v && !v.AsBool))
            {
                return null;
            }

            string type = p.Get("type")?.EnumName ?? "SOLID";
            float opacity = (p.Get("opacity")?.AsFloat ?? 1f) * layerOpacity;
            if (opacity <= 0.0005f)
            {
                return null;
            }

            var paint = new FigPaint { Opacity = opacity, Blend = ReadBlend(p.Get("blendMode")?.EnumName) };
            KiwiValue t = p.Get("transform");
            if (t != null)
            {
                paint.Transform = FigAffine.FromMatrix(t);
            }

            switch (type)
            {
                case "SOLID":
                {
                    KiwiValue c = p.Get("color");
                    if (c == null)
                    {
                        return null;
                    }

                    paint.Kind = FigPaintKind.Solid;
                    paint.Color = ReadColor(c);
                    paint.Color.a *= opacity;
                    return paint.Color.a > 0.0005f ? paint : null;
                }
                case "GRADIENT_LINEAR":
                case "GRADIENT_RADIAL":
                case "GRADIENT_ANGULAR":
                case "GRADIENT_DIAMOND":
                {
                    paint.Kind = type == "GRADIENT_LINEAR" ? FigPaintKind.LinearGradient :
                        type == "GRADIENT_RADIAL" ? FigPaintKind.RadialGradient :
                        type == "GRADIENT_ANGULAR" ? FigPaintKind.AngularGradient : FigPaintKind.DiamondGradient;
                    List<KiwiValue> stops = p.Get("stops")?.Items;
                    if (stops == null || stops.Count == 0)
                    {
                        return null;
                    }

                    paint.Lut = BuildLut(stops, opacity);
                    return paint;
                }
                case "IMAGE":
                {
                    byte[] hash = p.Path("image.hash")?.Bytes;
                    if (hash == null || hash.Length == 0)
                    {
                        return null;
                    }

                    paint.Kind = FigPaintKind.Image;
                    paint.ImageHash = FigUtil.Hex(hash);
                    paint.ScaleMode = p.Get("imageScaleMode")?.EnumName ?? "FILL";
                    paint.ImageScale = p.Get("scale")?.AsFloat ?? 1f;
                    paint.ImageRotation = p.Get("rotation")?.AsFloat ?? 0f;
                    return paint;
                }
                default:
                    return null;
            }
        }

        public static Color ReadColor(KiwiValue c)
        {
            return new Color(c.Get("r")?.AsFloat ?? 0, c.Get("g")?.AsFloat ?? 0, c.Get("b")?.AsFloat ?? 0, c.Get("a")?.AsFloat ?? 1);
        }

        public static FigBlend ReadBlend(string name)
        {
            switch (name)
            {
                case "MULTIPLY":
                    return FigBlend.Multiply;
                case "SCREEN":
                    return FigBlend.Screen;
                case "DARKEN":
                    return FigBlend.Darken;
                case "LIGHTEN":
                    return FigBlend.Lighten;
                case "OVERLAY":
                    return FigBlend.Overlay;
                default:
                    return FigBlend.Normal;
            }
        }

        private struct Stop
        {
            public float pos;
            public Color c;
        }

        /// <summary>Interpolate the stops (straight RGBA, like Figma) into a premultiplied lookup table.</summary>
        private static float[] BuildLut(List<KiwiValue> stops, float opacity)
        {
            var list = new List<Stop>();
            foreach (KiwiValue s in stops)
            {
                KiwiValue c = s.Get("color");
                if (c != null)
                {
                    list.Add(new Stop { pos = Mathf.Clamp01(s.Get("position")?.AsFloat ?? 0), c = ReadColor(c) });
                }
            }

            list.Sort((a, b) => a.pos.CompareTo(b.pos));
            var lut = new float[256 * 4];
            for (int i = 0; i < 256; i++)
            {
                float t = i / 255f;
                Color c;
                if (list.Count == 0)
                {
                    c = new Color(0, 0, 0, 0);
                }
                else if (t <= list[0].pos)
                {
                    c = list[0].c;
                }
                else if (t >= list[list.Count - 1].pos)
                {
                    c = list[list.Count - 1].c;
                }
                else
                {
                    int k = 1;
                    while (k < list.Count && list[k].pos < t)
                    {
                        k++;
                    }

                    Stop a = list[k - 1];
                    Stop b = list[k];
                    float span = Mathf.Max(1e-6f, b.pos - a.pos);
                    float f = (t - a.pos) / span;
                    c = new Color(a.c.r + (b.c.r - a.c.r) * f, a.c.g + (b.c.g - a.c.g) * f, a.c.b + (b.c.b - a.c.b) * f, a.c.a + (b.c.a - a.c.a) * f);
                }

                float alpha = c.a * opacity;
                lut[i * 4] = c.r * alpha;
                lut[i * 4 + 1] = c.g * alpha;
                lut[i * 4 + 2] = c.b * alpha;
                lut[i * 4 + 3] = alpha;
            }

            return lut;
        }
    }

    public enum FigEffectKind
    {
        DropShadow,
        InnerShadow,
        LayerBlur,
        BackgroundBlur,
    }

    public sealed class FigEffect
    {
        public FigEffectKind Kind;
        public Color Color;
        public Vector2 Offset;
        public float Radius;
        public float Spread;
        public bool ShowBehind;

        public static FigEffect Read(KiwiValue e)
        {
            if (e == null || (e.Get("visible") is KiwiValue v && !v.AsBool))
            {
                return null;
            }

            FigEffectKind kind;
            switch (e.Get("type")?.EnumName)
            {
                case "DROP_SHADOW":
                    kind = FigEffectKind.DropShadow;
                    break;
                case "INNER_SHADOW":
                    kind = FigEffectKind.InnerShadow;
                    break;
                case "FOREGROUND_BLUR":
                    kind = FigEffectKind.LayerBlur;
                    break;
                case "BACKGROUND_BLUR":
                    kind = FigEffectKind.BackgroundBlur;
                    break;
                default:
                    return null;
            }

            KiwiValue color = e.Get("color");
            KiwiValue offset = e.Get("offset");
            return new FigEffect
            {
                Kind = kind,
                Color = color != null ? FigPaint.ReadColor(color) : new Color(0, 0, 0, 0.25f),
                Offset = new Vector2(offset?.Get("x")?.AsFloat ?? 0, offset?.Get("y")?.AsFloat ?? 0),
                Radius = Mathf.Max(0, e.Get("radius")?.AsFloat ?? 0),
                Spread = e.Get("spread")?.AsFloat ?? 0,
                ShowBehind = e.Get("showShadowBehindNode")?.AsBool ?? false,
            };
        }

        /// <summary>How far outside the layer this effect paints, in layer units.</summary>
        public float Reach => Kind == FigEffectKind.InnerShadow ? 0 : Radius * 1.5f + Mathf.Abs(Offset.x) + Mathf.Abs(Offset.y) + Mathf.Max(0, Spread);
    }

    /// <summary>
    /// A decoded image (top-down straight RGBA) with a box-filtered mip chain, so minified images
    /// sample without shimmering. Built on the main thread, read by the renderer threads.
    /// </summary>
    public sealed class FigImageData
    {
        public readonly int Width;
        public readonly int Height;
        public readonly List<Color32[]> Levels = new();
        public readonly List<int> LevelWidths = new();
        public readonly List<int> LevelHeights = new();

        public FigImageData(int width, int height, Color32[] topDown)
        {
            Width = width;
            Height = height;
            Levels.Add(topDown);
            LevelWidths.Add(width);
            LevelHeights.Add(height);
            int w = width, h = height;
            Color32[] src = topDown;
            while ((w > 1 || h > 1) && Levels.Count < 14)
            {
                int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
                var dst = new Color32[nw * nh];
                for (int y = 0; y < nh; y++)
                {
                    int y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                    for (int x = 0; x < nw; x++)
                    {
                        int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1);
                        Color32 a = src[y0 * w + x0], b = src[y0 * w + x1], c = src[y1 * w + x0], d = src[y1 * w + x1];
                        dst[y * nw + x] = new Color32((byte)((a.r + b.r + c.r + d.r + 2) >> 2), (byte)((a.g + b.g + c.g + d.g + 2) >> 2),
                            (byte)((a.b + b.b + c.b + d.b + 2) >> 2), (byte)((a.a + b.a + c.a + d.a + 2) >> 2));
                    }
                }

                Levels.Add(dst);
                LevelWidths.Add(nw);
                LevelHeights.Add(nh);
                src = dst;
                w = nw;
                h = nh;
            }
        }
    }
}
