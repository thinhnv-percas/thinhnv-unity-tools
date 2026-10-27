using System;
using System.Collections.Generic;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>A 2D affine transform in Figma's layout: x' = A x + B y + C, y' = D x + E y + F.</summary>
    public struct FigAffine
    {
        public float A, B, C, D, E, F;

        public static readonly FigAffine Identity = new FigAffine { A = 1, E = 1 };

        public static FigAffine FromMatrix(KiwiValue m)
        {
            if (m == null)
            {
                return Identity;
            }

            return new FigAffine
            {
                A = m.Get("m00")?.AsFloat ?? 1,
                B = m.Get("m01")?.AsFloat ?? 0,
                C = m.Get("m02")?.AsFloat ?? 0,
                D = m.Get("m10")?.AsFloat ?? 0,
                E = m.Get("m11")?.AsFloat ?? 1,
                F = m.Get("m12")?.AsFloat ?? 0,
            };
        }

        public Vector2 Apply(float x, float y) => new Vector2(A * x + B * y + C, D * x + E * y + F);

        public Vector2 Apply(Vector2 p) => Apply(p.x, p.y);

        /// <summary>The linear part only (for deltas).</summary>
        public Vector2 ApplyVector(Vector2 v) => new Vector2(A * v.x + B * v.y, D * v.x + E * v.y);

        /// <summary>This transform after <paramref name="local"/> (a child's transform inside this one).</summary>
        public FigAffine Then(FigAffine local)
        {
            return new FigAffine
            {
                A = A * local.A + B * local.D,
                B = A * local.B + B * local.E,
                C = A * local.C + B * local.F + C,
                D = D * local.A + E * local.D,
                E = D * local.B + E * local.E,
                F = D * local.C + E * local.F + F,
            };
        }

        public FigAffine Inverse()
        {
            float det = A * E - B * D;
            if (Mathf.Abs(det) < 1e-12f)
            {
                return Identity;
            }

            float inv = 1f / det;
            return new FigAffine
            {
                A = E * inv,
                B = -B * inv,
                C = (B * F - E * C) * inv,
                D = -D * inv,
                E = A * inv,
                F = (D * C - A * F) * inv,
            };
        }

        public bool AxisAligned => Mathf.Abs(B) < 1e-4f && Mathf.Abs(D) < 1e-4f && A > 0 && E > 0;

        /// <summary>Average scale (square root of the determinant's magnitude).</summary>
        public float Scale => Mathf.Sqrt(Mathf.Abs(A * E - B * D));
    }

    /// <summary>One flattened contour of a geometry blob, in the layer's local space.</summary>
    public sealed class FigContour
    {
        public Vector2[] Points;
        public bool Closed;
    }

    /// <summary>
    /// Geometry helpers for the board: Figma path blobs, polygon builders, clipping and an
    /// anti-aliased scanline rasterizer (non-zero / even-odd) used to cache vector layers as textures.
    /// </summary>
    public static class FigGeometry
    {
        // ─── Path blobs ──────────────────────────────────────────────────────────

        /// <summary>
        /// Decode a geometry blob (<c>fillGeometry[i].commandsBlob</c>): commands <c>0</c> close,
        /// <c>1</c> move, <c>2</c> line, <c>3</c> quadratic, <c>4</c> cubic, each followed by its
        /// points as little-endian <c>f32</c> pairs. Curves are flattened into
        /// <paramref name="segmentsPerCurve"/> segments. Returns null on malformed data.
        /// </summary>
        public static List<FigContour> DecodePath(byte[] blob, int segmentsPerCurve = 12)
        {
            if (blob == null)
            {
                return null;
            }

            var r = new PathReader(blob);
            var p = new Vector2[3];
            while (r.Pos < blob.Length)
            {
                byte cmd = blob[r.Pos++];
                switch (cmd)
                {
                    case 0:
                        if (r.Points.Count > 0)
                        {
                            r.Finish(true);
                        }

                        r.Current = r.Start;
                        break;
                    case 1:
                        if (!r.Read(1, p))
                        {
                            return null;
                        }

                        r.Finish(false);
                        r.Current = r.Start = p[0];
                        r.Points.Add(r.Current);
                        break;
                    case 2:
                        if (!r.Read(1, p))
                        {
                            return null;
                        }

                        r.BeginSegment();
                        r.Current = p[0];
                        r.Points.Add(r.Current);
                        break;
                    case 3:
                        if (!r.Read(2, p))
                        {
                            return null;
                        }

                        r.BeginSegment();
                        for (int i = 1; i <= segmentsPerCurve; i++)
                        {
                            float t = (float)i / segmentsPerCurve;
                            float u = 1 - t;
                            r.Points.Add(u * u * r.Current + 2 * u * t * p[0] + t * t * p[1]);
                        }

                        r.Current = p[1];
                        break;
                    case 4:
                        if (!r.Read(3, p))
                        {
                            return null;
                        }

                        r.BeginSegment();
                        for (int i = 1; i <= segmentsPerCurve; i++)
                        {
                            float t = (float)i / segmentsPerCurve;
                            float u = 1 - t;
                            r.Points.Add(u * u * u * r.Current + 3 * u * u * t * p[0] + 3 * u * t * t * p[1] + t * t * t * p[2]);
                        }

                        r.Current = p[2];
                        break;
                    default:
                        return null;
                }
            }

            r.Finish(false);
            return r.Contours;
        }

        private sealed class PathReader
        {
            public readonly List<FigContour> Contours = new();
            public readonly List<Vector2> Points = new();
            public Vector2 Current;
            public Vector2 Start;
            public int Pos;
            private readonly byte[] blob;

            public PathReader(byte[] blob)
            {
                this.blob = blob;
            }

            /// <summary>A segment without a preceding move starts at the current point.</summary>
            public void BeginSegment()
            {
                if (Points.Count == 0)
                {
                    Points.Add(Current);
                }
            }

            public void Finish(bool closed)
            {
                if (Points.Count >= 2)
                {
                    Contours.Add(new FigContour { Points = Points.ToArray(), Closed = closed });
                }

                Points.Clear();
            }

            public bool Read(int count, Vector2[] into)
            {
                if (Pos + count * 8 > blob.Length)
                {
                    return false;
                }

                for (int i = 0; i < count; i++)
                {
                    float x = BitConverter.ToSingle(blob, Pos);
                    float y = BitConverter.ToSingle(blob, Pos + 4);
                    Pos += 8;
                    if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y))
                    {
                        return false;
                    }

                    into[i] = new Vector2(x, y);
                }

                return true;
            }
        }

        public static Rect Bounds(List<FigContour> contours)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (FigContour c in contours)
            {
                foreach (Vector2 v in c.Points)
                {
                    x0 = Mathf.Min(x0, v.x);
                    y0 = Mathf.Min(y0, v.y);
                    x1 = Mathf.Max(x1, v.x);
                    y1 = Mathf.Max(y1, v.y);
                }
            }

            return x0 > x1 ? new Rect(0, 0, 0, 0) : Rect.MinMaxRect(x0, y0, x1, y1);
        }

        // ─── Shapes ──────────────────────────────────────────────────────────────

        /// <summary>A rectangle with per-corner radii (top-left, top-right, bottom-right, bottom-left), clockwise.</summary>
        public static Vector2[] RoundedRect(float w, float h, float tl, float tr, float br, float bl, int segmentsPerCorner = 8)
        {
            float max = Mathf.Max(0, Mathf.Min(w, h) * 0.5f);
            tl = Mathf.Clamp(tl, 0, max);
            tr = Mathf.Clamp(tr, 0, max);
            br = Mathf.Clamp(br, 0, max);
            bl = Mathf.Clamp(bl, 0, max);
            if (tl <= 0 && tr <= 0 && br <= 0 && bl <= 0)
            {
                return new[] { new Vector2(0, 0), new Vector2(w, 0), new Vector2(w, h), new Vector2(0, h) };
            }

            var pts = new List<Vector2>(4 * (segmentsPerCorner + 1));
            Corner(pts, tl, tl, tl, Mathf.PI, segmentsPerCorner);
            Corner(pts, w - tr, tr, tr, 1.5f * Mathf.PI, segmentsPerCorner);
            Corner(pts, w - br, h - br, br, 0, segmentsPerCorner);
            Corner(pts, bl, h - bl, bl, 0.5f * Mathf.PI, segmentsPerCorner);
            return pts.ToArray();
        }

        private static void Corner(List<Vector2> pts, float cx, float cy, float r, float startAngle, int segments)
        {
            if (r <= 0)
            {
                // A sharp corner: the arc centre is the corner itself.
                pts.Add(new Vector2(cx, cy));
                return;
            }

            for (int i = 0; i <= segments; i++)
            {
                float a = startAngle + (Mathf.PI * 0.5f) * i / segments;
                pts.Add(new Vector2(cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r));
            }
        }

        public static Vector2[] Ellipse(float w, float h, int segments = 48)
        {
            var pts = new Vector2[segments];
            for (int i = 0; i < segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                pts[i] = new Vector2(w * 0.5f * (1 + Mathf.Cos(a)), h * 0.5f * (1 + Mathf.Sin(a)));
            }

            return pts;
        }

        // ─── Tests and clipping ──────────────────────────────────────────────────

        /// <summary>Even-odd point-in-polygon.</summary>
        public static bool Contains(Vector2[] polygon, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        /// <summary>
        /// Clip a convex polygon to <paramref name="clip"/> (Sutherland–Hodgman). Writes into
        /// <paramref name="output"/> (cleared first) and uses <paramref name="scratch"/> as a buffer.
        /// </summary>
        public static void ClipToRect(List<Vector2> input, Rect clip, List<Vector2> output, List<Vector2> scratch)
        {
            output.Clear();
            output.AddRange(input);
            ClipEdge(output, scratch, 0, clip.xMin, false);
            ClipEdge(output, scratch, 0, clip.xMax, true);
            ClipEdge(output, scratch, 1, clip.yMin, false);
            ClipEdge(output, scratch, 1, clip.yMax, true);
        }

        private static void ClipEdge(List<Vector2> poly, List<Vector2> scratch, int axis, float value, bool keepBelow)
        {
            if (poly.Count == 0)
            {
                return;
            }

            scratch.Clear();
            Vector2 prev = poly[poly.Count - 1];
            bool prevIn = Inside(prev, axis, value, keepBelow);
            foreach (Vector2 cur in poly)
            {
                bool curIn = Inside(cur, axis, value, keepBelow);
                if (curIn != prevIn)
                {
                    float a = axis == 0 ? prev.x : prev.y;
                    float b = axis == 0 ? cur.x : cur.y;
                    float t = (value - a) / (b - a);
                    scratch.Add(prev + (cur - prev) * t);
                }

                if (curIn)
                {
                    scratch.Add(cur);
                }

                prev = cur;
                prevIn = curIn;
            }

            poly.Clear();
            poly.AddRange(scratch);
        }

        private static bool Inside(Vector2 p, int axis, float value, bool keepBelow)
        {
            float v = axis == 0 ? p.x : p.y;
            return keepBelow ? v <= value : v >= value;
        }

        public static Rect Intersect(Rect a, Rect b)
        {
            float x0 = Mathf.Max(a.xMin, b.xMin);
            float y0 = Mathf.Max(a.yMin, b.yMin);
            float x1 = Mathf.Min(a.xMax, b.xMax);
            float y1 = Mathf.Min(a.yMax, b.yMax);
            return x1 <= x0 || y1 <= y0 ? new Rect(x0, y0, 0, 0) : Rect.MinMaxRect(x0, y0, x1, y1);
        }

        public static Rect Union(Rect a, Rect b)
        {
            return Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
        }

        public static Rect TransformBounds(FigAffine t, Rect local)
        {
            Vector2 a = t.Apply(local.xMin, local.yMin);
            Vector2 b = t.Apply(local.xMax, local.yMin);
            Vector2 c = t.Apply(local.xMax, local.yMax);
            Vector2 d = t.Apply(local.xMin, local.yMax);
            return Rect.MinMaxRect(
                Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x)),
                Mathf.Min(Mathf.Min(a.y, b.y), Mathf.Min(c.y, d.y)),
                Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x)),
                Mathf.Max(Mathf.Max(a.y, b.y), Mathf.Max(c.y, d.y)));
        }

        // ─── Rasterizer ──────────────────────────────────────────────────────────

        private const int SubSamples = 4;

        private struct Edge
        {
            public float X0, Y0, X1, Y1;
            public int Dir;
        }

        /// <summary>
        /// Coverage (0–255) of <paramref name="contours"/> mapped through <paramref name="toPixels"/>
        /// into a <paramref name="width"/> × <paramref name="height"/> grid, row 0 at the top. Every
        /// contour is treated as closed. 4× vertical supersampling, exact horizontal coverage.
        /// <paramref name="into"/> accumulates with max(), so several paths can share one mask.
        /// </summary>
        public static void Rasterize(List<FigContour> contours, FigAffine toPixels, int width, int height, bool evenOdd, byte[] into)
        {
            var edges = new List<Edge>();
            foreach (FigContour c in contours)
            {
                int n = c.Points.Length;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = toPixels.Apply(c.Points[i]);
                    Vector2 b = toPixels.Apply(c.Points[(i + 1) % n]);
                    if (Mathf.Abs(a.y - b.y) < 1e-6f)
                    {
                        continue;
                    }

                    edges.Add(a.y < b.y
                        ? new Edge { X0 = a.x, Y0 = a.y, X1 = b.x, Y1 = b.y, Dir = 1 }
                        : new Edge { X0 = b.x, Y0 = b.y, X1 = a.x, Y1 = a.y, Dir = -1 });
                }
            }

            if (edges.Count == 0)
            {
                return;
            }

            edges.Sort((p, q) => p.Y0.CompareTo(q.Y0));
            var active = new List<int>();
            var crossings = new List<KeyValuePair<float, int>>();
            var row = new float[width];
            int next = 0;
            const float weight = 1f / SubSamples;

            for (int y = 0; y < height; y++)
            {
                Array.Clear(row, 0, width);
                bool any = false;
                for (int s = 0; s < SubSamples; s++)
                {
                    float sy = y + (s + 0.5f) / SubSamples;
                    while (next < edges.Count && edges[next].Y0 <= sy)
                    {
                        active.Add(next++);
                    }

                    crossings.Clear();
                    for (int i = active.Count - 1; i >= 0; i--)
                    {
                        Edge e = edges[active[i]];
                        if (e.Y1 <= sy)
                        {
                            active.RemoveAt(i);
                            continue;
                        }

                        if (e.Y0 > sy)
                        {
                            continue;
                        }

                        float x = e.X0 + (sy - e.Y0) * (e.X1 - e.X0) / (e.Y1 - e.Y0);
                        crossings.Add(new KeyValuePair<float, int>(x, e.Dir));
                    }

                    if (crossings.Count < 2)
                    {
                        continue;
                    }

                    crossings.Sort((p, q) => p.Key.CompareTo(q.Key));
                    int winding = 0;
                    for (int i = 0; i < crossings.Count - 1; i++)
                    {
                        winding += evenOdd ? 1 : crossings[i].Value;
                        bool inside = evenOdd ? (winding & 1) != 0 : winding != 0;
                        if (inside)
                        {
                            AddSpan(row, width, crossings[i].Key, crossings[i + 1].Key, weight);
                            any = true;
                        }
                    }
                }

                if (!any)
                {
                    continue;
                }

                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    int v = (int)(row[x] * 255f + 0.5f);
                    if (v > 255)
                    {
                        v = 255;
                    }

                    if (v > into[offset + x])
                    {
                        into[offset + x] = (byte)v;
                    }
                }
            }
        }

        private static void AddSpan(float[] row, int width, float xa, float xb, float weight)
        {
            if (xb <= 0 || xa >= width || xb <= xa)
            {
                return;
            }

            xa = Mathf.Max(0, xa);
            xb = Mathf.Min(width, xb);
            int ia = (int)xa;
            int ib = (int)xb;
            if (ia == ib)
            {
                row[ia] += (xb - xa) * weight;
                return;
            }

            row[ia] += (ia + 1 - xa) * weight;
            for (int i = ia + 1; i < ib; i++)
            {
                row[i] += weight;
            }

            if (ib < width)
            {
                row[ib] += (xb - ib) * weight;
            }
        }
    }
}
