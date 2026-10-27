using System;
using System.Collections.Generic;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// The path a text-on-path layer (<c>TEXT_PATH</c>) follows, read from its vector network the
    /// way figcraft reads it (<c>crates/format/src/fig/text_path.rs</c>): the network is one chain
    /// or cycle, one segment carries the start marker and <c>textPathStart.tValue</c> is the start
    /// point on it. Flattened to a polyline in layer space for placing glyphs.
    /// </summary>
    public sealed class FigTextPath
    {
        private const uint StartSegmentStyle = 0x01ffffff;
        private const int CurveSteps = 32;

        private readonly List<Vector2> points = new();
        private readonly List<float> lengths = new();

        public bool Closed { get; private set; }

        /// <summary>Where the text starts, 0–1 by length (already mirrored when flipped).</summary>
        public float Start { get; private set; }

        /// <summary>The text runs on the other side, from the end towards the start.</summary>
        public bool Flip { get; private set; }

        public float Length => lengths.Count > 0 ? lengths[lengths.Count - 1] : 0f;

        private struct Segment
        {
            public int A, B;
            public Vector2 TA, TB;
            public uint Style;
        }

        private struct Use
        {
            public int Segment;
            public bool Forward;
        }

        /// <summary>
        /// The path of network <paramref name="blob"/> (vertices in <c>normalizedSize</c> space, scaled by
        /// <paramref name="sx"/>, <paramref name="sy"/> into the layer), or null when it isn't a simple chain.
        /// </summary>
        public static FigTextPath FromNetwork(byte[] blob, float sx, float sy, float t, bool forward)
        {
            if (blob == null || blob.Length < 12 || !(sx > 0) || !(sy > 0) || float.IsNaN(t))
            {
                return null;
            }

            int pos = 0;
            uint nv = U32(blob, ref pos), ns = U32(blob, ref pos);
            U32(blob, ref pos);
            if (nv > 200000 || ns > 200000 || 12 + nv * 12L + ns * 28L > blob.Length || ns == 0)
            {
                return null;
            }

            var vertices = new Vector2[nv];
            for (int i = 0; i < nv; i++)
            {
                U32(blob, ref pos);
                vertices[i] = new Vector2(F32(blob, ref pos) * sx, F32(blob, ref pos) * sy);
            }

            var segments = new Segment[ns];
            int marked = -1, markers = 0;
            for (int i = 0; i < ns; i++)
            {
                var s = new Segment { Style = U32(blob, ref pos) };
                s.A = (int)U32(blob, ref pos);
                s.TA = new Vector2(F32(blob, ref pos) * sx, F32(blob, ref pos) * sy);
                s.B = (int)U32(blob, ref pos);
                s.TB = new Vector2(F32(blob, ref pos) * sx, F32(blob, ref pos) * sy);
                if (s.A < 0 || s.A >= nv || s.B < 0 || s.B >= nv || s.A == s.B)
                {
                    return null;
                }

                if (s.Style == StartSegmentStyle)
                {
                    marked = i;
                    markers++;
                }

                segments[i] = s;
            }

            if (markers != 1)
            {
                // No (or an ambiguous) start marker: start at the chain's first end.
                marked = -1;
            }

            List<Use> chain = OrderedChain(vertices.Length, segments, marked, out bool closed);
            if (chain == null)
            {
                return null;
            }

            var path = new FigTextPath { Closed = closed };
            float prefix = 0, partial = 0;
            t = Mathf.Clamp01(t);
            foreach (Use use in chain)
            {
                Segment s = segments[use.Segment];
                Vector2 p0 = vertices[s.A], p3 = vertices[s.B];
                bool line = s.TA == Vector2.zero && s.TB == Vector2.zero;
                Vector2 p1 = line ? p0 : p0 + s.TA, p2 = line ? p3 : p3 + s.TB;
                if (!use.Forward)
                {
                    Vector2 tmp = p0;
                    p0 = p3;
                    p3 = tmp;
                    tmp = p1;
                    p1 = p2;
                    p2 = tmp;
                }

                if (path.points.Count == 0)
                {
                    path.Add(p0);
                }

                float before = path.Length;
                int steps = line ? 1 : CurveSteps;
                for (int k = 1; k <= steps; k++)
                {
                    path.Add(Cubic(p0, p1, p2, p3, k / (float)steps));
                }

                if (use.Segment == marked)
                {
                    prefix = before;
                    // The marked segment runs a→b in the chain, so t is local to it as walked.
                    partial = line ? (p3 - p0).magnitude * t : CubicLength(p0, p1, p2, p3, t);
                }
            }

            if (path.Length <= 1e-6f)
            {
                return null;
            }

            float start = marked >= 0 ? Mathf.Clamp01((prefix + partial) / path.Length) : 0f;
            path.Flip = !forward;
            path.Start = path.Flip ? 1f - start : start;
            return path;
        }

        /// <summary>The point <paramref name="along"/> from the start (flipped: from the end) and the reading direction there.</summary>
        public bool At(float along, out Vector2 point, out Vector2 dir)
        {
            point = dir = default;
            float len = Length;
            if (len <= 1e-6f || float.IsNaN(along))
            {
                return false;
            }

            float s = Closed ? along - Mathf.Floor(along / len) * len : along;
            if (s < 0 || s > len)
            {
                return false;
            }

            if (Flip)
            {
                s = len - s;
            }

            int i = lengths.BinarySearch(s);
            if (i < 0)
            {
                i = ~i;
            }

            i = Mathf.Clamp(i, 1, points.Count - 1);
            Vector2 a = points[i - 1], b = points[i];
            float seg = lengths[i] - lengths[i - 1];
            float k = seg > 1e-9f ? (s - lengths[i - 1]) / seg : 0f;
            point = a + (b - a) * k;
            dir = (b - a).normalized;
            if (Flip)
            {
                dir = -dir;
            }

            return dir != Vector2.zero;
        }

        /// <summary>The reading direction at the point of the path nearest <paramref name="q"/>, and how far that is.</summary>
        public Vector2 DirectionNear(Vector2 q, out float distance)
        {
            distance = float.MaxValue;
            Vector2 best = Vector2.right;
            for (int i = 1; i < points.Count; i++)
            {
                Vector2 a = points[i - 1], ab = points[i] - a;
                float l2 = ab.sqrMagnitude;
                if (l2 <= 1e-12f)
                {
                    continue;
                }

                float k = Mathf.Clamp01(Vector2.Dot(q - a, ab) / l2);
                float d = (a + ab * k - q).magnitude;
                if (d < distance)
                {
                    distance = d;
                    best = ab / Mathf.Sqrt(l2);
                }
            }

            return Flip ? -best : best;
        }

        private void Add(Vector2 p)
        {
            lengths.Add(points.Count == 0 ? 0f : lengths[lengths.Count - 1] + (p - points[points.Count - 1]).magnitude);
            points.Add(p);
        }

        private static List<Use> OrderedChain(int vertexCount, Segment[] segments, int marked, out bool cycle)
        {
            cycle = false;
            var adjacency = new List<int>[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                adjacency[i] = new List<int>(2);
            }

            for (int i = 0; i < segments.Length; i++)
            {
                adjacency[segments[i].A].Add(i);
                adjacency[segments[i].B].Add(i);
            }

            int endpoint = -1, endpoints = 0;
            for (int v = 0; v < vertexCount; v++)
            {
                if (adjacency[v].Count > 2)
                {
                    return null;
                }

                if (adjacency[v].Count == 1)
                {
                    endpoints++;
                    if (endpoint < 0)
                    {
                        endpoint = v;
                    }
                }
            }

            if (endpoints != 0 && endpoints != 2)
            {
                return null;
            }

            cycle = endpoints == 0;
            var used = new bool[segments.Length];
            var chain = new List<Use>(segments.Length);
            int at;
            if (cycle)
            {
                int first = marked >= 0 ? marked : 0;
                chain.Add(new Use { Segment = first, Forward = true });
                used[first] = true;
                at = segments[first].B;
                while (at != segments[first].A)
                {
                    int next = Unused(adjacency[at], used);
                    if (next < 0 || chain.Count >= segments.Length)
                    {
                        return null;
                    }

                    used[next] = true;
                    bool forward = segments[next].A == at;
                    at = forward ? segments[next].B : segments[next].A;
                    chain.Add(new Use { Segment = next, Forward = forward });
                }
            }
            else
            {
                at = endpoint;
                for (int next = Unused(adjacency[at], used); next >= 0; next = Unused(adjacency[at], used))
                {
                    used[next] = true;
                    bool forward = segments[next].A == at;
                    at = forward ? segments[next].B : segments[next].A;
                    chain.Add(new Use { Segment = next, Forward = forward });
                }

                // Orient the chain so the marked segment runs a→b.
                int mi = chain.FindIndex(u => u.Segment == marked);
                if (mi >= 0 && !chain[mi].Forward)
                {
                    chain.Reverse();
                    for (int i = 0; i < chain.Count; i++)
                    {
                        chain[i] = new Use { Segment = chain[i].Segment, Forward = !chain[i].Forward };
                    }
                }
            }

            foreach (bool u in used)
            {
                if (!u)
                {
                    return null;
                }
            }

            return chain;
        }

        private static int Unused(List<int> edges, bool[] used)
        {
            foreach (int e in edges)
            {
                if (!used[e])
                {
                    return e;
                }
            }

            return -1;
        }

        private static Vector2 Cubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float u = 1 - t;
            return p0 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + p3 * (t * t * t);
        }

        private static float CubicLength(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float len = 0;
            Vector2 prev = p0;
            int steps = Mathf.Max(1, Mathf.CeilToInt(CurveSteps * t));
            for (int k = 1; k <= steps; k++)
            {
                Vector2 p = Cubic(p0, p1, p2, p3, t * k / steps);
                len += (p - prev).magnitude;
                prev = p;
            }

            return len;
        }

        private static uint U32(byte[] b, ref int pos)
        {
            uint v = (uint)(b[pos] | b[pos + 1] << 8 | b[pos + 2] << 16 | b[pos + 3] << 24);
            pos += 4;
            return v;
        }

        private static float F32(byte[] b, ref int pos)
        {
            float v = BitConverter.ToSingle(b, pos);
            pos += 4;
            return float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;
        }
    }
}
