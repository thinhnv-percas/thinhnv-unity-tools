using System;
using System.Collections.Generic;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// A coverage mask (0–255 per pixel) over a canvas, remembering which rows and columns were
    /// touched so clearing and compositing only visit those.
    /// </summary>
    public sealed class FigMask
    {
        public readonly int Width;
        public readonly int Height;
        public readonly byte[] A;
        public readonly int[] RowMin;
        public readonly int[] RowMax;
        public int YMin;
        public int YMax;

        public FigMask(int width, int height)
        {
            Width = width;
            Height = height;
            A = new byte[width * height];
            RowMin = new int[height];
            RowMax = new int[height];
            YMin = height;
            YMax = -1;
            for (int y = 0; y < height; y++)
            {
                RowMin[y] = width;
                RowMax[y] = -1;
            }
        }

        public bool IsEmpty => YMax < YMin;

        public void Clear()
        {
            for (int y = Math.Max(0, YMin); y <= Math.Min(Height - 1, YMax); y++)
            {
                if (RowMax[y] >= RowMin[y])
                {
                    Array.Clear(A, y * Width + RowMin[y], RowMax[y] - RowMin[y] + 1);
                }

                RowMin[y] = Width;
                RowMax[y] = -1;
            }

            YMin = Height;
            YMax = -1;
        }

        public void Touch(int y, int x0, int x1)
        {
            if (x1 < x0)
            {
                return;
            }

            if (x0 < RowMin[y])
            {
                RowMin[y] = x0;
            }

            if (x1 > RowMax[y])
            {
                RowMax[y] = x1;
            }

            if (y < YMin)
            {
                YMin = y;
            }

            if (y > YMax)
            {
                YMax = y;
            }
        }

        /// <summary>Make every pixel full (a clip that clips nothing).</summary>
        public void Fill()
        {
            for (int i = 0; i < A.Length; i++)
            {
                A[i] = 255;
            }

            for (int y = 0; y < Height; y++)
            {
                RowMin[y] = 0;
                RowMax[y] = Width - 1;
            }

            YMin = 0;
            YMax = Height - 1;
        }

        public void CopyFrom(FigMask other)
        {
            Clear();
            Buffer.BlockCopy(other.A, 0, A, 0, A.Length);
            Array.Copy(other.RowMin, RowMin, Height);
            Array.Copy(other.RowMax, RowMax, Height);
            YMin = other.YMin;
            YMax = other.YMax;
        }

        /// <summary>this = this × (1 − other) (what lies outside the other coverage).</summary>
        public void MultiplyByInverse(FigMask other)
        {
            for (int y = Math.Max(0, YMin); y <= Math.Min(Height - 1, YMax); y++)
            {
                int x0 = Math.Max(RowMin[y], other.RowMin[y]), x1 = Math.Min(RowMax[y], other.RowMax[y]);
                int row = y * Width;
                for (int x = x0; x <= x1; x++)
                {
                    int i = row + x;
                    A[i] = (byte)((A[i] * (255 - other.A[i]) + 127) / 255);
                }
            }
        }

        /// <summary>this = this × other (intersection of coverages).</summary>
        public void MultiplyBy(FigMask other)
        {
            for (int y = Math.Max(0, YMin); y <= Math.Min(Height - 1, YMax); y++)
            {
                int x0 = RowMin[y], x1 = RowMax[y];
                if (x1 < x0)
                {
                    continue;
                }

                int row = y * Width;
                int ox0 = other.RowMin[y], ox1 = other.RowMax[y];
                for (int x = x0; x <= x1; x++)
                {
                    int i = row + x;
                    A[i] = x < ox0 || x > ox1 ? (byte)0 : (byte)((A[i] * other.A[i] + 127) / 255);
                }

                RowMin[y] = Math.Max(x0, ox0);
                RowMax[y] = Math.Min(x1, ox1);
            }
        }
    }

    /// <summary>Scanline rasterizer, polyline stroker and blur used by the board renderer.</summary>
    public static class FigRaster
    {
        private const int SubSamples = 4;

        private sealed class Scratch
        {
            public float[] EdgeX0 = new float[256], EdgeY0 = new float[256], EdgeX1 = new float[256], EdgeY1 = new float[256];
            public int[] EdgeDir = new int[256];
            public int[] Order = new int[256];
            public int[] Active = new int[256];
            public float[] CrossX = new float[64];
            public int[] CrossDir = new int[64];
            public float[] Row = new float[256];
            public int EdgeCount;
        }

        [ThreadStatic] private static Scratch scratch;

        /// <summary>
        /// Add the coverage of <paramref name="contours"/> (pixel coordinates in the mask, every
        /// contour closed) into <paramref name="mask"/> with max(), so several paths union. 4×
        /// vertical supersampling, exact horizontal coverage.
        /// </summary>
        public static void Fill(List<Vector2[]> contours, bool evenOdd, FigMask mask)
        {
            Scratch s = scratch ??= new Scratch();
            s.EdgeCount = 0;
            float yTop = float.MaxValue, yBottom = float.MinValue;
            foreach (Vector2[] c in contours)
            {
                int n = c.Length;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = c[i];
                    Vector2 b = c[i + 1 == n ? 0 : i + 1];
                    if (a.y == b.y)
                    {
                        continue;
                    }

                    AddEdge(s, a, b);
                    yTop = Mathf.Min(yTop, Mathf.Min(a.y, b.y));
                    yBottom = Mathf.Max(yBottom, Mathf.Max(a.y, b.y));
                }
            }

            int count = s.EdgeCount;
            if (count == 0)
            {
                return;
            }

            int y0 = Math.Max(0, (int)Math.Floor(yTop));
            int y1 = Math.Min(mask.Height - 1, (int)Math.Ceiling(yBottom));
            if (y1 < y0)
            {
                return;
            }

            // Edges sorted by top y, swept with an active list.
            int[] order = s.Order;
            for (int i = 0; i < count; i++)
            {
                order[i] = i;
            }

            float[] ey0 = s.EdgeY0;
            Array.Sort(order, 0, count, Comparer<int>.Create((p, q) => ey0[p].CompareTo(ey0[q])));
            int width = mask.Width;
            if (s.Row.Length < width + 1)
            {
                s.Row = new float[width + 1];
            }

            float[] row = s.Row;
            int next = 0;
            int activeCount = 0;
            const float weight = 1f / SubSamples;

            for (int y = y0; y <= y1; y++)
            {
                int minX = width, maxX = -1;
                for (int sub = 0; sub < SubSamples; sub++)
                {
                    float sy = y + (sub + 0.5f) / SubSamples;
                    while (next < count && ey0[order[next]] <= sy)
                    {
                        if (activeCount == s.Active.Length)
                        {
                            Array.Resize(ref s.Active, activeCount * 2);
                        }

                        s.Active[activeCount++] = order[next++];
                    }

                    int crossings = 0;
                    for (int k = activeCount - 1; k >= 0; k--)
                    {
                        int e = s.Active[k];
                        if (s.EdgeY1[e] <= sy)
                        {
                            s.Active[k] = s.Active[--activeCount];
                            continue;
                        }

                        if (s.EdgeY0[e] > sy)
                        {
                            continue;
                        }

                        float x = s.EdgeX0[e] + (sy - s.EdgeY0[e]) * (s.EdgeX1[e] - s.EdgeX0[e]) / (s.EdgeY1[e] - s.EdgeY0[e]);
                        if (crossings == s.CrossX.Length)
                        {
                            Array.Resize(ref s.CrossX, crossings * 2);
                            Array.Resize(ref s.CrossDir, crossings * 2);
                        }

                        // Insertion sort: crossings per scanline are few.
                        int j = crossings++;
                        while (j > 0 && s.CrossX[j - 1] > x)
                        {
                            s.CrossX[j] = s.CrossX[j - 1];
                            s.CrossDir[j] = s.CrossDir[j - 1];
                            j--;
                        }

                        s.CrossX[j] = x;
                        s.CrossDir[j] = s.EdgeDir[e];
                    }

                    int winding = 0;
                    for (int k = 0; k + 1 < crossings; k++)
                    {
                        winding += evenOdd ? 1 : s.CrossDir[k];
                        bool inside = evenOdd ? (winding & 1) != 0 : winding != 0;
                        if (!inside)
                        {
                            continue;
                        }

                        float xa = Mathf.Max(0, s.CrossX[k]);
                        float xb = Mathf.Min(width, s.CrossX[k + 1]);
                        if (xb <= xa)
                        {
                            continue;
                        }

                        int ia = (int)xa, ib = (int)xb;
                        if (ia < minX)
                        {
                            minX = ia;
                        }

                        int last = ib < width ? ib : width - 1;
                        if (last > maxX)
                        {
                            maxX = last;
                        }

                        if (ia == ib)
                        {
                            row[ia] += (xb - xa) * weight;
                            continue;
                        }

                        row[ia] += (ia + 1 - xa) * weight;
                        for (int x = ia + 1; x < ib; x++)
                        {
                            row[x] += weight;
                        }

                        if (ib < width)
                        {
                            row[ib] += (xb - ib) * weight;
                        }
                    }
                }

                if (maxX < minX)
                {
                    continue;
                }

                int offset = y * width;
                byte[] a = mask.A;
                for (int x = minX; x <= maxX; x++)
                {
                    int v = (int)(row[x] * 255f + 0.5f);
                    row[x] = 0;
                    if (v > 255)
                    {
                        v = 255;
                    }

                    if (v > a[offset + x])
                    {
                        a[offset + x] = (byte)v;
                    }
                }

                mask.Touch(y, minX, maxX);
            }
        }

        private static void AddEdge(Scratch s, Vector2 a, Vector2 b)
        {
            int i = s.EdgeCount;
            if (i == s.EdgeX0.Length)
            {
                int n = i * 2;
                Array.Resize(ref s.EdgeX0, n);
                Array.Resize(ref s.EdgeY0, n);
                Array.Resize(ref s.EdgeX1, n);
                Array.Resize(ref s.EdgeY1, n);
                Array.Resize(ref s.EdgeDir, n);
                Array.Resize(ref s.Order, n);
            }

            if (a.y < b.y)
            {
                s.EdgeX0[i] = a.x;
                s.EdgeY0[i] = a.y;
                s.EdgeX1[i] = b.x;
                s.EdgeY1[i] = b.y;
                s.EdgeDir[i] = 1;
            }
            else
            {
                s.EdgeX0[i] = b.x;
                s.EdgeY0[i] = b.y;
                s.EdgeX1[i] = a.x;
                s.EdgeY1[i] = a.y;
                s.EdgeDir[i] = -1;
            }

            s.EdgeCount = i + 1;
        }

        // ─── Stroking ────────────────────────────────────────────────────────────

        /// <summary>
        /// Polygons covering a <paramref name="width"/>-wide stroke along <paramref name="contours"/>
        /// (closed), all wound the same way so a non-zero fill unions them: a quad per segment and a
        /// round-ish joint at every vertex.
        /// </summary>
        public static List<Vector2[]> Stroke(List<Vector2[]> contours, float width)
        {
            var polys = new List<Vector2[]>();
            float half = width * 0.5f;
            foreach (Vector2[] c in contours)
            {
                int n = c.Length;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = c[i];
                    Vector2 b = c[(i + 1) % n];
                    Vector2 d = b - a;
                    float len = d.magnitude;
                    if (len < 1e-4f)
                    {
                        continue;
                    }

                    Vector2 nrm = new Vector2(-d.y, d.x) / len * half;
                    polys.Add(Wound(new[] { a + nrm, b + nrm, b - nrm, a - nrm }));
                    if (half > 0.75f)
                    {
                        var joint = new Vector2[8];
                        for (int k = 0; k < 8; k++)
                        {
                            float ang = k * Mathf.PI / 4f;
                            joint[k] = a + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * half;
                        }

                        polys.Add(Wound(joint));
                    }
                }
            }

            return polys;
        }

        /// <summary>The polygon wound clockwise in screen space (positive signed area with y down).</summary>
        private static Vector2[] Wound(Vector2[] poly)
        {
            float area = 0;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                area += (poly[j].x * poly[i].y) - (poly[i].x * poly[j].y);
            }

            if (area < 0)
            {
                Array.Reverse(poly);
            }

            return poly;
        }

        // ─── Blur and spread ─────────────────────────────────────────────────────

        /// <summary>Box radii of three passes approximating a Gaussian of <paramref name="sigma"/>.</summary>
        public static int[] BoxesForGauss(float sigma)
        {
            float wIdeal = Mathf.Sqrt(12f * sigma * sigma / 3f + 1f);
            int wl = (int)Mathf.Floor(wIdeal);
            if (wl % 2 == 0)
            {
                wl--;
            }

            int wu = wl + 2;
            float mIdeal = (12f * sigma * sigma - 3 * wl * wl - 4 * 3 * wl - 3 * 3) / (-4f * wl - 4f);
            int m = Mathf.RoundToInt(mIdeal);
            var sizes = new int[3];
            for (int i = 0; i < 3; i++)
            {
                sizes[i] = ((i < m ? wl : wu) - 1) / 2;
            }

            return sizes;
        }

        /// <summary>Gaussian-ish blur (three box passes each way) of <paramref name="channels"/> interleaved floats per pixel.</summary>
        public static void Blur(float[] data, int width, int height, int channels, float sigma)
        {
            if (sigma < 0.3f)
            {
                return;
            }

            int[] boxes = BoxesForGauss(sigma);
            float[] tmp = ScratchBuffer(ref blurScratch, width * height * channels);
            foreach (int r in boxes)
            {
                if (r <= 0)
                {
                    continue;
                }

                BoxH(data, tmp, width, height, channels, r);
                BoxV(tmp, data, width, height, channels, r);
            }
        }

        private static void BoxH(float[] src, float[] dst, int w, int h, int ch, int r)
        {
            float inv = 1f / (r + r + 1);
            for (int y = 0; y < h; y++)
            {
                int row = y * w * ch;
                for (int c = 0; c < ch; c++)
                {
                    float acc = 0;
                    for (int x = -r; x <= r; x++)
                    {
                        acc += src[row + Math.Max(0, Math.Min(w - 1, x)) * ch + c] * (x < 0 || x >= w ? 0 : 1);
                    }

                    for (int x = 0; x < w; x++)
                    {
                        dst[row + x * ch + c] = acc * inv;
                        int add = x + r + 1, sub = x - r;
                        if (add < w)
                        {
                            acc += src[row + add * ch + c];
                        }

                        if (sub >= 0)
                        {
                            acc -= src[row + sub * ch + c];
                        }
                    }
                }
            }
        }

        /// <summary>Vertical box pass, row by row (a running sum per column) so memory is read in order.</summary>
        private static void BoxV(float[] src, float[] dst, int w, int h, int ch, int r)
        {
            float inv = 1f / (r + r + 1);
            int stride = w * ch;
            float[] acc = ScratchBuffer(ref columnScratch, stride);
            Array.Clear(acc, 0, stride);
            for (int y = 0; y <= Math.Min(r, h - 1); y++)
            {
                int row = y * stride;
                for (int i = 0; i < stride; i++)
                {
                    acc[i] += src[row + i];
                }
            }

            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int i = 0; i < stride; i++)
                {
                    dst[row + i] = acc[i] * inv;
                }

                int add = y + r + 1, sub = y - r;
                if (add < h)
                {
                    int ar = add * stride;
                    for (int i = 0; i < stride; i++)
                    {
                        acc[i] += src[ar + i];
                    }
                }

                if (sub >= 0)
                {
                    int sr = sub * stride;
                    for (int i = 0; i < stride; i++)
                    {
                        acc[i] -= src[sr + i];
                    }
                }
            }
        }

        [ThreadStatic] private static float[] blurScratch;
        [ThreadStatic] private static float[] columnScratch;
        [ThreadStatic] private static float[] spreadScratch;

        /// <summary>A per-thread buffer of at least <paramref name="n"/> floats (contents undefined).</summary>
        private static float[] ScratchBuffer(ref float[] buffer, int n)
        {
            if (buffer == null || buffer.Length < n)
            {
                buffer = new float[Math.Max(n, buffer == null ? 0 : buffer.Length * 3 / 2)];
            }

            return buffer;
        }

        /// <summary>Grow (r &gt; 0) or shrink (r &lt; 0) an alpha field by |r| pixels with a separable max / min filter.</summary>
        public static void Spread(float[] alpha, int width, int height, int r)
        {
            if (r == 0)
            {
                return;
            }

            bool grow = r > 0;
            r = Math.Abs(r);
            float[] tmp = ScratchBuffer(ref spreadScratch, width * height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float m = grow ? 0f : 1f;
                    for (int k = Math.Max(0, x - r); k <= Math.Min(width - 1, x + r); k++)
                    {
                        float v = alpha[y * width + k];
                        m = grow ? Math.Max(m, v) : Math.Min(m, v);
                    }

                    tmp[y * width + x] = m;
                }
            }

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    float m = grow ? 0f : 1f;
                    for (int k = Math.Max(0, y - r); k <= Math.Min(height - 1, y + r); k++)
                    {
                        float v = tmp[k * width + x];
                        m = grow ? Math.Max(m, v) : Math.Min(m, v);
                    }

                    alpha[y * width + x] = m;
                }
            }
        }
    }
}
