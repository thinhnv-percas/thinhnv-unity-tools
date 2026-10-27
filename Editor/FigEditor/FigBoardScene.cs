using System.Collections.Generic;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>One drawable layer of a page, in world (page) space.</summary>
    public sealed class FigBoardItem
    {
        public FigNode Node;
        public string Id;
        public string Type;

        public int Index;
        public int ParentIndex = -1;

        /// <summary>One past the last item of this layer's subtree (items are in paint order, parents first).</summary>
        public int SubtreeEnd;

        /// <summary>Index of the nearest ancestor that clips its content, or -1.</summary>
        public int ClipParent = -1;

        public FigAffine ParentWorld;
        public FigAffine World;
        public float Width;
        public float Height;

        /// <summary>World-space outline of the layer's box (rounded rect / ellipse / rect).</summary>
        public Vector2[] Outline;

        /// <summary>World-space axis-aligned bounds of <see cref="Outline"/>.</summary>
        public Rect Bounds;

        public float Opacity = 1f;
        public bool Locked;
        public bool ClipsContent;
        public bool TopLevel;

        public bool HasFill;
        public Color Fill;
        public string ImageHash;

        /// <summary>STRETCH, FIT, FILL or TILE.</summary>
        public string ImageScaleMode;

        public bool HasStroke;
        public Color Stroke;
        public float StrokeWeight;

        /// <summary>Drawn from its <c>fillGeometry</c> / <c>strokeGeometry</c> blobs (vectors, booleans, stars…).</summary>
        public bool UsesGeometry;

        public string Text;
        public float FontSize;
        public Color TextColor;
        public string TextAlignH;
        public string TextAlignV;
        public bool TextWraps;
        public bool TextBold;
        public bool TextItalic;

        /// <summary>
        /// Identify the geometry blobs a vector draws from (indices, winding, count), so cached rasters
        /// survive scene rebuilds and refresh only when the geometry itself changes.
        /// </summary>
        public int FillSignature;
        public int StrokeSignature;

        public bool IsContainer => Type == "FRAME" || Type == "GROUP" || Type == "SYMBOL" || Type == "INSTANCE" || Type == "SECTION" || Type == "BOOLEAN_OPERATION";

        /// <summary>Whether the board can resize it from its box (primitives, frames, text).</summary>
        public bool Resizable => !UsesGeometry && Type != "GROUP" && Type != "BOOLEAN_OPERATION";
    }

    /// <summary>
    /// The cached, world-space scene of one page: every visible layer in paint order with its
    /// transform, outline, bounds and paint, built once and patched in place when layers move
    /// (<see cref="Refresh"/>) instead of being rebuilt every frame.
    /// </summary>
    public sealed class FigBoardScene
    {
        public const int MaxItems = 50000;

        public FigNode Page;
        public readonly List<FigBoardItem> Items = new();
        public readonly Dictionary<string, int> IndexById = new();
        public Color Background = new Color(0.12f, 0.12f, 0.12f);
        public Rect ContentBounds;
        public bool Truncated;

        private static readonly HashSet<string> PrimitiveTypes = new HashSet<string>
        {
            "FRAME", "GROUP", "SYMBOL", "INSTANCE", "SECTION", "RECTANGLE", "ROUNDED_RECTANGLE", "ELLIPSE", "TEXT", "LINE", "SLICE",
        };

        private readonly struct BuildStep
        {
            public readonly FigNode Node;
            public readonly int Parent;
            public readonly bool Exit;

            public BuildStep(FigNode node, int parent, bool exit)
            {
                Node = node;
                Parent = parent;
                Exit = exit;
            }
        }

        public FigBoardItem Find(string id)
        {
            return id != null && IndexById.TryGetValue(id, out int i) ? Items[i] : null;
        }

        public static FigBoardScene Build(FigNode page)
        {
            var scene = new FigBoardScene { Page = page };
            if (page == null)
            {
                return scene;
            }

            KiwiValue bg = page.Change.Get("backgroundColor");
            if (bg != null)
            {
                scene.Background = ReadColor(bg, 1f);
            }

            // Iterative DFS; an exit step closes the subtree of the item it names.
            var stack = new Stack<BuildStep>();
            for (int i = page.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(new BuildStep(page.Children[i], -1, false));
            }

            while (stack.Count > 0)
            {
                BuildStep step = stack.Pop();
                FigNode node = step.Node;
                int parent = step.Parent;
                if (step.Exit)
                {
                    scene.Items[parent].SubtreeEnd = scene.Items.Count;
                    continue;
                }

                if (node.Change.Get("visible") is KiwiValue visible && !visible.AsBool)
                {
                    continue;
                }

                if (scene.Items.Count >= MaxItems)
                {
                    scene.Truncated = true;
                    break;
                }

                var item = new FigBoardItem
                {
                    Node = node,
                    Id = node.Id,
                    Type = node.Type,
                    Index = scene.Items.Count,
                    ParentIndex = parent,
                    TopLevel = parent < 0,
                };

                FigBoardItem parentItem = parent >= 0 ? scene.Items[parent] : null;
                item.ParentWorld = parentItem?.World ?? FigAffine.Identity;
                item.ClipParent = parentItem == null ? -1 : parentItem.ClipsContent ? parent : parentItem.ClipParent;
                float parentOpacity = parentItem?.Opacity ?? 1f;
                scene.Items.Add(item);
                scene.IndexById[item.Id] = item.Index;

                ReadPlacement(item);
                ReadPaint(item, parentOpacity);

                stack.Push(new BuildStep(node, item.Index, true));
                for (int i = node.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push(new BuildStep(node.Children[i], item.Index, false));
                }
            }

            // A truncated build leaves open subtrees; close them at the end.
            foreach (FigBoardItem item in scene.Items)
            {
                if (item.SubtreeEnd <= item.Index)
                {
                    item.SubtreeEnd = scene.Items.Count;
                }
            }

            scene.UpdateContentBounds();
            return scene;
        }

        public void UpdateContentBounds()
        {
            bool first = true;
            Rect bounds = new Rect(0, 0, 100, 100);
            foreach (FigBoardItem item in Items)
            {
                if (!item.TopLevel)
                {
                    continue;
                }

                bounds = first ? item.Bounds : FigGeometry.Union(bounds, item.Bounds);
                first = false;
            }

            ContentBounds = bounds;
        }

        /// <summary>
        /// Re-read the transform, size and paint of <paramref name="index"/> and re-place its whole
        /// subtree, after that layer was moved, resized or restyled. Cost: the subtree's size.
        /// </summary>
        public void Refresh(int index, bool paint)
        {
            if (index < 0 || index >= Items.Count)
            {
                return;
            }

            FigBoardItem root = Items[index];
            for (int i = index; i < root.SubtreeEnd; i++)
            {
                FigBoardItem item = Items[i];
                FigBoardItem parent = item.ParentIndex >= 0 ? Items[item.ParentIndex] : null;
                item.ParentWorld = parent?.World ?? FigAffine.Identity;
                ReadPlacement(item);
                if (paint)
                {
                    // Opacity multiplies down the tree, so the whole subtree repaints.
                    ReadPaint(item, parent?.Opacity ?? 1f);
                }
            }
        }

        /// <summary>
        /// After <paramref name="index"/>'s own transform or size changed (a move or resize): re-read
        /// that layer only and carry its descendants along by the same change of transform, reusing
        /// their cached outlines. Much cheaper than <see cref="Refresh"/> for big subtrees.
        /// </summary>
        public void RefreshPlacement(int index)
        {
            if (index < 0 || index >= Items.Count)
            {
                return;
            }

            FigBoardItem root = Items[index];
            FigAffine before = root.World;
            ReadPlacement(root);
            FigAffine delta = root.World.Then(before.Inverse());
            for (int i = index + 1; i < root.SubtreeEnd; i++)
            {
                FigBoardItem item = Items[i];
                item.ParentWorld = delta.Then(item.ParentWorld);
                item.World = delta.Then(item.World);
                Vector2[] outline = item.Outline;
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                for (int k = 0; k < outline.Length; k++)
                {
                    Vector2 p = delta.Apply(outline[k]);
                    outline[k] = p;
                    x0 = Mathf.Min(x0, p.x);
                    y0 = Mathf.Min(y0, p.y);
                    x1 = Mathf.Max(x1, p.x);
                    y1 = Mathf.Max(y1, p.y);
                }

                item.Bounds = Rect.MinMaxRect(x0, y0, x1, y1);
            }
        }

        private static void ReadPlacement(FigBoardItem item)
        {
            KiwiValue change = item.Node.Change;
            item.World = item.ParentWorld.Then(FigAffine.FromMatrix(change.Get("transform")));
            KiwiValue size = change.Get("size");
            item.Width = Mathf.Max(0, size?.Get("x")?.AsFloat ?? 0);
            item.Height = Mathf.Max(0, size?.Get("y")?.AsFloat ?? 0);

            Vector2[] local;
            if (item.Type == "ELLIPSE")
            {
                local = FigGeometry.Ellipse(item.Width, item.Height);
            }
            else
            {
                float radius = change.Get("cornerRadius")?.AsFloat ?? 0;
                bool independent = change.Get("rectangleCornerRadiiIndependent")?.AsBool ?? false;
                float tl = independent ? change.Get("rectangleTopLeftCornerRadius")?.AsFloat ?? radius : radius;
                float tr = independent ? change.Get("rectangleTopRightCornerRadius")?.AsFloat ?? radius : radius;
                float br = independent ? change.Get("rectangleBottomRightCornerRadius")?.AsFloat ?? radius : radius;
                float bl = independent ? change.Get("rectangleBottomLeftCornerRadius")?.AsFloat ?? radius : radius;
                local = FigGeometry.RoundedRect(item.Width, item.Height, tl, tr, br, bl);
            }

            var world = new Vector2[local.Length];
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < local.Length; i++)
            {
                Vector2 p = item.World.Apply(local[i]);
                world[i] = p;
                x0 = Mathf.Min(x0, p.x);
                y0 = Mathf.Min(y0, p.y);
                x1 = Mathf.Max(x1, p.x);
                y1 = Mathf.Max(y1, p.y);
            }

            item.Outline = world;
            item.Bounds = Rect.MinMaxRect(x0, y0, x1, y1);
            item.ClipsContent = item.IsContainer && item.Type != "GROUP" && item.Type != "BOOLEAN_OPERATION" &&
                                !(change.Get("frameMaskDisabled")?.AsBool ?? false) && item.World.AxisAligned;
        }

        private static void ReadPaint(FigBoardItem item, float parentOpacity)
        {
            KiwiValue change = item.Node.Change;
            item.Opacity = parentOpacity * (change.Get("opacity")?.AsFloat ?? 1f);
            item.Locked = change.Get("locked")?.AsBool ?? false;
            item.UsesGeometry = !PrimitiveTypes.Contains(item.Type) && (CountOf(change, "fillGeometry") > 0 || CountOf(change, "strokeGeometry") > 0);
            item.FillSignature = item.UsesGeometry ? Signature(change.Get("fillGeometry")) : 0;
            item.StrokeSignature = item.UsesGeometry ? Signature(change.Get("strokeGeometry")) : 0;

            item.HasFill = false;
            item.ImageHash = null;
            List<KiwiValue> fills = change.Get("fillPaints")?.Items;
            if (fills != null && item.Type != "TEXT")
            {
                // The top-most visible paint wins (paints are listed bottom to top).
                for (int i = fills.Count - 1; i >= 0; i--)
                {
                    if (TopPaint(fills[i], item.Opacity, out Color color, out string hash, out string mode))
                    {
                        item.HasFill = true;
                        item.Fill = color;
                        item.ImageHash = hash;
                        item.ImageScaleMode = mode;
                        break;
                    }
                }
            }

            item.HasStroke = false;
            List<KiwiValue> strokes = change.Get("strokePaints")?.Items;
            float weight = change.Get("strokeWeight")?.AsFloat ?? 1f;
            if (strokes != null && weight > 0 && item.Type != "TEXT")
            {
                for (int i = strokes.Count - 1; i >= 0; i--)
                {
                    if (TopPaint(strokes[i], item.Opacity, out Color color, out _, out _))
                    {
                        item.HasStroke = true;
                        item.Stroke = color;
                        item.StrokeWeight = weight;
                        break;
                    }
                }
            }

            item.Text = null;
            if (item.Type == "TEXT")
            {
                item.Text = change.Path("textData.characters")?.String ?? "";
                item.FontSize = change.Get("fontSize")?.AsFloat ?? 12f;
                item.TextAlignH = change.Get("textAlignHorizontal")?.EnumName ?? "LEFT";
                item.TextAlignV = change.Get("textAlignVertical")?.EnumName ?? "TOP";
                item.TextWraps = (change.Get("textAutoResize")?.EnumName ?? "NONE") != "WIDTH_AND_HEIGHT";
                string style = change.Path("fontName.style")?.String ?? "";
                item.TextBold = style.IndexOf("Bold", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                style.IndexOf("Black", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                style.IndexOf("Heavy", System.StringComparison.OrdinalIgnoreCase) >= 0;
                item.TextItalic = style.IndexOf("Italic", System.StringComparison.OrdinalIgnoreCase) >= 0;
                item.TextColor = new Color(0, 0, 0, item.Opacity);
                if (fills != null)
                {
                    for (int i = fills.Count - 1; i >= 0; i--)
                    {
                        if (TopPaint(fills[i], item.Opacity, out Color color, out string hash, out _) && hash == null)
                        {
                            item.TextColor = color;
                            break;
                        }
                    }
                }
            }
        }

        private static int Signature(KiwiValue paths)
        {
            List<KiwiValue> list = paths?.Items;
            if (list == null)
            {
                return 0;
            }

            unchecked
            {
                int h = 17 + list.Count;
                foreach (KiwiValue path in list)
                {
                    h = h * 31 + (int)(path.Get("commandsBlob")?.Uint ?? 0);
                    h = h * 31 + (int)(path.Get("windingRule")?.EnumValue ?? 0);
                }

                return h;
            }
        }

        private static int CountOf(KiwiValue change, string field)
        {
            return change.Get(field)?.Items?.Count ?? 0;
        }

        /// <summary>The colour (or image) of a visible paint; false for hidden or fully transparent ones.</summary>
        private static bool TopPaint(KiwiValue paint, float opacity, out Color color, out string imageHash, out string scaleMode)
        {
            color = default;
            imageHash = null;
            scaleMode = null;
            if (paint.Get("visible") is KiwiValue v && !v.AsBool)
            {
                return false;
            }

            string type = paint.Get("type")?.EnumName;
            float alpha = (paint.Get("opacity")?.AsFloat ?? 1f) * opacity;
            if (alpha <= 0.001f)
            {
                return false;
            }

            if (type == "IMAGE")
            {
                byte[] hash = paint.Path("image.hash")?.Bytes;
                if (hash == null || hash.Length == 0)
                {
                    return false;
                }

                imageHash = FigUtil.Hex(hash);
                scaleMode = paint.Get("imageScaleMode")?.EnumName ?? "FILL";
                color = new Color(1, 1, 1, alpha);
                return true;
            }

            KiwiValue c = paint.Get("color");
            // Gradients show as the average of their stops.
            List<KiwiValue> stops = type != "SOLID" ? paint.Get("stops")?.Items : null;
            if (stops != null && stops.Count > 0)
            {
                float r = 0, g = 0, b = 0, a = 0;
                int n = 0;
                foreach (KiwiValue stop in stops)
                {
                    KiwiValue sc = stop.Get("color");
                    if (sc == null)
                    {
                        continue;
                    }

                    Color s = ReadColor(sc, 1f);
                    r += s.r;
                    g += s.g;
                    b += s.b;
                    a += s.a;
                    n++;
                }

                if (n > 0)
                {
                    color = new Color(r / n, g / n, b / n, a / n * alpha);
                    return color.a > 0.001f;
                }
            }

            if (c == null)
            {
                return false;
            }

            color = ReadColor(c, alpha);
            return color.a > 0.001f;
        }

        public static Color ReadColor(KiwiValue c, float alpha)
        {
            return new Color(c.Get("r")?.AsFloat ?? 0, c.Get("g")?.AsFloat ?? 0, c.Get("b")?.AsFloat ?? 0, (c.Get("a")?.AsFloat ?? 1f) * alpha);
        }

        // ─── Queries ─────────────────────────────────────────────────────────────

        /// <summary>The top-most item whose outline contains <paramref name="p"/> (skips locked ones), or -1.</summary>
        public int HitTest(Vector2 p, bool includeLocked = false)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                FigBoardItem item = Items[i];
                if ((!includeLocked && item.Locked) || !item.Bounds.Contains(p))
                {
                    continue;
                }

                if (item.ClipParent >= 0 && !Items[item.ClipParent].Bounds.Contains(p))
                {
                    continue;
                }

                // Vectors and text hit on their box; shapes on their outline.
                if (item.UsesGeometry || item.Text != null || FigGeometry.Contains(item.Outline, p))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Ancestor chain of <paramref name="index"/>, top-level first, ending with the item itself.</summary>
        public List<int> Chain(int index)
        {
            var chain = new List<int>();
            for (int i = index; i >= 0; i = Items[i].ParentIndex)
            {
                chain.Add(i);
            }

            chain.Reverse();
            return chain;
        }

        public bool IsAncestor(int ancestor, int index)
        {
            return ancestor >= 0 && index >= ancestor && index < Items[ancestor].SubtreeEnd;
        }
    }
}
