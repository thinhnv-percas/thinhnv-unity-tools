using System;
using System.Collections.Generic;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// Software renderer for a <see cref="FigScene"/> snapshot: draws any rectangle of the page, at
    /// any zoom, into premultiplied RGBA. It only reads immutable scene data and decoded images, so
    /// one instance per worker thread renders tiles in parallel.
    ///
    /// Covers solid, linear / radial / angular / diamond gradient and image fills (fill, fit,
    /// stretch, tile; mip-mapped sampling), blend modes, strokes (the file's outlines, or inside /
    /// centre / outside rings), glyph outlines, clipping to the frame shape (rounded corners), masks,
    /// boolean results, isolated group opacity and effects (drop / inner shadow, layer and
    /// background blur).
    /// </summary>
    public sealed class FigRenderer
    {
        private const float Tolerance = 0.2f;
        private const int MaxMargin = 512;

        /// <summary>Decoded image by hash, or null while it isn't decoded (it is then listed in <see cref="Missing"/>).</summary>
        public Func<string, FigImageData> Images;

        public readonly HashSet<string> Missing = new();

        private float scale;
        private HashSet<int> excluded;
        private readonly List<Vector2[]> contours = new();
        private const int MaxPooledFloats = 3 << 20;
        private const int MaxPooledMaskBytes = 6 << 20;

        // Tiles render the same few buffer sizes over and over: reuse them instead of feeding the
        // garbage collector a megabyte or more per tile (its pauses stall the editor while zooming).
        private readonly Dictionary<int, Stack<float[]>> floatPool = new();
        private readonly Dictionary<long, Stack<FigMask>> maskPool = new();
        private int pooledFloats;
        private int pooledMaskBytes;

        private sealed class Canvas
        {
            public int OX, OY, W, H;
            public float[] P;
        }

        private Canvas NewCanvas(int ox, int oy, int w, int h)
        {
            return new Canvas { OX = ox, OY = oy, W = w, H = h, P = RentFloats(w * h * 4, true) };
        }

        private void Free(Canvas c)
        {
            ReturnFloats(c.P);
            c.P = null;
        }

        /// <summary>A float array of exactly <paramref name="n"/> elements, zeroed when <paramref name="clear"/>.</summary>
        private float[] RentFloats(int n, bool clear)
        {
            if (floatPool.TryGetValue(n, out Stack<float[]> stack) && stack.Count > 0)
            {
                float[] a = stack.Pop();
                pooledFloats -= n;
                if (clear)
                {
                    Array.Clear(a, 0, n);
                }

                return a;
            }

            return new float[n];
        }

        private void ReturnFloats(float[] a)
        {
            if (a == null || pooledFloats + a.Length > MaxPooledFloats)
            {
                return;
            }

            if (!floatPool.TryGetValue(a.Length, out Stack<float[]> stack))
            {
                floatPool[a.Length] = stack = new Stack<float[]>();
            }

            stack.Push(a);
            pooledFloats += a.Length;
        }

        /// <summary>
        /// Render the page rectangle <paramref name="x"/>, <paramref name="y"/>, <paramref name="w"/> ×
        /// <paramref name="h"/> (pixels at <paramref name="zoom"/>, i.e. world × zoom) as top-down RGBA.
        /// <paramref name="apron"/> renders that many extra pixels around it (for background blur near the edges);
        /// <paramref name="exclude"/> skips those subtrees (the selection being dragged).
        /// </summary>
        public Color32[] Render(FigSceneNode[] nodes, Color background, float zoom, int x, int y, int w, int h, int apron = 0, HashSet<int> exclude = null,
            Color32[] into = null, bool bottomUp = false)
        {
            scale = zoom;
            excluded = exclude;
            Missing.Clear();
            Canvas canvas = NewCanvas(x - apron, y - apron, w + 2 * apron, h + 2 * apron);
            float[] p = canvas.P;
            for (int i = 0; i < canvas.W * canvas.H * 4; i += 4)
            {
                p[i] = background.r;
                p[i + 1] = background.g;
                p[i + 2] = background.b;
                p[i + 3] = 1f;
            }

            DrawRange(nodes, 0, nodes.Length, canvas, null);
            Color32[] output = into != null && into.Length == w * h ? into : new Color32[w * h];
            for (int row = 0; row < h; row++)
            {
                int src = ((row + apron) * canvas.W + apron) * 4;
                // Textures store rows bottom-up.
                int dst = (bottomUp ? h - 1 - row : row) * w;
                for (int col = 0; col < w; col++, src += 4)
                {
                    float a = p[src + 3];
                    output[dst + col] = a <= 0
                        ? new Color32(0, 0, 0, 0)
                        : new Color32(ToByte(p[src] / a), ToByte(p[src + 1] / a), ToByte(p[src + 2] / a), ToByte(a));
                }
            }

            Free(canvas);
            return output;
        }

        /// <summary>Render only the subtrees at <paramref name="roots"/> on transparent (the board's lifted drag layer).</summary>
        public Color32[] RenderOnly(FigSceneNode[] nodes, IList<int> roots, float zoom, int x, int y, int w, int h)
        {
            scale = zoom;
            excluded = null;
            Missing.Clear();
            Canvas canvas = NewCanvas(x, y, w, h);
            foreach (int r in roots)
            {
                DrawRange(nodes, r, nodes[r].SubtreeEnd, canvas, null);
            }

            var output = new Color32[w * h];
            float[] p = canvas.P;
            for (int i = 0, j = 0; i < output.Length; i++, j += 4)
            {
                float a = p[j + 3];
                output[i] = a <= 0 ? new Color32(0, 0, 0, 0) : new Color32(ToByte(p[j] / a), ToByte(p[j + 1] / a), ToByte(p[j + 2] / a), ToByte(a));
            }

            Free(canvas);
            return output;
        }

        private static byte ToByte(float v)
        {
            int i = (int)(v * 255f + 0.5f);
            return i <= 0 ? (byte)0 : i >= 255 ? (byte)255 : (byte)i;
        }

        // ─── Tree walk ───────────────────────────────────────────────────────────

        /// <summary>Draw the sibling layers in [start, end) (each with its subtree) onto the canvas.</summary>
        private void DrawRange(FigSceneNode[] nodes, int start, int end, Canvas c, FigMask clip)
        {
            FigMask maskClip = null;
            for (int i = start; i < end; i = nodes[i].SubtreeEnd)
            {
                FigSceneNode n = nodes[i];
                if (!Overlaps(n.SubtreeBounds, c) || (excluded != null && excluded.Contains(i)))
                {
                    continue;
                }

                if (!n.IsMask && DrawAsDot(n, c, maskClip ?? clip))
                {
                    continue;
                }

                if (n.IsMask)
                {
                    // A mask clips the siblings above it, and isn't drawn itself.
                    FigMask m = Coverage(n.FillPaths ?? BoxPath(n), n.World, c);
                    if (maskClip != null)
                    {
                        m.MultiplyBy(maskClip);
                        Release(maskClip);
                    }
                    else if (clip != null)
                    {
                        m.MultiplyBy(clip);
                    }

                    maskClip = m;
                    continue;
                }

                FigMask effective = maskClip ?? clip;
                if (n.NeedsIsolation)
                {
                    DrawIsolated(nodes, n, c, effective);
                }
                else
                {
                    DrawNode(nodes, n, c, effective, n.Opacity);
                }
            }

            if (maskClip != null)
            {
                Release(maskClip);
            }
        }

        /// <summary>
        /// Level of detail: a layer (with its subtree) smaller than a pixel is one blended dot of its
        /// first fill, weighted by the area it covers — zoomed out, a page of thousands of tiny layers
        /// costs about as much as a page of rectangles.
        /// </summary>
        private bool DrawAsDot(FigSceneNode n, Canvas c, FigMask clip)
        {
            Rect b = n.SubtreeBounds;
            float pw = b.width * scale, ph = b.height * scale;
            if (pw >= 1f || ph >= 1f || n.NeedsIsolation)
            {
                return false;
            }

            List<FigPaint> fills = n.Fills ?? n.Strokes;
            if (fills == null || fills.Count == 0 || (n.FillPaths == null && n.Glyphs == null && n.StrokePaths == null))
            {
                // Nothing of its own to stand for the subtree: draw it normally (its children are dots too).
                return false;
            }

            int px = (int)Math.Floor(b.center.x * scale) - c.OX, py = (int)Math.Floor(b.center.y * scale) - c.OY;
            if (px < 0 || py < 0 || px >= c.W || py >= c.H)
            {
                return true;
            }

            FigPaint paint = fills[fills.Count - 1];
            float r, g, bl, a;
            if (paint.Kind == FigPaintKind.Solid)
            {
                r = paint.Color.r * paint.Color.a;
                g = paint.Color.g * paint.Color.a;
                bl = paint.Color.b * paint.Color.a;
                a = paint.Color.a;
            }
            else if (paint.Lut != null)
            {
                r = paint.Lut[128 * 4];
                g = paint.Lut[128 * 4 + 1];
                bl = paint.Lut[128 * 4 + 2];
                a = paint.Lut[128 * 4 + 3];
            }
            else
            {
                r = g = bl = 0.6f * paint.Opacity;
                a = paint.Opacity;
            }

            // Text covers roughly a third of its box with ink.
            float k = Mathf.Max(0.05f, pw * ph) * (n.Glyphs != null ? 0.35f : 1f) * n.Opacity;
            if (clip != null)
            {
                k *= ClipAt(clip, px, py) / 255f;
            }

            if (k > 0.0005f)
            {
                Blend(c.P, (py * c.W + px) * 4, r * k, g * k, bl * k, a * k, FigBlend.Normal);
            }

            return true;
        }

        private bool Overlaps(Rect world, Canvas c)
        {
            return world.xMax * scale >= c.OX && world.xMin * scale <= c.OX + c.W && world.yMax * scale >= c.OY && world.yMin * scale <= c.OY + c.H;
        }

        private static List<FigPath> BoxPath(FigSceneNode n)
        {
            return new List<FigPath> { FigPath.Rectangle(0, 0, n.Width, n.Height) };
        }

        /// <summary>A layer's own paint (fills, strokes, glyphs) and its children, at <paramref name="opacity"/>.</summary>
        private void DrawNode(FigSceneNode[] nodes, FigSceneNode n, Canvas c, FigMask clip, float opacity)
        {
            if (Overlaps(n.PaintBounds, c))
            {
                if (n.Fills != null && n.FillPaths != null)
                {
                    FigMask cov = Coverage(n.FillPaths, n.World, c);
                    foreach (FigPaint paint in n.Fills)
                    {
                        Composite(c, cov, clip, paint, opacity, n);
                    }

                    Release(cov);
                }

                if (n.Glyphs != null)
                {
                    foreach (FigGlyphRun run in n.Glyphs)
                    {
                        FigMask cov = Coverage(new List<FigPath> { run.Path }, n.World, c);
                        foreach (FigPaint paint in run.Fills)
                        {
                            Composite(c, cov, clip, paint, opacity, n);
                        }

                        Release(cov);
                    }
                }

                if (!n.StrokeAboveChildren)
                {
                    DrawStrokes(n, c, clip, opacity);
                }
            }

            if (n.SubtreeEnd > n.Index + 1 && !n.SkipChildren)
            {
                FigMask childClip = clip;
                FigMask own = null;
                if (n.ClipsContent && n.FillPaths != null)
                {
                    own = Coverage(n.FillPaths, n.World, c);
                    if (clip != null)
                    {
                        own.MultiplyBy(clip);
                    }

                    childClip = own;
                }

                if (own == null || !own.IsEmpty)
                {
                    if (opacity < 0.999f)
                    {
                        // Not isolated (no effects): children take the opacity directly.
                        DrawChildrenWithOpacity(nodes, n, c, childClip, opacity);
                    }
                    else
                    {
                        DrawRange(nodes, n.Index + 1, n.SubtreeEnd, c, childClip);
                    }
                }

                if (own != null)
                {
                    Release(own);
                }
            }

            if (n.StrokeAboveChildren && Overlaps(n.PaintBounds, c))
            {
                DrawStrokes(n, c, clip, opacity);
            }
        }

        private void DrawChildrenWithOpacity(FigSceneNode[] nodes, FigSceneNode n, Canvas c, FigMask clip, float opacity)
        {
            Canvas temp = NewCanvas(c.OX, c.OY, c.W, c.H);
            DrawRange(nodes, n.Index + 1, n.SubtreeEnd, temp, clip);
            Blit(temp, c, null, opacity, FigBlend.Normal);
            Free(temp);
        }

        private void DrawStrokes(FigSceneNode n, Canvas c, FigMask clip, float opacity)
        {
            if (n.Strokes == null)
            {
                return;
            }

            FigMask cov;
            if (n.StrokePaths != null)
            {
                cov = Coverage(n.StrokePaths, n.World, c);
            }
            else if (n.StrokeFromOutline && (n.FillPaths != null || n.Glyphs != null))
            {
                cov = GetMask(c);
                FigAffine toPixels = ToPixels(n.World, c);
                contours.Clear();
                List<FigPath> shape = n.FillPaths;
                if (shape == null)
                {
                    shape = new List<FigPath>(n.Glyphs.Count);
                    foreach (FigGlyphRun run in n.Glyphs)
                    {
                        shape.Add(run.Path);
                    }
                }

                foreach (FigPath path in shape)
                {
                    path.Flatten(toPixels, Tolerance, contours);
                }

                // Inside / outside: a ring twice as wide, cut to the inside or outside of the shape.
                bool sided = n.StrokeAlign == "INSIDE" || n.StrokeAlign == "OUTSIDE";
                float width = Mathf.Max(0.5f, n.StrokeWeight * n.World.Scale * scale * (sided ? 2f : 1f));
                FigRaster.Fill(FigRaster.Stroke(contours, width), false, cov);
                if (sided)
                {
                    FigMask inside = Coverage(shape, n.World, c);
                    if (n.StrokeAlign == "INSIDE")
                    {
                        cov.MultiplyBy(inside);
                    }
                    else
                    {
                        cov.MultiplyByInverse(inside);
                    }

                    Release(inside);
                }
            }
            else
            {
                return;
            }

            foreach (FigPaint paint in n.Strokes)
            {
                Composite(c, cov, clip, paint, opacity, n);
            }

            Release(cov);
        }

        // ─── Isolation and effects ───────────────────────────────────────────────

        /// <summary>
        /// Draw a layer and its subtree on its own canvas (with room for its effects), then apply the
        /// effects and composite it with its opacity and blend mode — like Figma's layer compositing.
        /// </summary>
        private void DrawIsolated(FigSceneNode[] nodes, FigSceneNode n, Canvas c, FigMask clip)
        {
            float reach = 0;
            if (n.Effects != null)
            {
                foreach (FigEffect e in n.Effects)
                {
                    reach = Mathf.Max(reach, e.Reach);
                }
            }

            int margin = Mathf.Min(MaxMargin, Mathf.CeilToInt(reach * n.World.Scale * scale) + 2);
            // The isolated canvas: this canvas grown by the margin, cut to what the subtree can paint.
            int x0 = c.OX - margin, y0 = c.OY - margin, x1 = c.OX + c.W + margin, y1 = c.OY + c.H + margin;
            Rect sb = n.SubtreeBounds;
            x0 = Math.Max(x0, (int)Math.Floor(sb.xMin * scale) - 2);
            y0 = Math.Max(y0, (int)Math.Floor(sb.yMin * scale) - 2);
            x1 = Math.Min(x1, (int)Math.Ceiling(sb.xMax * scale) + 2);
            y1 = Math.Min(y1, (int)Math.Ceiling(sb.yMax * scale) + 2);
            if (x1 <= x0 || y1 <= y0)
            {
                return;
            }

            Canvas layer = NewCanvas(x0, y0, x1 - x0, y1 - y0);
            DrawNode(nodes, n, layer, null, 1f);

            float[] alpha = null;
            if (n.Effects != null)
            {
                alpha = RentFloats(layer.W * layer.H, false);
                for (int i = 0; i < alpha.Length; i++)
                {
                    alpha[i] = layer.P[i * 4 + 3];
                }

                // Background blur: what is already under the layer, blurred inside its shape.
                foreach (FigEffect e in n.Effects)
                {
                    if (e.Kind == FigEffectKind.BackgroundBlur)
                    {
                        BackgroundBlur(c, layer, alpha, clip, e.Radius * 0.5f * n.World.Scale * scale);
                    }
                }

                // Drop shadows go under the layer.
                foreach (FigEffect e in n.Effects)
                {
                    if (e.Kind == FigEffectKind.DropShadow)
                    {
                        DrawShadow(c, layer, alpha, clip, e, n, n.Opacity);
                    }
                }

                foreach (FigEffect e in n.Effects)
                {
                    if (e.Kind == FigEffectKind.InnerShadow)
                    {
                        InnerShadow(layer, alpha, e, n);
                    }
                }

                foreach (FigEffect e in n.Effects)
                {
                    if (e.Kind == FigEffectKind.LayerBlur)
                    {
                        FigRaster.Blur(layer.P, layer.W, layer.H, 4, e.Radius * 0.5f * n.World.Scale * scale);
                    }
                }
            }

            Blit(layer, c, clip, n.Opacity, n.Blend);
            ReturnFloats(alpha);
            Free(layer);
        }

        private void DrawShadow(Canvas c, Canvas layer, float[] alpha, FigMask clip, FigEffect e, FigSceneNode n, float opacity)
        {
            int w = layer.W, h = layer.H;
            float[] a = RentFloats(w * h, true);
            Vector2 offset = n.World.ApplyVector(e.Offset) * scale;
            int dx = Mathf.RoundToInt(offset.x), dy = Mathf.RoundToInt(offset.y);
            for (int y = 0; y < h; y++)
            {
                int sy = y - dy;
                if (sy < 0 || sy >= h)
                {
                    continue;
                }

                for (int x = 0; x < w; x++)
                {
                    int sx = x - dx;
                    if (sx >= 0 && sx < w)
                    {
                        a[y * w + x] = alpha[sy * w + sx];
                    }
                }
            }

            int spread = Mathf.RoundToInt(e.Spread * n.World.Scale * scale);
            if (spread != 0)
            {
                FigRaster.Spread(a, w, h, spread);
            }

            FigRaster.Blur(a, w, h, 1, e.Radius * 0.5f * n.World.Scale * scale);
            Color col = e.Color;
            for (int y = 0; y < h; y++)
            {
                int cy = layer.OY + y - c.OY;
                if (cy < 0 || cy >= c.H)
                {
                    continue;
                }

                for (int x = 0; x < w; x++)
                {
                    int cx = layer.OX + x - c.OX;
                    if (cx < 0 || cx >= c.W)
                    {
                        continue;
                    }

                    int li = y * w + x;
                    float k = a[li] * col.a * opacity;
                    if (!e.ShowBehind)
                    {
                        k *= 1f - alpha[li];
                    }

                    if (clip != null)
                    {
                        k *= ClipAt(clip, cx, cy) / 255f;
                    }

                    if (k <= 0.0005f)
                    {
                        continue;
                    }

                    int ci = (cy * c.W + cx) * 4;
                    float inv = 1f - k;
                    c.P[ci] = col.r * k + c.P[ci] * inv;
                    c.P[ci + 1] = col.g * k + c.P[ci + 1] * inv;
                    c.P[ci + 2] = col.b * k + c.P[ci + 2] * inv;
                    c.P[ci + 3] = k + c.P[ci + 3] * inv;
                }
            }

            ReturnFloats(a);
        }

        private void InnerShadow(Canvas layer, float[] alpha, FigEffect e, FigSceneNode n)
        {
            int w = layer.W, h = layer.H;
            float[] a = RentFloats(w * h, false);
            Vector2 offset = n.World.ApplyVector(e.Offset) * scale;
            int dx = Mathf.RoundToInt(offset.x), dy = Mathf.RoundToInt(offset.y);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int sx = x - dx, sy = y - dy;
                    float inside = sx >= 0 && sx < w && sy >= 0 && sy < h ? alpha[sy * w + sx] : 0f;
                    a[y * w + x] = 1f - inside;
                }
            }

            int spread = Mathf.RoundToInt(e.Spread * n.World.Scale * scale);
            if (spread != 0)
            {
                FigRaster.Spread(a, w, h, spread);
            }

            FigRaster.Blur(a, w, h, 1, e.Radius * 0.5f * n.World.Scale * scale);
            Color col = e.Color;
            for (int i = 0; i < a.Length; i++)
            {
                float k = a[i] * alpha[i] * col.a;
                if (k <= 0.0005f)
                {
                    continue;
                }

                int pi = i * 4;
                float inv = 1f - k;
                // Inside the shape only: keep the layer's alpha, tint its colour.
                layer.P[pi] = col.r * k * alpha[i] + layer.P[pi] * inv;
                layer.P[pi + 1] = col.g * k * alpha[i] + layer.P[pi + 1] * inv;
                layer.P[pi + 2] = col.b * k * alpha[i] + layer.P[pi + 2] * inv;
                layer.P[pi + 3] = Mathf.Max(layer.P[pi + 3], k * alpha[i]);
            }

            ReturnFloats(a);
        }

        private void BackgroundBlur(Canvas c, Canvas layer, float[] alpha, FigMask clip, float sigma)
        {
            // The overlap of the layer and the canvas, blurred, shown through the layer's alpha.
            int x0 = Math.Max(c.OX, layer.OX), y0 = Math.Max(c.OY, layer.OY);
            int x1 = Math.Min(c.OX + c.W, layer.OX + layer.W), y1 = Math.Min(c.OY + c.H, layer.OY + layer.H);
            int w = x1 - x0, h = y1 - y0;
            if (w <= 0 || h <= 0 || sigma < 0.3f)
            {
                return;
            }

            float[] region = RentFloats(w * h * 4, false);
            for (int y = 0; y < h; y++)
            {
                Array.Copy(c.P, ((y0 - c.OY + y) * c.W + (x0 - c.OX)) * 4, region, y * w * 4, w * 4);
            }

            FigRaster.Blur(region, w, h, 4, sigma);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int cx = x0 - c.OX + x, cy = y0 - c.OY + y;
                    float k = alpha[(y0 - layer.OY + y) * layer.W + (x0 - layer.OX + x)];
                    if (clip != null)
                    {
                        k *= ClipAt(clip, cx, cy) / 255f;
                    }

                    if (k <= 0)
                    {
                        continue;
                    }

                    int ci = (cy * c.W + cx) * 4, ri = (y * w + x) * 4;
                    for (int ch = 0; ch < 4; ch++)
                    {
                        c.P[ci + ch] += (region[ri + ch] - c.P[ci + ch]) * k;
                    }
                }
            }

            ReturnFloats(region);
        }

        /// <summary>Composite an isolated layer canvas onto <paramref name="dst"/>.</summary>
        private void Blit(Canvas src, Canvas dst, FigMask clip, float opacity, FigBlend blend)
        {
            int x0 = Math.Max(dst.OX, src.OX), y0 = Math.Max(dst.OY, src.OY);
            int x1 = Math.Min(dst.OX + dst.W, src.OX + src.W), y1 = Math.Min(dst.OY + dst.H, src.OY + src.H);
            for (int y = y0; y < y1; y++)
            {
                int dy = y - dst.OY, sy = y - src.OY;
                for (int x = x0; x < x1; x++)
                {
                    int dx = x - dst.OX, sx = x - src.OX;
                    int si = (sy * src.W + sx) * 4;
                    float k = opacity;
                    if (clip != null)
                    {
                        k *= ClipAt(clip, dx, dy) / 255f;
                    }

                    float sa = src.P[si + 3] * k;
                    if (sa <= 0.0001f)
                    {
                        continue;
                    }

                    Blend(dst.P, (dy * dst.W + dx) * 4, src.P[si] * k, src.P[si + 1] * k, src.P[si + 2] * k, sa, blend);
                }
            }
        }

        private static int ClipAt(FigMask clip, int x, int y)
        {
            if (y < 0 || y >= clip.Height || x < clip.RowMin[y] || x > clip.RowMax[y])
            {
                return 0;
            }

            return clip.A[y * clip.Width + x];
        }

        // ─── Coverage ────────────────────────────────────────────────────────────

        private FigAffine ToPixels(FigAffine world, Canvas c)
        {
            return new FigAffine { A = scale, E = scale, C = -c.OX, F = -c.OY }.Then(world);
        }

        private FigMask Coverage(List<FigPath> paths, FigAffine world, Canvas c)
        {
            FigMask mask = GetMask(c);
            FigAffine toPixels = ToPixels(world, c);
            foreach (FigPath path in paths)
            {
                Rect b = FigGeometry.TransformBounds(toPixels, path.Bounds);
                if (b.xMax < 0 || b.yMax < 0 || b.xMin > c.W || b.yMin > c.H)
                {
                    continue;
                }

                contours.Clear();
                path.Flatten(toPixels, Tolerance, contours);
                FigRaster.Fill(contours, path.EvenOdd, mask);
            }

            return mask;
        }

        private FigMask GetMask(Canvas c)
        {
            long key = (long)c.W << 32 | (uint)c.H;
            if (maskPool.TryGetValue(key, out Stack<FigMask> stack) && stack.Count > 0)
            {
                FigMask m = stack.Pop();
                pooledMaskBytes -= m.Width * m.Height + m.Height * 8;
                return m;
            }

            return new FigMask(c.W, c.H);
        }

        private void Release(FigMask m)
        {
            int bytes = m.Width * m.Height + m.Height * 8;
            if (pooledMaskBytes + bytes > MaxPooledMaskBytes)
            {
                return;
            }

            long key = (long)m.Width << 32 | (uint)m.Height;
            if (!maskPool.TryGetValue(key, out Stack<FigMask> stack))
            {
                maskPool[key] = stack = new Stack<FigMask>();
            }

            m.Clear();
            stack.Push(m);
            pooledMaskBytes += bytes;
        }

        // ─── Paint ───────────────────────────────────────────────────────────────

        /// <summary>Paint <paramref name="paint"/> through coverage <paramref name="cov"/> (and the clip) onto the canvas.</summary>
        private void Composite(Canvas c, FigMask cov, FigMask clip, FigPaint paint, float opacity, FigSceneNode n)
        {
            if (cov.IsEmpty)
            {
                return;
            }

            float w = n.Width > 1e-6f ? n.Width : 1f, h = n.Height > 1e-6f ? n.Height : 1f;
            // Pixel centre → layer-local point.
            FigAffine local = ToPixels(n.World, c).Inverse();
            FigAffine gradient = default;
            FigImageData image = null;
            int level = 0;
            ImageMap map = default;

            switch (paint.Kind)
            {
                case FigPaintKind.Image:
                    image = Images?.Invoke(paint.ImageHash);
                    if (image == null)
                    {
                        Missing.Add(paint.ImageHash);
                        // Placeholder while the image decodes.
                        var placeholder = new FigPaint { Kind = FigPaintKind.Solid, Color = new Color(0.82f, 0.82f, 0.82f, paint.Opacity), Opacity = paint.Opacity };
                        Composite(c, cov, clip, placeholder, opacity, n);
                        return;
                    }

                    map = ImageMapFor(paint, image, w, h);
                    // Texels per screen pixel, to pick the mip level.
                    Vector2 du = local.ApplyVector(new Vector2(1, 0));
                    Vector2 dv = local.ApplyVector(new Vector2(0, 1));
                    float texels = Mathf.Max((map.ToUv.ApplyVector(du)).magnitude * image.Width, (map.ToUv.ApplyVector(dv)).magnitude * image.Height);
                    level = Mathf.Clamp((int)Mathf.Floor(Mathf.Log(Mathf.Max(1f, texels), 2f)), 0, image.Levels.Count - 1);
                    break;
                case FigPaintKind.Solid:
                    break;
                default:
                    gradient = paint.Transform.Then(new FigAffine { A = 1f / w, E = 1f / h }).Then(local);
                    break;
            }

            Color sc = paint.Color;
            float sr = sc.r * sc.a, sg = sc.g * sc.a, sb = sc.b * sc.a, sa = sc.a;
            FigBlend blend = paint.Blend != FigBlend.Normal ? paint.Blend : n.Blend;
            bool normal = blend == FigBlend.Normal;
            float[] p = c.P;
            float[] lut = paint.Lut;

            for (int y = Math.Max(0, cov.YMin); y <= Math.Min(cov.Height - 1, cov.YMax); y++)
            {
                int x0 = cov.RowMin[y], x1 = cov.RowMax[y];
                if (clip != null)
                {
                    x0 = Math.Max(x0, clip.RowMin[y]);
                    x1 = Math.Min(x1, clip.RowMax[y]);
                }

                int row = y * cov.Width;
                for (int x = x0; x <= x1; x++)
                {
                    int a = cov.A[row + x];
                    if (a == 0)
                    {
                        continue;
                    }

                    float k = a * (1f / 255f) * opacity;
                    if (clip != null)
                    {
                        k *= clip.A[row + x] * (1f / 255f);
                        if (k <= 0)
                        {
                            continue;
                        }
                    }

                    float r, g, b, al;
                    switch (paint.Kind)
                    {
                        case FigPaintKind.Solid:
                            r = sr;
                            g = sg;
                            b = sb;
                            al = sa;
                            break;
                        case FigPaintKind.Image:
                        {
                            Vector2 lp = local.Apply(x + 0.5f, y + 0.5f);
                            if (!SampleImage(image, level, map, lp, out r, out g, out b, out al))
                            {
                                continue;
                            }

                            r *= paint.Opacity;
                            g *= paint.Opacity;
                            b *= paint.Opacity;
                            al *= paint.Opacity;
                            break;
                        }
                        default:
                        {
                            Vector2 gp = gradient.Apply(x + 0.5f, y + 0.5f);
                            float t = GradientT(paint.Kind, gp);
                            int li = Mathf.Clamp((int)(t * 255f + 0.5f), 0, 255) * 4;
                            r = lut[li];
                            g = lut[li + 1];
                            b = lut[li + 2];
                            al = lut[li + 3];
                            break;
                        }
                    }

                    int pi = (y * c.W + x) * 4;
                    if (normal)
                    {
                        float ka = al * k;
                        float inv = 1f - ka;
                        p[pi] = r * k + p[pi] * inv;
                        p[pi + 1] = g * k + p[pi + 1] * inv;
                        p[pi + 2] = b * k + p[pi + 2] * inv;
                        p[pi + 3] = ka + p[pi + 3] * inv;
                    }
                    else
                    {
                        Blend(p, pi, r * k, g * k, b * k, al * k, blend);
                    }
                }
            }
        }

        private static float GradientT(FigPaintKind kind, Vector2 g)
        {
            switch (kind)
            {
                case FigPaintKind.LinearGradient:
                    return g.x;
                case FigPaintKind.RadialGradient:
                {
                    float dx = g.x - 0.5f, dy = g.y - 0.5f;
                    return Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                }
                case FigPaintKind.AngularGradient:
                {
                    float angle = Mathf.Atan2(g.y - 0.5f, g.x - 0.5f) / (2f * Mathf.PI);
                    return angle < 0 ? angle + 1f : angle;
                }
                default:
                    return (Mathf.Abs(g.x - 0.5f) + Mathf.Abs(g.y - 0.5f)) * 2f;
            }
        }

        /// <summary>Premultiplied source over destination with a separable blend mode.</summary>
        private static void Blend(float[] p, int i, float sr, float sg, float sb, float sa, FigBlend mode)
        {
            float dr = p[i], dg = p[i + 1], db = p[i + 2], da = p[i + 3];
            if (mode == FigBlend.Normal)
            {
                float inv = 1f - sa;
                p[i] = sr + dr * inv;
                p[i + 1] = sg + dg * inv;
                p[i + 2] = sb + db * inv;
                p[i + 3] = sa + da * inv;
                return;
            }

            p[i] = Mix(sr, sa, dr, da, mode);
            p[i + 1] = Mix(sg, sa, dg, da, mode);
            p[i + 2] = Mix(sb, sa, db, da, mode);
            p[i + 3] = sa + da * (1f - sa);
        }

        private static float Mix(float sc, float sa, float dc, float da, FigBlend mode)
        {
            float cs = sa > 0 ? sc / sa : 0, cd = da > 0 ? dc / da : 0;
            float bm;
            switch (mode)
            {
                case FigBlend.Multiply:
                    bm = cs * cd;
                    break;
                case FigBlend.Screen:
                    bm = cs + cd - cs * cd;
                    break;
                case FigBlend.Darken:
                    bm = Mathf.Min(cs, cd);
                    break;
                case FigBlend.Lighten:
                    bm = Mathf.Max(cs, cd);
                    break;
                default:
                    bm = cd <= 0.5f ? 2f * cs * cd : 1f - 2f * (1f - cs) * (1f - cd);
                    break;
            }

            return (1f - da) * sc + (1f - sa) * dc + sa * da * bm;
        }

        // ─── Images ──────────────────────────────────────────────────────────────

        private struct ImageMap
        {
            /// <summary>Layer-local point → image uv (0–1 covers the image).</summary>
            public FigAffine ToUv;
            public bool Tile;
            public bool ClampOutside;
        }

        /// <summary>How an image paint lays the image over a <paramref name="w"/> × <paramref name="h"/> layer.</summary>
        private static ImageMap ImageMapFor(FigPaint paint, FigImageData image, float w, float h)
        {
            float iw = Mathf.Max(1, image.Width), ih = Mathf.Max(1, image.Height);
            int quarter = ((int)Mathf.Round(paint.ImageRotation / 90f) % 4 + 4) % 4;
            bool swap = quarter % 2 == 1;
            float rw = swap ? ih : iw, rh = swap ? iw : ih;
            FigAffine toUnit;
            var map = new ImageMap();
            switch (paint.ScaleMode)
            {
                case "STRETCH":
                    // The paint transform maps the layer's unit box to image space (a crop).
                    map.ToUv = paint.Transform.Then(new FigAffine { A = 1f / w, E = 1f / h });
                    map.ClampOutside = true;
                    return map;
                case "TILE":
                {
                    float s = paint.ImageScale > 0 ? paint.ImageScale : 1f;
                    toUnit = new FigAffine { A = 1f / (rw * s), E = 1f / (rh * s) };
                    map.Tile = true;
                    break;
                }
                case "FIT":
                {
                    float s = Mathf.Min(w / rw, h / rh);
                    float ox = (w - rw * s) * 0.5f, oy = (h - rh * s) * 0.5f;
                    toUnit = new FigAffine { A = 1f / (rw * s), E = 1f / (rh * s), C = -ox / (rw * s), F = -oy / (rh * s) };
                    map.ClampOutside = true;
                    break;
                }
                default:
                {
                    float s = Mathf.Max(w / rw, h / rh);
                    float ox = (w - rw * s) * 0.5f, oy = (h - rh * s) * 0.5f;
                    toUnit = new FigAffine { A = 1f / (rw * s), E = 1f / (rh * s), C = -ox / (rw * s), F = -oy / (rh * s) };
                    break;
                }
            }

            // Rotate the unit square by quarter turns about its centre.
            FigAffine rot = FigAffine.Identity;
            if (quarter == 1)
            {
                rot = new FigAffine { A = 0, B = 1, C = 0, D = -1, E = 0, F = 1 };
            }
            else if (quarter == 2)
            {
                rot = new FigAffine { A = -1, B = 0, C = 1, D = 0, E = -1, F = 1 };
            }
            else if (quarter == 3)
            {
                rot = new FigAffine { A = 0, B = -1, C = 1, D = 1, E = 0, F = 0 };
            }

            map.ToUv = rot.Then(toUnit);
            return map;
        }

        /// <summary>Bilinear sample (premultiplied) of the image at layer point <paramref name="lp"/>; false outside it.</summary>
        private static bool SampleImage(FigImageData image, int level, ImageMap map, Vector2 lp, out float r, out float g, out float b, out float a)
        {
            Vector2 uv = map.ToUv.Apply(lp);
            r = g = b = a = 0;
            if (map.Tile)
            {
                uv.x -= Mathf.Floor(uv.x);
                uv.y -= Mathf.Floor(uv.y);
            }
            else if (map.ClampOutside && (uv.x < 0 || uv.y < 0 || uv.x > 1 || uv.y > 1))
            {
                return false;
            }

            Color32[] px = image.Levels[level];
            int w = image.LevelWidths[level], h = image.LevelHeights[level];
            float fx = Mathf.Clamp(uv.x * w - 0.5f, 0, w - 1), fy = Mathf.Clamp(uv.y * h - 0.5f, 0, h - 1);
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
            float tx = fx - x0, ty = fy - y0;
            Accumulate(px[y0 * w + x0], (1 - tx) * (1 - ty), ref r, ref g, ref b, ref a);
            Accumulate(px[y0 * w + x1], tx * (1 - ty), ref r, ref g, ref b, ref a);
            Accumulate(px[y1 * w + x0], (1 - tx) * ty, ref r, ref g, ref b, ref a);
            Accumulate(px[y1 * w + x1], tx * ty, ref r, ref g, ref b, ref a);
            return true;
        }

        private static void Accumulate(Color32 c, float wgt, ref float r, ref float g, ref float b, ref float a)
        {
            float al = c.a * (1f / 255f) * wgt;
            r += c.r * (1f / 255f) * al;
            g += c.g * (1f / 255f) * al;
            b += c.b * (1f / 255f) * al;
            a += al;
        }
    }
}
