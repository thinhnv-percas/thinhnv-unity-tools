using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// The board's rendering cache. The page is drawn in 256-pixel tiles by <see cref="FigRenderer"/>
    /// on background threads and uploaded to textures a few per frame, so the editor never waits:
    /// <list type="bullet">
    /// <item>panning reuses tiles; zooming shows the last zoom's tiles scaled until the new ones are
    /// in (new tiles are requested once the zoom settles);</item>
    /// <item>an edit invalidates only the tiles its old and new bounds touch; stale tiles stay on
    /// screen until their replacement is ready, so nothing flickers;</item>
    /// <item>while a selection is dragged it is drawn from one "lifted" texture moved on the GPU,
    /// over tiles rendered without it;</item>
    /// <item>images decode on the main thread a few per frame, when a tile first needs them.</item>
    /// </list>
    /// </summary>
    public sealed class FigTileCache : IDisposable
    {
        public const int TileSize = 256;
        private const int MaxTiles = 600;
        private const int UploadsPerFrame = 12;
        private const double ZoomSettleSeconds = 0.12;
        private const int ImageDecodesPerFrame = 2;

        private struct Key : IEquatable<Key>
        {
            public int Zoom;
            public int X;
            public int Y;
            public int Layer;

            public bool Equals(Key o) => Zoom == o.Zoom && X == o.X && Y == o.Y && Layer == o.Layer;

            public override bool Equals(object obj) => obj is Key k && Equals(k);

            public override int GetHashCode() => ((Zoom * 397 ^ X) * 397 ^ Y) * 31 + Layer;
        }

        private sealed class Tile
        {
            public Texture2D Texture;
            public float Scale;
            public int Version = -1;
            public bool Stale = true;
            public bool Queued;
            public int Generation;
            public int LastUsed;
            public HashSet<string> MissingImages;
        }

        private sealed class Job
        {
            public Key Key;
            public int Generation;
            public FigSceneNode[] Nodes;
            public Color Background;
            public float Scale;
            public int Version;
            public int Apron;
            public HashSet<int> Exclude;
            public List<int> Only;
            public int X, Y, W, H;
            public float Priority;

            // Results
            public Color32[] Pixels;
            public string[] Missing;
        }

        private readonly Dictionary<Key, Tile> tiles = new();
        private readonly List<Job> queue = new();
        private readonly Queue<Job> done = new();
        private readonly object sync = new();
        private readonly List<Thread> threads = new();
        private volatile bool stopping;
        private int frame;
        private int[] visibleTiles;
        private int visibleZoom;
        private float lastScale = -1f;
        private float settledScale = -1f;
        private double scaleChangedAt;
        private readonly Stack<Texture2D> texturePool = new();

        /// <summary>Decoded images by hash; replaced (copy-on-write) whenever one is added, so workers read it lock-free.</summary>
        private volatile Dictionary<string, FigImageData> images = new();
        private readonly HashSet<string> wantedImages = new();
        private readonly HashSet<string> failedImages = new();

        /// <summary>Returns the bytes of an image member by hash (main thread).</summary>
        public Func<string, byte[]> ImageBytes;

        public FigScene Scene { get; private set; }

        public int PendingJobs
        {
            get
            {
                lock (sync)
                {
                    return queue.Count + done.Count;
                }
            }
        }

        public FigTileCache()
        {
            int count = Mathf.Clamp(Environment.ProcessorCount - 1, 1, 6);
            for (int i = 0; i < count; i++)
            {
                var t = new Thread(Work) { IsBackground = true, Name = "Fig Editor renderer " + i, Priority = System.Threading.ThreadPriority.BelowNormal };
                threads.Add(t);
                t.Start();
            }
        }

        public void Dispose()
        {
            stopping = true;
            lock (sync)
            {
                queue.Clear();
                Monitor.PulseAll(sync);
            }

            foreach (Thread t in threads)
            {
                t.Join(500);
            }

            threads.Clear();
            ClearTextures();
            ClearLift();
        }

        private void ClearTextures()
        {
            foreach (Tile tile in tiles.Values)
            {
                if (tile.Texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(tile.Texture);
                }
            }

            tiles.Clear();
            while (texturePool.Count > 0)
            {
                Texture2D t = texturePool.Pop();
                if (t != null)
                {
                    UnityEngine.Object.DestroyImmediate(t);
                }
            }
        }

        // ─── Scene and invalidation ──────────────────────────────────────────────

        /// <summary>A whole new scene (page switch, structure change): every tile is stale.</summary>
        public void SetScene(FigScene scene, bool samePage)
        {
            Scene = scene;
            if (!samePage)
            {
                ClearTextures();
            }
            else
            {
                foreach (Tile t in tiles.Values)
                {
                    t.Stale = true;
                }
            }

            CancelQueued();
        }

        /// <summary>The scene changed only inside <paramref name="world"/> (an edit): those tiles are stale.</summary>
        public void UpdateScene(FigScene scene, Rect world)
        {
            Scene = scene;
            foreach (KeyValuePair<Key, Tile> pair in tiles)
            {
                float s = pair.Value.Scale;
                var r = new Rect(pair.Key.X * TileSize / s, pair.Key.Y * TileSize / s, TileSize / s, TileSize / s);
                if (r.Overlaps(Grow(world, 2f / s)))
                {
                    pair.Value.Stale = true;
                }
            }
        }

        public void ClearImages()
        {
            images = new Dictionary<string, FigImageData>();
            wantedImages.Clear();
            failedImages.Clear();
        }

        private static Rect Grow(Rect r, float d) => Rect.MinMaxRect(r.xMin - d, r.yMin - d, r.xMax + d, r.yMax + d);

        private void CancelQueued()
        {
            lock (sync)
            {
                foreach (Job j in queue)
                {
                    if (tiles.TryGetValue(j.Key, out Tile t))
                    {
                        t.Queued = false;
                    }
                }

                queue.Clear();
            }
        }

        // ─── Drawing ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Draw the visible part of the page into the current GUI clip (board-local coordinates,
        /// <paramref name="size"/> big) at <paramref name="scale"/> / <paramref name="offset"/>, and queue
        /// what is missing. Returns true while work is outstanding (keep repainting).
        /// </summary>
        public bool Draw(Vector2 size, float scale, Vector2 offset, HashSet<int> exclude)
        {
            frame++;
            FigScene scene = Scene;
            if (scene == null)
            {
                return false;
            }

            double now = EditorApplication.timeSinceStartup;
            if (!Mathf.Approximately(scale, lastScale))
            {
                lastScale = scale;
                scaleChangedAt = now;
            }

            bool settled = now - scaleChangedAt >= ZoomSettleSeconds || settledScale < 0;
            if (settled)
            {
                settledScale = scale;
            }

            EditorGUI.DrawRect(new Rect(0, 0, size.x, size.y), scene.Background);

            // Tiles of an older zoom first (scaled), so the screen is never empty while zooming.
            int layer = exclude != null && exclude.Count > 0 ? 1 : 0;
            int zoomKey = ZoomKey(settledScale);
            foreach (KeyValuePair<Key, Tile> pair in tiles)
            {
                if (pair.Value.Texture != null && pair.Key.Layer == 0 && pair.Key.Zoom != zoomKey)
                {
                    DrawTile(pair.Key, pair.Value, scale, offset, size);
                }
            }

            // Then the current zoom's tiles, requesting the missing and stale ones.
            float s = settledScale;
            int tx0 = Mathf.FloorToInt(-offset.x * s / scale / TileSize);
            int ty0 = Mathf.FloorToInt(-offset.y * s / scale / TileSize);
            int tx1 = Mathf.FloorToInt((size.x - offset.x) * s / scale / TileSize);
            int ty1 = Mathf.FloorToInt((size.y - offset.y) * s / scale / TileSize);
            var center = new Vector2((tx0 + tx1) * 0.5f, (ty0 + ty1) * 0.5f);
            bool pending = !settled;
            for (int ty = ty0; ty <= ty1; ty++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    var key = new Key { Zoom = zoomKey, X = tx, Y = ty, Layer = layer };
                    tiles.TryGetValue(key, out Tile t);
                    if (t == null)
                    {
                        // A layer-1 (selection lifted) tile not ready yet: show the normal one meanwhile.
                        if (layer == 1 && tiles.TryGetValue(new Key { Zoom = zoomKey, X = tx, Y = ty, Layer = 0 }, out Tile normal) && normal.Texture != null)
                        {
                            DrawTile(new Key { Zoom = zoomKey, X = tx, Y = ty }, normal, scale, offset, size);
                        }

                        t = new Tile { Scale = s };
                        tiles[key] = t;
                    }

                    t.LastUsed = frame;
                    if (t.Texture != null)
                    {
                        DrawTile(key, t, scale, offset, size);
                    }

                    if ((t.Stale || t.Version < 0) && !t.Queued && settled)
                    {
                        Enqueue(key, t, scene, s, exclude, (new Vector2(tx, ty) - center).sqrMagnitude);
                    }

                    pending |= t.Stale || t.Version < 0;
                    if (layer == 1 && settled)
                    {
                        // After a drop the normal tiles under the move are stale: re-render them
                        // behind the lifted copy so it can be dropped once they are ready.
                        var baseKey = new Key { Zoom = zoomKey, X = tx, Y = ty };
                        if (tiles.TryGetValue(baseKey, out Tile b) && b.Stale && !b.Queued)
                        {
                            b.LastUsed = frame;
                            Enqueue(baseKey, b, scene, s, null, (new Vector2(tx, ty) - center).sqrMagnitude);
                        }
                    }
                }
            }

            visibleTiles = new[] { tx0, ty0, tx1, ty1 };
            visibleZoom = zoomKey;
            pending |= Pump();
            Trim();
            return pending;
        }

        private static int ZoomKey(float scale) => BitConverter.ToInt32(BitConverter.GetBytes(scale), 0);

        private static void DrawTile(Key key, Tile t, float scale, Vector2 offset, Vector2 size)
        {
            float k = scale / t.Scale;
            var r = new Rect(key.X * TileSize * k + offset.x, key.Y * TileSize * k + offset.y, TileSize * k, TileSize * k);
            if (r.xMax < 0 || r.yMax < 0 || r.xMin > size.x || r.yMin > size.y)
            {
                return;
            }

            // A hair of overlap hides seams between scaled tiles.
            if (!Mathf.Approximately(k, 1f))
            {
                r = new Rect(r.x - 0.5f, r.y - 0.5f, r.width + 1f, r.height + 1f);
            }

            GUI.DrawTexture(r, t.Texture, ScaleMode.StretchToFill, false);
        }

        private void Enqueue(Key key, Tile t, FigScene scene, float scale, HashSet<int> exclude, float priority)
        {
            t.Queued = true;
            t.Generation++;
            int apron = scene.MaxBackgroundBlur > 0 ? Mathf.Min(96, Mathf.CeilToInt(scene.MaxBackgroundBlur * scale * 1.5f)) : 0;
            var job = new Job
            {
                Key = key,
                Generation = t.Generation,
                Nodes = scene.Nodes,
                Background = scene.Background,
                Scale = scale,
                Version = scene.Version,
                Apron = apron,
                Exclude = exclude != null && exclude.Count > 0 ? new HashSet<int>(exclude) : null,
                X = key.X * TileSize,
                Y = key.Y * TileSize,
                W = TileSize,
                H = TileSize,
                Priority = priority,
            };

            lock (sync)
            {
                queue.Add(job);
                Monitor.Pulse(sync);
            }
        }

        /// <summary>Upload finished tiles (a few per frame) and decode the images they asked for.</summary>
        private bool Pump()
        {
            bool any = false;
            for (int n = 0; n < UploadsPerFrame; n++)
            {
                Job job;
                lock (sync)
                {
                    if (done.Count == 0)
                    {
                        break;
                    }

                    job = done.Dequeue();
                }

                any = true;
                if (job.Only != null)
                {
                    FinishLift(job);
                    continue;
                }

                if (!tiles.TryGetValue(job.Key, out Tile t) || t.Generation != job.Generation)
                {
                    continue;
                }

                t.Queued = false;
                if (t.Texture == null)
                {
                    t.Texture = texturePool.Count > 0 ? texturePool.Pop() : NewTexture(TileSize, TileSize);
                }

                t.Texture.SetPixels32(job.Pixels);
                t.Texture.Apply(false, false);
                t.Version = job.Version;
                t.Stale = job.Version != Scene?.Version;
                t.MissingImages = job.Missing != null && job.Missing.Length > 0 ? new HashSet<string>(job.Missing) : null;
                if (t.MissingImages != null)
                {
                    foreach (string h in job.Missing)
                    {
                        if (!failedImages.Contains(h))
                        {
                            wantedImages.Add(h);
                        }
                    }
                }
            }

            any |= DecodeImages();
            return any;
        }

        private bool DecodeImages()
        {
            if (wantedImages.Count == 0 || ImageBytes == null)
            {
                return false;
            }

            var decoded = new List<string>();
            foreach (string hash in wantedImages)
            {
                if (decoded.Count >= ImageDecodesPerFrame)
                {
                    break;
                }

                decoded.Add(hash);
            }

            Dictionary<string, FigImageData> next = null;
            foreach (string hash in decoded)
            {
                wantedImages.Remove(hash);
                FigImageData data = Decode(ImageBytes(hash));
                if (data == null)
                {
                    failedImages.Add(hash);
                    continue;
                }

                next ??= new Dictionary<string, FigImageData>(images);
                next[hash] = data;
            }

            if (next == null)
            {
                return true;
            }

            images = next;
            foreach (Tile t in tiles.Values)
            {
                if (t.MissingImages != null)
                {
                    foreach (string h in decoded)
                    {
                        if (t.MissingImages.Contains(h))
                        {
                            t.Stale = true;
                            break;
                        }
                    }
                }
            }

            return true;
        }

        private static FigImageData Decode(byte[] bytes)
        {
            if (bytes == null)
            {
                return null;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                if (!tex.LoadImage(bytes, false))
                {
                    return null;
                }

                int w = tex.width, h = tex.height;
                Color32[] bottomUp = tex.GetPixels32();
                var topDown = new Color32[bottomUp.Length];
                for (int y = 0; y < h; y++)
                {
                    Array.Copy(bottomUp, (h - 1 - y) * w, topDown, y * w, w);
                }

                return new FigImageData(w, h, topDown);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static Texture2D NewTexture(int w, int h)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "Fig Editor tile",
            };
        }

        /// <summary>Drop the least recently used tiles over the budget, and tiles of old zooms.</summary>
        private void Trim()
        {
            if (tiles.Count <= MaxTiles)
            {
                return;
            }

            var order = new List<KeyValuePair<Key, Tile>>(tiles);
            order.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));
            int remove = tiles.Count - MaxTiles * 3 / 4;
            foreach (KeyValuePair<Key, Tile> pair in order)
            {
                if (remove-- <= 0 || pair.Value.LastUsed == frame)
                {
                    break;
                }

                if (pair.Value.Texture != null)
                {
                    texturePool.Push(pair.Value.Texture);
                }

                tiles.Remove(pair.Key);
            }

            while (texturePool.Count > 64)
            {
                UnityEngine.Object.DestroyImmediate(texturePool.Pop());
            }
        }

        // ─── Workers ─────────────────────────────────────────────────────────────

        private void Work()
        {
            var renderer = new FigRenderer();
            renderer.Images = hash =>
            {
                Dictionary<string, FigImageData> current = images;
                return current.TryGetValue(hash, out FigImageData d) ? d : null;
            };

            while (!stopping)
            {
                Job job = null;
                lock (sync)
                {
                    while (!stopping && queue.Count == 0)
                    {
                        Monitor.Wait(sync, 250);
                    }

                    if (stopping)
                    {
                        return;
                    }

                    // Closest to the centre of the view first.
                    int best = 0;
                    for (int i = 1; i < queue.Count; i++)
                    {
                        if (queue[i].Priority < queue[best].Priority)
                        {
                            best = i;
                        }
                    }

                    job = queue[best];
                    queue.RemoveAt(best);
                }

                try
                {
                    Color32[] topDown = job.Only != null
                        ? renderer.RenderOnly(job.Nodes, job.Only, job.Scale, job.X, job.Y, job.W, job.H)
                        : renderer.Render(job.Nodes, job.Background, job.Scale, job.X, job.Y, job.W, job.H, job.Apron, job.Exclude);
                    // Textures store rows bottom-up.
                    var pixels = new Color32[topDown.Length];
                    for (int y = 0; y < job.H; y++)
                    {
                        Array.Copy(topDown, y * job.W, pixels, (job.H - 1 - y) * job.W, job.W);
                    }

                    job.Pixels = pixels;
                    if (renderer.Missing.Count > 0)
                    {
                        job.Missing = new string[renderer.Missing.Count];
                        renderer.Missing.CopyTo(job.Missing);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    job.Pixels = new Color32[job.W * job.H];
                }

                lock (sync)
                {
                    done.Enqueue(job);
                }
            }
        }

        // ─── Lifted selection (drag) ─────────────────────────────────────────────

        private Texture2D liftTexture;
        private Rect liftWorld;
        private int liftGeneration;
        private bool liftPending;

        public bool HasLift => liftTexture != null;

        /// <summary>Render the subtrees at <paramref name="roots"/> into one texture, to drag them around without re-rendering.</summary>
        public void Lift(FigScene scene, List<int> roots, float scale, Vector2 offset, Vector2 size)
        {
            ClearLift();
            Rect? bounds = null;
            foreach (int r in roots)
            {
                Rect b = scene.Nodes[r].SubtreeBounds;
                bounds = bounds.HasValue ? FigGeometry.Union(bounds.Value, b) : b;
            }

            if (!bounds.HasValue)
            {
                return;
            }

            // Only the part that can come on screen while dragging (the view grown by its size).
            Rect view = Rect.MinMaxRect(-offset.x / scale - size.x / scale, -offset.y / scale - size.y / scale, (size.x * 2 - offset.x) / scale, (size.y * 2 - offset.y) / scale);
            Rect world = FigGeometry.Intersect(bounds.Value, view);
            int x = Mathf.FloorToInt(world.xMin * scale), y = Mathf.FloorToInt(world.yMin * scale);
            int w = Mathf.Clamp(Mathf.CeilToInt(world.xMax * scale) - x, 1, 4096), h = Mathf.Clamp(Mathf.CeilToInt(world.yMax * scale) - y, 1, 4096);
            liftWorld = new Rect(x / scale, y / scale, w / scale, h / scale);
            liftPending = true;
            var job = new Job
            {
                Generation = ++liftGeneration,
                Nodes = scene.Nodes,
                Scale = scale,
                Only = new List<int>(roots),
                X = x,
                Y = y,
                W = w,
                H = h,
                Priority = -1,
            };

            lock (sync)
            {
                queue.Add(job);
                Monitor.Pulse(sync);
            }
        }

        private void FinishLift(Job job)
        {
            if (job.Generation != liftGeneration || !liftPending)
            {
                return;
            }

            liftPending = false;
            if (liftTexture != null)
            {
                UnityEngine.Object.DestroyImmediate(liftTexture);
            }

            liftTexture = NewTexture(job.W, job.H);
            liftTexture.SetPixels32(job.Pixels);
            liftTexture.Apply(false, true);
        }

        /// <summary>Draw the lifted selection moved by <paramref name="worldDelta"/>.</summary>
        public void DrawLift(float scale, Vector2 offset, Vector2 worldDelta)
        {
            if (liftTexture == null)
            {
                return;
            }

            Rect r = new Rect((liftWorld.x + worldDelta.x) * scale + offset.x, (liftWorld.y + worldDelta.y) * scale + offset.y, liftWorld.width * scale, liftWorld.height * scale);
            GUI.DrawTexture(r, liftTexture, ScaleMode.StretchToFill, true);
        }

        public bool LiftReady => liftTexture != null && !liftPending;

        public void ClearLift()
        {
            liftPending = false;
            liftGeneration++;
            if (liftTexture != null)
            {
                UnityEngine.Object.DestroyImmediate(liftTexture);
                liftTexture = null;
            }
        }

        /// <summary>Whether every tile of the current zoom over <paramref name="world"/> is up to date (layer 0).</summary>
        public bool RegionReady(Rect world, float scale)
        {
            int zoomKey = ZoomKey(scale);
            int tx0 = Mathf.FloorToInt(world.xMin * scale / TileSize), ty0 = Mathf.FloorToInt(world.yMin * scale / TileSize);
            int tx1 = Mathf.FloorToInt(world.xMax * scale / TileSize), ty1 = Mathf.FloorToInt(world.yMax * scale / TileSize);
            if (visibleTiles != null && zoomKey == visibleZoom)
            {
                // Tiles off screen are never requested; only the visible ones need to be ready.
                tx0 = Mathf.Max(tx0, visibleTiles[0]);
                ty0 = Mathf.Max(ty0, visibleTiles[1]);
                tx1 = Mathf.Min(tx1, visibleTiles[2]);
                ty1 = Mathf.Min(ty1, visibleTiles[3]);
            }

            for (int ty = ty0; ty <= ty1; ty++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    if (!tiles.TryGetValue(new Key { Zoom = zoomKey, X = tx, Y = ty }, out Tile t) || t.Stale || t.Version < 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>Forget the tiles drawn without the lifted selection (the drag ended).</summary>
        public void DropLayer1()
        {
            var remove = new List<Key>();
            foreach (KeyValuePair<Key, Tile> pair in tiles)
            {
                if (pair.Key.Layer == 1)
                {
                    remove.Add(pair.Key);
                    if (pair.Value.Texture != null)
                    {
                        texturePool.Push(pair.Value.Texture);
                    }
                }
            }

            foreach (Key k in remove)
            {
                tiles.Remove(k);
            }
        }
    }
}
