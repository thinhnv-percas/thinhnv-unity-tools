using System;
using System.Collections.Generic;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// An immutable path in a layer's local space — commands <c>0</c> close, <c>1</c> move,
    /// <c>2</c> line, <c>3</c> quadratic, <c>4</c> cubic, the same encoding as Figma's geometry blobs —
    /// plus the winding rule it fills with. Curves stay curves until <see cref="Flatten"/>, which
    /// subdivides them for the zoom they are drawn at, so shapes stay smooth at any zoom.
    /// </summary>
    public sealed class FigPath
    {
        private const float Kappa = 0.5522847f;

        public readonly byte[] Commands;
        public readonly float[] Points;
        public readonly bool EvenOdd;
        public readonly Rect Bounds;

        private FigPath(byte[] commands, float[] points, bool evenOdd)
        {
            Commands = commands;
            Points = points;
            EvenOdd = evenOdd;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i + 1 < points.Length; i += 2)
            {
                x0 = Mathf.Min(x0, points[i]);
                x1 = Mathf.Max(x1, points[i]);
                y0 = Mathf.Min(y0, points[i + 1]);
                y1 = Mathf.Max(y1, points[i + 1]);
            }

            Bounds = x0 > x1 ? new Rect(0, 0, 0, 0) : Rect.MinMaxRect(x0, y0, x1, y1);
        }

        public bool IsEmpty => Commands.Length == 0;

        /// <summary>A path from a geometry blob, or null when it is malformed.</summary>
        public static FigPath FromBlob(byte[] blob, bool evenOdd)
        {
            if (blob == null)
            {
                return null;
            }

            var commands = new List<byte>();
            var points = new List<float>();
            int pos = 0;
            while (pos < blob.Length)
            {
                byte cmd = blob[pos++];
                int count;
                switch (cmd)
                {
                    case 0:
                        count = 0;
                        break;
                    case 1:
                    case 2:
                        count = 1;
                        break;
                    case 3:
                        count = 2;
                        break;
                    case 4:
                        count = 3;
                        break;
                    default:
                        return null;
                }

                if (pos + count * 8 > blob.Length)
                {
                    return null;
                }

                for (int i = 0; i < count * 2; i++)
                {
                    float v = BitConverter.ToSingle(blob, pos);
                    pos += 4;
                    if (float.IsNaN(v) || float.IsInfinity(v))
                    {
                        return null;
                    }

                    points.Add(v);
                }

                commands.Add(cmd);
            }

            return new FigPath(commands.ToArray(), points.ToArray(), evenOdd);
        }

        /// <summary>The same path with another winding rule.</summary>
        public FigPath WithWinding(bool evenOdd)
        {
            return evenOdd == EvenOdd ? this : new FigPath(Commands, Points, evenOdd);
        }

        /// <summary>The same path mapped through <paramref name="t"/>.</summary>
        public FigPath Transformed(FigAffine t)
        {
            var pts = new float[Points.Length];
            for (int i = 0; i + 1 < Points.Length; i += 2)
            {
                Vector2 p = t.Apply(Points[i], Points[i + 1]);
                pts[i] = p.x;
                pts[i + 1] = p.y;
            }

            return new FigPath(Commands, pts, EvenOdd);
        }

        /// <summary>All of <paramref name="paths"/> as one path (their contours share one winding rule).</summary>
        public static FigPath Concat(IList<FigPath> paths, bool evenOdd)
        {
            var b = new Builder();
            foreach (FigPath p in paths)
            {
                if (p == null)
                {
                    continue;
                }

                b.Commands.AddRange(p.Commands);
                b.Points.AddRange(p.Points);
            }

            return b.Build(evenOdd);
        }

        // ─── Shapes ──────────────────────────────────────────────────────────────

        public static FigPath Rectangle(float x, float y, float w, float h)
        {
            var b = new Builder();
            b.MoveTo(x, y);
            b.LineTo(x + w, y);
            b.LineTo(x + w, y + h);
            b.LineTo(x, y + h);
            b.Close();
            return b.Build(false);
        }

        /// <summary>Corner radii are clamped like Figma does: never more than half the shorter side.</summary>
        public static FigPath RoundedRectangle(float x, float y, float w, float h, float tl, float tr, float br, float bl)
        {
            float max = Mathf.Max(0, Mathf.Min(w, h) * 0.5f);
            tl = Mathf.Clamp(tl, 0, max);
            tr = Mathf.Clamp(tr, 0, max);
            br = Mathf.Clamp(br, 0, max);
            bl = Mathf.Clamp(bl, 0, max);
            if (tl <= 0 && tr <= 0 && br <= 0 && bl <= 0)
            {
                return Rectangle(x, y, w, h);
            }

            var b = new Builder();
            b.MoveTo(x + tl, y);
            b.LineTo(x + w - tr, y);
            if (tr > 0)
            {
                b.CubicTo(x + w - tr + tr * Kappa, y, x + w, y + tr - tr * Kappa, x + w, y + tr);
            }

            b.LineTo(x + w, y + h - br);
            if (br > 0)
            {
                b.CubicTo(x + w, y + h - br + br * Kappa, x + w - br + br * Kappa, y + h, x + w - br, y + h);
            }

            b.LineTo(x + bl, y + h);
            if (bl > 0)
            {
                b.CubicTo(x + bl - bl * Kappa, y + h, x, y + h - bl + bl * Kappa, x, y + h - bl);
            }

            b.LineTo(x, y + tl);
            if (tl > 0)
            {
                b.CubicTo(x, y + tl - tl * Kappa, x + tl - tl * Kappa, y, x + tl, y);
            }

            b.Close();
            return b.Build(false);
        }

        public static FigPath Ellipse(float x, float y, float w, float h)
        {
            float rx = w * 0.5f, ry = h * 0.5f, cx = x + rx, cy = y + ry;
            float kx = rx * Kappa, ky = ry * Kappa;
            var b = new Builder();
            b.MoveTo(cx + rx, cy);
            b.CubicTo(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry);
            b.CubicTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy);
            b.CubicTo(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry);
            b.CubicTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy);
            b.Close();
            return b.Build(false);
        }

        /// <summary>A ring between two closed shapes, filled even-odd (strokes of primitives).</summary>
        public static FigPath Ring(FigPath outer, FigPath inner)
        {
            if (inner == null || inner.IsEmpty)
            {
                return outer.WithWinding(true);
            }

            return Concat(new[] { outer, inner }, true);
        }

        // ─── Flattening ──────────────────────────────────────────────────────────

        /// <summary>
        /// The path's contours as polylines in pixel space (through <paramref name="toPixels"/>),
        /// curves subdivided until they deviate less than <paramref name="tolerance"/> pixels.
        /// Every contour is closed implicitly for filling.
        /// </summary>
        public void Flatten(FigAffine toPixels, float tolerance, List<Vector2[]> output)
        {
            var current = new List<Vector2>(32);
            Vector2 cursor = default;
            Vector2 start = default;
            int pi = 0;
            foreach (byte cmd in Commands)
            {
                switch (cmd)
                {
                    case 0:
                        if (current.Count >= 2)
                        {
                            output.Add(current.ToArray());
                        }

                        current.Clear();
                        cursor = start;
                        break;
                    case 1:
                        if (current.Count >= 2)
                        {
                            output.Add(current.ToArray());
                        }

                        current.Clear();
                        cursor = start = toPixels.Apply(Points[pi], Points[pi + 1]);
                        pi += 2;
                        current.Add(cursor);
                        break;
                    case 2:
                    {
                        if (current.Count == 0)
                        {
                            current.Add(cursor);
                        }

                        cursor = toPixels.Apply(Points[pi], Points[pi + 1]);
                        pi += 2;
                        current.Add(cursor);
                        break;
                    }
                    case 3:
                    {
                        if (current.Count == 0)
                        {
                            current.Add(cursor);
                        }

                        Vector2 c = toPixels.Apply(Points[pi], Points[pi + 1]);
                        Vector2 e = toPixels.Apply(Points[pi + 2], Points[pi + 3]);
                        pi += 4;
                        float dd = (cursor - 2 * c + e).magnitude;
                        int n = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(dd / (8f * tolerance)) * 2f), 1, 256);
                        for (int i = 1; i <= n; i++)
                        {
                            float t = (float)i / n, u = 1 - t;
                            current.Add(u * u * cursor + 2 * u * t * c + t * t * e);
                        }

                        cursor = e;
                        break;
                    }
                    case 4:
                    {
                        if (current.Count == 0)
                        {
                            current.Add(cursor);
                        }

                        Vector2 c1 = toPixels.Apply(Points[pi], Points[pi + 1]);
                        Vector2 c2 = toPixels.Apply(Points[pi + 2], Points[pi + 3]);
                        Vector2 e = toPixels.Apply(Points[pi + 4], Points[pi + 5]);
                        pi += 6;
                        float dd = Mathf.Max((cursor - 2 * c1 + c2).magnitude, (c1 - 2 * c2 + e).magnitude);
                        int n = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(dd * 0.75f / tolerance)), 1, 256);
                        for (int i = 1; i <= n; i++)
                        {
                            float t = (float)i / n, u = 1 - t;
                            current.Add(u * u * u * cursor + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * e);
                        }

                        cursor = e;
                        break;
                    }
                }
            }

            if (current.Count >= 2)
            {
                output.Add(current.ToArray());
            }
        }

        public sealed class Builder
        {
            public readonly List<byte> Commands = new();
            public readonly List<float> Points = new();

            public void MoveTo(float x, float y)
            {
                Commands.Add(1);
                Points.Add(x);
                Points.Add(y);
            }

            public void LineTo(float x, float y)
            {
                Commands.Add(2);
                Points.Add(x);
                Points.Add(y);
            }

            public void QuadTo(float x1, float y1, float x, float y)
            {
                Commands.Add(3);
                Points.Add(x1);
                Points.Add(y1);
                Points.Add(x);
                Points.Add(y);
            }

            public void CubicTo(float x1, float y1, float x2, float y2, float x, float y)
            {
                Commands.Add(4);
                Points.Add(x1);
                Points.Add(y1);
                Points.Add(x2);
                Points.Add(y2);
                Points.Add(x);
                Points.Add(y);
            }

            public void Close()
            {
                Commands.Add(0);
            }

            public FigPath Build(bool evenOdd)
            {
                return new FigPath(Commands.ToArray(), Points.ToArray(), evenOdd);
            }
        }
    }
}
