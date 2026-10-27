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

    /// <summary>Rectangle and polygon helpers shared by the scene, the renderer and the board.</summary>
    public static class FigGeometry
    {
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
    }
}
