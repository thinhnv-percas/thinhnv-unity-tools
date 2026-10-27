using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// Draws a <see cref="FigBoardScene"/> into the board each repaint, keeping the per-frame work small:
    /// <list type="bullet">
    /// <item>shapes go out as one batch of GL triangles (split only where a texture must be drawn in
    /// between to keep the paint order), not one immediate-mode call per layer;</item>
    /// <item>vector layers are rasterized from their geometry blobs into textures cached per layer and
    /// zoom bucket (powers of two), within a time budget per frame and an LRU pixel budget;</item>
    /// <item>images decode lazily, a few per frame; layers off screen or under a pixel are skipped and
    /// tiny text draws as a bar.</item>
    /// </list>
    /// Everything is clipped on the CPU to the board and to clipping frames, so nothing spills onto
    /// the rest of the window.
    /// </summary>
    public sealed class FigBoardRenderer : IDisposable
    {
        private const float RasterBudgetMs = 8f;
        private const int MaxRasterSide = 1024;
        private const long MaxRasterPixels = 24L * 1024 * 1024;
        private const int ImageDecodesPerFrame = 2;

        private sealed class Raster
        {
            public Texture2D Texture;
            public Rect LocalBounds;
            public float Bucket;
            public int Signature;
            public int LastUsed;
            public long Pixels;
        }

        private readonly Dictionary<string, Raster> rasters = new();
        private readonly Dictionary<long, List<FigContour>> decodedBlobs = new();
        private readonly Stopwatch budget = new();
        private long rasterPixels;
        private int frame;

        private readonly List<Vector3> verts = new(4096);
        private readonly List<Color> colors = new(4096);
        private readonly List<Vector2> polyA = new(64);
        private readonly List<Vector2> polyB = new(64);
        private readonly List<Vector2> polyC = new(64);
        private GUIStyle textStyle;

        /// <summary>True when the last frame left work undone (rasters, images); repaint again.</summary>
        public bool HasPendingWork { get; private set; }

        public int DrawnItems { get; private set; }
        public int RasterCount => rasters.Count;
        public long RasterMegapixels => rasterPixels / (1024 * 1024);

        /// <summary>Looks up (and may decode) an image texture; returns null while not available.</summary>
        public Func<string, bool, Texture2D> ImageSource;

        public FigFile File;

        public void Dispose()
        {
            Clear();
        }

        /// <summary>Drop every cached raster and decoded blob (file closed or reloaded).</summary>
        public void Clear()
        {
            foreach (Raster r in rasters.Values)
            {
                if (r.Texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(r.Texture);
                }
            }

            rasters.Clear();
            decodedBlobs.Clear();
            rasterPixels = 0;
        }

        // ─── Frame ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Draw <paramref name="scene"/> into a GUI clip of size <paramref name="size"/>, mapping world
        /// points through <c>p * scale + offset</c>. Call during Repaint, inside GUI.BeginClip.
        /// </summary>
        public void Draw(FigBoardScene scene, Vector2 size, float scale, Vector2 offset)
        {
            frame++;
            HasPendingWork = false;
            DrawnItems = 0;
            budget.Reset();
            budget.Start();
            int decodes = ImageDecodesPerFrame;

            var canvas = new Rect(0, 0, size.x, size.y);
            EditorGUI.DrawRect(canvas, scene.Background);
            Rect visibleWorld = Rect.MinMaxRect(-offset.x / scale, -offset.y / scale, (size.x - offset.x) / scale, (size.y - offset.y) / scale);

            List<FigBoardItem> items = scene.Items;
            for (int i = 0; i < items.Count; i++)
            {
                FigBoardItem item = items[i];
                float pad = item.HasStroke ? item.StrokeWeight : 0;
                Rect b = item.Bounds;
                if (b.xMax + pad < visibleWorld.xMin || b.xMin - pad > visibleWorld.xMax || b.yMax + pad < visibleWorld.yMin || b.yMin - pad > visibleWorld.yMax)
                {
                    // Off screen: the children may still be visible, so only this layer is skipped,
                    // unless it clips them too.
                    if (item.ClipsContent)
                    {
                        i = item.SubtreeEnd - 1;
                    }

                    continue;
                }

                Rect screen = ToScreen(b, scale, offset);
                if (screen.width < 0.5f && screen.height < 0.5f)
                {
                    if (item.ClipsContent)
                    {
                        i = item.SubtreeEnd - 1;
                    }

                    continue;
                }

                Rect clip = canvas;
                if (item.ClipParent >= 0)
                {
                    clip = FigGeometry.Intersect(clip, ToScreen(items[item.ClipParent].Bounds, scale, offset));
                    if (clip.width <= 0 || clip.height <= 0)
                    {
                        continue;
                    }
                }

                DrawnItems++;
                DrawItem(item, screen, clip, scale, offset, ref decodes);
            }

            Flush();
            budget.Stop();
            TrimRasters();
        }

        private void DrawItem(FigBoardItem item, Rect screen, Rect clip, float scale, Vector2 offset, ref int decodes)
        {
            if (item.UsesGeometry)
            {
                bool tiny = screen.width < 3f && screen.height < 3f;
                if (item.HasFill)
                {
                    if (tiny)
                    {
                        AddRect(screen, item.Fill, clip);
                    }
                    else
                    {
                        DrawGeometry(item, false, item.Fill, scale, offset, clip);
                    }
                }

                if (item.HasStroke && !tiny)
                {
                    DrawGeometry(item, true, item.Stroke, scale, offset, clip);
                }

                return;
            }

            if (item.HasFill)
            {
                if (item.ImageHash != null)
                {
                    DrawImage(item, screen, clip, scale, offset, ref decodes);
                }
                else
                {
                    AddPolygon(item.Outline, scale, offset, item.Fill, clip);
                }
            }

            if (item.HasStroke)
            {
                float width = item.StrokeWeight * item.World.Scale * scale;
                Color c = item.Stroke;
                if (width < 1f)
                {
                    // Hairlines fade instead of disappearing.
                    c.a *= Mathf.Max(0.25f, width);
                    width = 1f;
                }

                AddOutline(item.Outline, item.Type == "LINE" ? 2 : item.Outline.Length, item.Type != "LINE", scale, offset, width, c, clip);
            }

            if (item.Type == "LINE" && !item.HasStroke)
            {
                AddOutline(item.Outline, 2, false, scale, offset, 1f, new Color(0.5f, 0.5f, 0.5f, 0.8f), clip);
            }

            if (item.Text != null)
            {
                DrawText(item, screen, clip, scale);
            }
        }

        private static Rect ToScreen(Rect world, float scale, Vector2 offset)
        {
            return new Rect(world.x * scale + offset.x, world.y * scale + offset.y, world.width * scale, world.height * scale);
        }

        // ─── Overlays (selection, guides) ────────────────────────────────────────

        /// <summary>A polyline in board (screen) coordinates, clipped to <paramref name="clip"/>.</summary>
        public void OverlayLine(Vector2[] points, int count, bool closed, float width, Color color, Rect clip)
        {
            AddOutline(points, count, closed, 1f, Vector2.zero, width, color, clip);
        }

        public void OverlayRect(Rect r, Color color, Rect clip)
        {
            AddRect(r, color, clip);
        }

        /// <summary>Draw the queued overlay geometry now (before GUI labels that should sit on top).</summary>
        public void FlushOverlay()
        {
            Flush();
        }

        // ─── GL batch ────────────────────────────────────────────────────────────

        private void AddRect(Rect r, Color c, Rect clip)
        {
            polyA.Clear();
            polyA.Add(new Vector2(r.xMin, r.yMin));
            polyA.Add(new Vector2(r.xMax, r.yMin));
            polyA.Add(new Vector2(r.xMax, r.yMax));
            polyA.Add(new Vector2(r.xMin, r.yMax));
            AddConvex(polyA, c, clip);
        }

        private void AddPolygon(Vector2[] world, float scale, Vector2 offset, Color c, Rect clip)
        {
            polyA.Clear();
            foreach (Vector2 p in world)
            {
                polyA.Add(p * scale + offset);
            }

            AddConvex(polyA, c, clip);
        }

        private void AddOutline(Vector2[] world, int count, bool closed, float scale, Vector2 offset, float width, Color c, Rect clip)
        {
            int segments = closed ? count : count - 1;
            float half = width * 0.5f;
            for (int i = 0; i < segments; i++)
            {
                Vector2 a = world[i] * scale + offset;
                Vector2 b = world[(i + 1) % count] * scale + offset;
                Vector2 d = b - a;
                float len = d.magnitude;
                if (len < 1e-4f)
                {
                    continue;
                }

                // Extend each segment by half the width so corners don't show notches.
                Vector2 dir = d / len;
                Vector2 n = new Vector2(-dir.y, dir.x) * half;
                a -= dir * half;
                b += dir * half;
                polyA.Clear();
                polyA.Add(a + n);
                polyA.Add(b + n);
                polyA.Add(b - n);
                polyA.Add(a - n);
                AddConvex(polyA, c, clip);
            }
        }

        /// <summary>Clip a convex screen polygon to <paramref name="clip"/> and add it as a triangle fan.</summary>
        private void AddConvex(List<Vector2> poly, Color c, Rect clip)
        {
            List<Vector2> pts = poly;
            if (!InsideRect(poly, clip))
            {
                FigGeometry.ClipToRect(poly, clip, polyB, polyC);
                pts = polyB;
            }

            for (int i = 1; i + 1 < pts.Count; i++)
            {
                verts.Add(pts[0]);
                verts.Add(pts[i]);
                verts.Add(pts[i + 1]);
                colors.Add(c);
                colors.Add(c);
                colors.Add(c);
            }
        }

        private static bool InsideRect(List<Vector2> poly, Rect r)
        {
            foreach (Vector2 p in poly)
            {
                if (p.x < r.xMin || p.x > r.xMax || p.y < r.yMin || p.y > r.yMax)
                {
                    return false;
                }
            }

            return true;
        }

        private void Flush()
        {
            if (verts.Count == 0)
            {
                return;
            }

            if (Event.current.type == EventType.Repaint)
            {
                HandleUtility.ApplyWireMaterial();
                GL.PushMatrix();
                GL.MultMatrix(Handles.matrix);
                GL.Begin(GL.TRIANGLES);
                Color last = new Color(-1, -1, -1, -1);
                for (int i = 0; i < verts.Count; i++)
                {
                    Color c = colors[i];
                    if (c != last)
                    {
                        GL.Color(c);
                        last = c;
                    }

                    GL.Vertex(verts[i]);
                }

                GL.End();
                GL.PopMatrix();
            }

            verts.Clear();
            colors.Clear();
        }

        // ─── Images ──────────────────────────────────────────────────────────────

        private void DrawImage(FigBoardItem item, Rect screen, Rect clip, float scale, Vector2 offset, ref int decodes)
        {
            bool allowDecode = decodes > 0;
            Texture2D tex = ImageSource?.Invoke(item.ImageHash, allowDecode);
            if (tex == null)
            {
                if (allowDecode)
                {
                    decodes--;
                }

                // Placeholder while it decodes (or when it can't be shown): a neutral fill.
                AddPolygon(item.Outline, scale, offset, new Color(0.55f, 0.55f, 0.55f, 0.5f * item.Fill.a), clip);
                HasPendingWork |= !allowDecode;
                return;
            }

            if (!item.World.AxisAligned)
            {
                // Rotated images draw as their average tone.
                AddPolygon(item.Outline, scale, offset, new Color(0.6f, 0.6f, 0.6f, item.Fill.a), clip);
                return;
            }

            Flush();
            var tint = new Color(1, 1, 1, item.Fill.a);
            bool clipped = screen.xMin < clip.xMin || screen.yMin < clip.yMin || screen.xMax > clip.xMax || screen.yMax > clip.yMax;
            float radius = (item.Node.Change.Get("cornerRadius")?.AsFloat ?? 0) * item.World.Scale * scale;
            ScaleMode mode = item.ImageScaleMode == "FIT" ? ScaleMode.ScaleToFit : item.ImageScaleMode == "FILL" ? ScaleMode.ScaleAndCrop : ScaleMode.StretchToFill;

            if (!clipped)
            {
                GUI.DrawTexture(screen, tex, mode, true, 0, tint, 0, radius);
                return;
            }

            // Clipped by a frame or the board edge: work out which part of the texture shows.
            Rect dest = screen;
            var uv = new Rect(0, 0, 1, 1);
            float texAspect = (float)tex.width / Mathf.Max(1, tex.height);
            float rectAspect = screen.width / Mathf.Max(1e-4f, screen.height);
            if (mode == ScaleMode.ScaleAndCrop)
            {
                if (texAspect > rectAspect)
                {
                    float w = rectAspect / texAspect;
                    uv = new Rect((1 - w) * 0.5f, 0, w, 1);
                }
                else
                {
                    float h = texAspect / rectAspect;
                    uv = new Rect(0, (1 - h) * 0.5f, 1, h);
                }
            }
            else if (mode == ScaleMode.ScaleToFit)
            {
                if (texAspect > rectAspect)
                {
                    float h = screen.width / texAspect;
                    dest = new Rect(screen.x, screen.y + (screen.height - h) * 0.5f, screen.width, h);
                }
                else
                {
                    float w = screen.height * texAspect;
                    dest = new Rect(screen.x + (screen.width - w) * 0.5f, screen.y, w, screen.height);
                }
            }

            Rect shown = FigGeometry.Intersect(dest, clip);
            if (shown.width <= 0 || shown.height <= 0)
            {
                return;
            }

            DrawTexturePart(tex, dest, uv, shown, tint);
        }

        /// <summary>Draw the part <paramref name="shown"/> of a texture laid out over <paramref name="dest"/>.</summary>
        private static void DrawTexturePart(Texture tex, Rect dest, Rect uv, Rect shown, Color tint)
        {
            float u0 = uv.x + (shown.xMin - dest.xMin) / dest.width * uv.width;
            float u1 = uv.x + (shown.xMax - dest.xMin) / dest.width * uv.width;
            // Texture v runs bottom to top; screen y top to bottom.
            float vTop = uv.yMax - (shown.yMin - dest.yMin) / dest.height * uv.height;
            float vBottom = uv.yMax - (shown.yMax - dest.yMin) / dest.height * uv.height;
            Color old = GUI.color;
            GUI.color = tint;
            GUI.DrawTextureWithTexCoords(shown, tex, new Rect(u0, vBottom, u1 - u0, vTop - vBottom), true);
            GUI.color = old;
        }

        // ─── Vector geometry (raster cache) ──────────────────────────────────────

        private void DrawGeometry(FigBoardItem item, bool stroke, Color color, float scale, Vector2 offset, Rect clip)
        {
            float pixelsPerUnit = scale * Mathf.Max(1e-4f, item.World.Scale);
            Raster raster = GetRaster(item, stroke, pixelsPerUnit);
            if (raster == null || raster.Texture == null)
            {
                return;
            }

            Rect localBounds = raster.LocalBounds;
            if (item.World.AxisAligned)
            {
                Rect world = FigGeometry.TransformBounds(item.World, localBounds);
                Rect dest = ToScreen(world, scale, offset);
                Rect shown = FigGeometry.Intersect(dest, clip);
                if (shown.width <= 0 || shown.height <= 0)
                {
                    return;
                }

                Flush();
                DrawTexturePart(raster.Texture, dest, new Rect(0, 0, 1, 1), shown, color);
                return;
            }

            // Rotated / flipped: rotate the GUI around the layer's origin and draw it unrotated.
            // RotateAroundPivot takes clip-local points, so this works inside the board's clip.
            // Shear is ignored.
            Flush();
            FigAffine t = item.World;
            float sx = Mathf.Sqrt(t.A * t.A + t.D * t.D) * scale;
            float sy = Mathf.Sqrt(t.B * t.B + t.E * t.E) * scale;
            bool flipped = t.A * t.E - t.B * t.D < 0;
            float angle = Mathf.Atan2(t.D, t.A) * Mathf.Rad2Deg;
            Vector2 pivot = t.Apply(0, 0) * scale + offset;
            var rect = new Rect(pivot.x + localBounds.x * sx, pivot.y + localBounds.y * sy * (flipped ? -1 : 1), localBounds.width * sx, localBounds.height * sy);
            if (flipped)
            {
                rect.y -= rect.height;
            }

            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, pivot);
            Color oldColor = GUI.color;
            GUI.color = color;
            GUI.DrawTextureWithTexCoords(rect, raster.Texture, flipped ? new Rect(0, 1, 1, -1) : new Rect(0, 0, 1, 1), true);
            GUI.color = oldColor;
            GUI.matrix = old;
        }

        private Raster GetRaster(FigBoardItem item, bool stroke, float pixelsPerUnit)
        {
            string key = item.Id + (stroke ? "/s" : "/f");
            float bucket = Mathf.Pow(2f, Mathf.Ceil(Mathf.Log(Mathf.Max(1f / 64f, pixelsPerUnit), 2f)));
            rasters.TryGetValue(key, out Raster cached);
            int signature = stroke ? item.StrokeSignature : item.FillSignature;
            bool fresh = cached != null && cached.Signature == signature;
            if (fresh && Mathf.Approximately(cached.Bucket, bucket))
            {
                cached.LastUsed = frame;
                return cached;
            }

            if (budget.Elapsed.TotalMilliseconds > RasterBudgetMs)
            {
                // Out of time this frame: show what we have (stale size is fine for a frame or two).
                HasPendingWork = true;
                if (cached != null)
                {
                    cached.LastUsed = frame;
                }

                return fresh ? cached : null;
            }

            Raster built = BuildRaster(item, stroke, bucket);
            if (cached != null)
            {
                rasterPixels -= cached.Pixels;
                if (cached.Texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(cached.Texture);
                }
            }

            if (built == null)
            {
                rasters.Remove(key);
                return null;
            }

            built.LastUsed = frame;
            built.Signature = signature;
            rasters[key] = built;
            rasterPixels += built.Pixels;
            return built;
        }

        private struct Shape
        {
            public List<FigContour> Contours;
            public bool EvenOdd;
        }

        private Raster BuildRaster(FigBoardItem item, bool stroke, float bucket)
        {
            List<KiwiValue> paths = item.Node.Change.Get(stroke ? "strokeGeometry" : "fillGeometry")?.Items;
            List<KiwiValue> blobs = File?.Message.Get("blobs")?.Items;
            if (paths == null || paths.Count == 0 || blobs == null)
            {
                return null;
            }

            var shapes = new List<Shape>();
            Rect bounds = default;
            bool first = true;
            foreach (KiwiValue path in paths)
            {
                KiwiValue blobIndex = path.Get("commandsBlob");
                if (blobIndex == null)
                {
                    continue;
                }

                long index = (long)blobIndex.Uint;
                if (!decodedBlobs.TryGetValue(index, out List<FigContour> contours))
                {
                    byte[] bytes = index < blobs.Count ? blobs[(int)index].Get("bytes")?.Bytes : null;
                    contours = FigGeometry.DecodePath(bytes) ?? new List<FigContour>();
                    decodedBlobs[index] = contours;
                }

                if (contours.Count == 0)
                {
                    continue;
                }

                string winding = path.Get("windingRule")?.EnumName ?? "NONZERO";
                shapes.Add(new Shape { Contours = contours, EvenOdd = winding == "ODD" || winding == "EVENODD" });
                Rect b = FigGeometry.Bounds(contours);
                bounds = first ? b : FigGeometry.Union(bounds, b);
                first = false;
            }

            if (shapes.Count == 0 || bounds.width <= 0 && bounds.height <= 0)
            {
                return null;
            }

            float ppu = bucket;
            float longest = Mathf.Max(bounds.width, bounds.height) * ppu;
            if (longest > MaxRasterSide - 2)
            {
                ppu *= (MaxRasterSide - 2) / longest;
            }

            int width = Mathf.Max(1, Mathf.CeilToInt(bounds.width * ppu) + 2);
            int height = Mathf.Max(1, Mathf.CeilToInt(bounds.height * ppu) + 2);
            var mask = new byte[width * height];
            var toPixels = new FigAffine { A = ppu, E = ppu, C = 1 - bounds.xMin * ppu, F = 1 - bounds.yMin * ppu };
            foreach (Shape shape in shapes)
            {
                FigGeometry.Rasterize(shape.Contours, toPixels, width, height, shape.EvenOdd, mask);
            }

            // White with coverage as alpha, tinted when drawn; texture rows run bottom to top.
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                int src = y * width;
                int dst = (height - 1 - y) * width;
                for (int x = 0; x < width; x++)
                {
                    pixels[dst + x] = new Color32(255, 255, 255, mask[src + x]);
                }
            }

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = "FigRaster " + item.Id,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            // The texture covers the bounds plus the one-pixel margin on each side.
            float margin = 1f / ppu;
            var local = Rect.MinMaxRect(bounds.xMin - margin, bounds.yMin - margin, bounds.xMin - margin + width / ppu, bounds.yMin - margin + height / ppu);
            return new Raster { Texture = tex, LocalBounds = local, Bucket = bucket, Pixels = (long)width * height };
        }

        /// <summary>Evict the least recently used rasters while over the pixel budget (never this frame's).</summary>
        private void TrimRasters()
        {
            if (rasterPixels <= MaxRasterPixels)
            {
                return;
            }

            var byAge = new List<KeyValuePair<string, Raster>>(rasters);
            byAge.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));
            foreach (KeyValuePair<string, Raster> pair in byAge)
            {
                if (rasterPixels <= MaxRasterPixels * 3 / 4 || pair.Value.LastUsed == frame)
                {
                    break;
                }

                rasterPixels -= pair.Value.Pixels;
                if (pair.Value.Texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(pair.Value.Texture);
                }

                rasters.Remove(pair.Key);
            }
        }

        /// <summary>Forget the rasters of one layer (its geometry changed).</summary>
        public void Invalidate(string id)
        {
            foreach (string suffix in new[] { "/f", "/s" })
            {
                if (rasters.TryGetValue(id + suffix, out Raster r))
                {
                    rasterPixels -= r.Pixels;
                    if (r.Texture != null)
                    {
                        UnityEngine.Object.DestroyImmediate(r.Texture);
                    }

                    rasters.Remove(id + suffix);
                }
            }
        }

        // ─── Text ────────────────────────────────────────────────────────────────

        private void DrawText(FigBoardItem item, Rect screen, Rect clip, float scale)
        {
            if (string.IsNullOrEmpty(item.Text))
            {
                return;
            }

            float size = item.FontSize * item.World.Scale * scale;
            if (size < 5f)
            {
                // Too small to read: a faint bar per line keeps the layout readable.
                var bar = new Rect(screen.x, screen.y + screen.height * 0.3f, screen.width, Mathf.Max(1f, screen.height * 0.4f));
                AddRect(bar, new Color(item.TextColor.r, item.TextColor.g, item.TextColor.b, item.TextColor.a * 0.35f), clip);
                return;
            }

            if (!item.World.AxisAligned)
            {
                return;
            }

            Flush();
            textStyle ??= new GUIStyle(EditorStyles.label) { richText = false, clipping = TextClipping.Overflow, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
            textStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(size), 1, 160);
            textStyle.wordWrap = item.TextWraps;
            textStyle.fontStyle = item.TextBold ? item.TextItalic ? FontStyle.BoldAndItalic : FontStyle.Bold : item.TextItalic ? FontStyle.Italic : FontStyle.Normal;
            textStyle.normal.textColor = item.TextColor;
            textStyle.alignment = Anchor(item.TextAlignH, item.TextAlignV);

            Rect shown = FigGeometry.Intersect(screen, clip);
            if (shown.width <= 0 || shown.height <= 0)
            {
                return;
            }

            // The group clips to the visible part; the label keeps the layer's box so alignment and
            // wrapping match, and overflows it like Figma text does.
            GUI.BeginGroup(shown);
            GUI.Label(new Rect(screen.x - shown.x, screen.y - shown.y, Mathf.Max(screen.width, 1), Mathf.Max(screen.height, 1)), item.Text, textStyle);
            GUI.EndGroup();
        }

        private static TextAnchor Anchor(string h, string v)
        {
            int col = h == "CENTER" ? 1 : h == "RIGHT" ? 2 : 0;
            int row = v == "CENTER" ? 1 : v == "BOTTOM" ? 2 : 0;
            return (TextAnchor)(row * 3 + col);
        }
    }
}
