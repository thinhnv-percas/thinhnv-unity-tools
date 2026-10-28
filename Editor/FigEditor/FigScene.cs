using System;
using System.Collections.Generic;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>Glyph outlines of one text style, ready to fill.</summary>
    public sealed class FigGlyphRun
    {
        public FigPath Path;
        public List<FigPaint> Fills;

        /// <summary>Extra stroke (layer units) thickening a face lighter than the style asks for.</summary>
        public float Embolden;
    }

    /// <summary>
    /// One drawable layer of a page in paint order, with everything the renderer needs resolved
    /// out of the file (geometry, paints, effects, glyphs). Render fields never change after the
    /// node is built: an edit makes a new node (<see cref="FigScene"/> is copy-on-write), so the
    /// renderer threads can keep drawing an older snapshot safely.
    /// </summary>
    public sealed class FigSceneNode
    {
        // Identity
        public string Id;
        public FigNode Source;
        public FigNode Owner;
        public bool Expanded;
        public FigView View;
        public string Type;
        public string Name;

        // Tree (indices into the scene's node array)
        public int Index;
        public int ParentIndex = -1;
        public int SubtreeEnd;
        public int Depth;

        // Placement
        public FigAffine Local;
        public FigAffine ParentWorld;
        public FigAffine World;
        public float Width;
        public float Height;
        public Vector2[] Corners;
        public Rect Bounds;
        public Rect PaintBounds;
        public Rect SubtreeBounds;

        // Rendering
        public List<FigPath> FillPaths;
        public List<FigPath> StrokePaths;
        public bool StrokeFromOutline;
        public float StrokeWeight;
        public string StrokeAlign;
        public List<FigPaint> Fills;
        public List<FigPaint> Strokes;
        public List<FigEffect> Effects;
        public List<FigGlyphRun> Glyphs;
        public float Opacity = 1f;
        public FigBlend Blend;
        public bool ClipsContent;
        public bool IsMask;
        public bool Locked;
        public bool SkipChildren;
        public bool StrokeAboveChildren;
        public float CornerRadius;

        // Text drawn by the UI when the file has no glyph outlines for it
        public bool FallbackText;

        /// <summary>The glyphs were laid out here with installed fonts (not Figma's cached outlines).</summary>
        public bool GlyphsFromLayout;

        /// <summary>Fonts that aren't installed and what drew them instead.</summary>
        public string FontNote;
        public string Text;
        public float FontSize = 12f;
        public string FontFamily;
        public string FontStyle;
        public string TextAlignH = "LEFT";
        public string TextAlignV = "TOP";
        public bool TextWraps = true;
        public Color TextColor = Color.black;

        public bool IsContainer => Type == "FRAME" || Type == "GROUP" || Type == "SYMBOL" || Type == "INSTANCE" || Type == "SECTION" || Type == "BOOLEAN_OPERATION" || Type == "COMPONENT_SET";

        public bool IsFrameLike => Type == "FRAME" || Type == "SYMBOL" || Type == "INSTANCE" || Type == "SECTION" || Type == "COMPONENT_SET";

        /// <summary>Whether the board can resize it from its box (not vectors, groups or instance internals).</summary>
        public bool Resizable => !Expanded && Type != "GROUP" && Type != "BOOLEAN_OPERATION" && (FillPaths == null || IsPrimitive(Type));

        public bool NeedsIsolation => (Opacity < 0.999f && SubtreeEnd > Index + 1 && !SkipChildren) || (Effects != null && Effects.Count > 0) || (Blend != FigBlend.Normal && SubtreeEnd > Index + 1);

        public static bool IsPrimitive(string type)
        {
            return type == "RECTANGLE" || type == "ROUNDED_RECTANGLE" || type == "ELLIPSE" || type == "FRAME" || type == "SYMBOL" ||
                   type == "INSTANCE" || type == "SECTION" || type == "COMPONENT_SET" || type == "LINE" || type == "TEXT";
        }

        internal FigSceneNode Copy()
        {
            return (FigSceneNode)MemberwiseClone();
        }
    }

    /// <summary>
    /// The page as a flat list of drawable layers in paint order (parents first, subtree ranges
    /// contiguous), with instances expanded from their main components the way figcraft does: the
    /// main's layers seen through the instance's override rows and derived layout (by GUID path,
    /// outer instances winning), component properties (visibility, text, instance swaps), and
    /// constraint fitting when an instance is resized without derived layout.
    /// </summary>
    public sealed class FigScene
    {
        public const int MaxNodes = 100000;
        private const int MaxDepth = 64;

        public FigNode Page;
        public FigSceneNode[] Nodes = Array.Empty<FigSceneNode>();
        public readonly Dictionary<string, int> IndexById = new();
        public Color Background = new Color(0.118f, 0.118f, 0.118f);
        public Rect ContentBounds;
        public bool Truncated;
        public int Version;

        /// <summary>Largest background blur (layer units), so tiles can render with enough margin.</summary>
        public float MaxBackgroundBlur;

        private FigFile file;
        private List<KiwiValue> blobs;
        private readonly Dictionary<string, string> propParents = new();
        private readonly Dictionary<string, Scope> scopes = new();
        private readonly List<FigSceneNode> building = new();

        public FigSceneNode Find(string id)
        {
            return id != null && IndexById.TryGetValue(id, out int i) && i < Nodes.Length ? Nodes[i] : null;
        }

        // ─── Build ───────────────────────────────────────────────────────────────

        public static FigScene Build(FigFile file, FigNode page)
        {
            var scene = new FigScene { Page = page, file = file, blobs = file?.Message.Get("blobs")?.Items };
            if (page == null)
            {
                return scene;
            }

            KiwiValue bg = page.Change.Get("backgroundColor");
            if (bg != null)
            {
                scene.Background = FigPaint.ReadColor(bg);
                scene.Background.a = 1f;
            }

            scene.CollectPropParents();
            foreach (FigNode child in page.Children)
            {
                if (!scene.Emit(child, -1, null, null, null, 0))
                {
                    break;
                }
            }

            scene.Nodes = scene.building.ToArray();
            scene.building.Clear();
            for (int i = 0; i < scene.Nodes.Length; i++)
            {
                scene.IndexById[scene.Nodes[i].Id] = i;
            }

            scene.ComputeSubtreeBounds();
            scene.UpdateContentBounds();
            return scene;
        }

        private void CollectPropParents()
        {
            if (file == null)
            {
                return;
            }

            foreach (FigNode n in file.Nodes.Values)
            {
                List<KiwiValue> defs = n.Change.Get("componentPropDefs")?.Items;
                if (defs == null)
                {
                    continue;
                }

                foreach (KiwiValue d in defs)
                {
                    string id = FigUtil.GuidString(d.Get("id"));
                    string parent = FigUtil.GuidString(d.Get("parentPropDefId"));
                    if (id != null && parent != null && parent != id)
                    {
                        propParents[id] = parent;
                    }
                }
            }
        }

        private string PropRoot(string def)
        {
            for (int i = 0; i <= MaxDepth && def != null && propParents.TryGetValue(def, out string parent); i++)
            {
                def = parent;
            }

            return def;
        }

        /// <summary>One override or derived-layout row addressed to a layer of an instance.</summary>
        private struct Row
        {
            public KiwiValue Value;
            public bool Derived;
            public bool Stale;
            public int SourceDepth;
            public int Ordinal;
        }

        /// <summary>The expansion context of an instance (figcraft's <c>Inst</c>).</summary>
        private sealed class Inst
        {
            public string RootSceneId;
            public FigNode Owner;
            public List<string> Path = new();
            public Dictionary<string, List<Row>> Overrides = new();
            public Dictionary<string, KiwiValue> Props = new();
            public List<string> Symbols = new();
        }

        /// <summary>A main's layer ids, and the old <c>overrideKey</c> aliases (null when ambiguous).</summary>
        private sealed class Scope
        {
            public readonly HashSet<string> Current = new();
            public readonly Dictionary<string, string> Aliases = new();
        }

        /// <summary>How a resized container's children follow it when the file has no derived layout.</summary>
        private sealed class Fit
        {
            public bool Scale;
            public Vector2 From;
            public Vector2 To;
        }

        private Scope ScopeOf(string main)
        {
            if (scopes.TryGetValue(main, out Scope cached))
            {
                return cached;
            }

            var scope = new Scope();
            FigNode root = file.Find(main);
            var pending = new Stack<FigNode>();
            if (root != null)
            {
                foreach (FigNode c in root.Children)
                {
                    pending.Push(c);
                }
            }

            int visited = 0;
            while (pending.Count > 0 && visited < MaxNodes)
            {
                FigNode n = pending.Pop();
                visited++;
                if (!scope.Current.Add(n.Id))
                {
                    continue;
                }

                string key = FigUtil.GuidString(n.Change.Get("overrideKey"));
                if (key != null)
                {
                    scope.Aliases[key] = scope.Aliases.TryGetValue(key, out string existing) && existing != n.Id ? null : n.Id;
                }

                if (n.Type != "INSTANCE")
                {
                    foreach (FigNode c in n.Children)
                    {
                        pending.Push(c);
                    }
                }
            }

            scopes[main] = scope;
            return scope;
        }

        private static void Normalize(List<string> path, int index, Scope scope)
        {
            if (index >= path.Count || scope.Current.Contains(path[index]))
            {
                return;
            }

            if (scope.Aliases.TryGetValue(path[index], out string current) && current != null)
            {
                path[index] = current;
            }
        }

        private static string Key(List<string> path) => string.Join(";", path);

        /// <summary>Emit <paramref name="source"/> and its subtree. Returns false once the node budget is spent.</summary>
        private bool Emit(FigNode source, int parent, Inst inst, Fit fit, FigSceneNode parentNode, int depth)
        {
            if (depth > MaxDepth)
            {
                return true;
            }

            if (building.Count >= MaxNodes)
            {
                Truncated = true;
                return false;
            }

            // The layer seen through the instance's rows.
            List<string> path = null;
            List<KiwiValue> rows = null;
            HashSet<KiwiValue> stale = null;
            bool derivedPlacement = false;
            if (inst != null)
            {
                path = new List<string>(inst.Path) { source.Id };
                if (inst.Overrides.TryGetValue(Key(path), out List<Row> found))
                {
                    rows = new List<KiwiValue>(found.Count);
                    foreach (Row r in found)
                    {
                        rows.Add(r.Value);
                        if (r.Stale)
                        {
                            (stale ??= new HashSet<KiwiValue>()).Add(r.Value);
                        }

                        if (r.Derived && !r.Stale && (r.Value.Get("size") != null || r.Value.Get("transform") != null))
                        {
                            derivedPlacement = true;
                        }
                    }
                }
            }

            var view = new FigView(source.Change, rows, stale);
            bool visible = view.Bool("visible", true);
            if (inst != null)
            {
                // Component properties bound to this layer.
                ApplyPropRefs(view, inst, ref visible, out string propText);
                if (propText != null)
                {
                    view = WithText(view, propText);
                }
            }

            if (!visible)
            {
                return true;
            }

            var node = new FigSceneNode
            {
                Id = inst == null ? source.Id : "I" + inst.RootSceneId + ";" + Key(path),
                Source = source,
                Owner = inst?.Owner ?? source,
                Expanded = inst != null,
                View = view,
                Type = view.EnumName("type") ?? source.Type,
                Name = view.Str("name") ?? "",
                Index = building.Count,
                ParentIndex = parent,
                Depth = depth,
            };

            // Placement: the view's size and transform, or where constraints put it in a resized parent.
            KiwiValue size = view.Get("size");
            node.Width = Mathf.Max(0, size?.Get("x")?.AsFloat ?? 0);
            node.Height = Mathf.Max(0, size?.Get("y")?.AsFloat ?? 0);
            node.Local = FigAffine.FromMatrix(view.Get("transform"));
            bool stacked = parentNode != null && IsStacked(parentNode.View) && view.EnumName("stackPositioning") != "ABSOLUTE";
            if (fit != null && !derivedPlacement && !stacked)
            {
                ApplyFit(fit, node, view);
            }

            node.ParentWorld = parentNode?.World ?? FigAffine.Identity;
            building.Add(node);
            Place(node);
            Resolve(node);

            // Children: an instance draws its main's layers; everything else its own.
            FigNode childSource = source;
            Inst childInst = inst;
            if (node.Type == "INSTANCE" && file != null)
            {
                Inst expanded = Expand(source, view, inst, path ?? new List<string>(), node);
                if (expanded != null)
                {
                    childInst = expanded;
                    childSource = file.Find(expanded.Symbols[expanded.Symbols.Count - 1]);
                }
                else if (inst != null)
                {
                    childSource = null;
                }
            }

            // A container drawn at another size than its own (main) size moves its children with it.
            Fit childFit = null;
            float baseW = source.Change.Get("size")?.Get("x")?.AsFloat ?? node.Width;
            float baseH = source.Change.Get("size")?.Get("y")?.AsFloat ?? node.Height;
            if (childSource != null && childSource != source)
            {
                baseW = childSource.Change.Get("size")?.Get("x")?.AsFloat ?? baseW;
                baseH = childSource.Change.Get("size")?.Get("y")?.AsFloat ?? baseH;
            }

            if (Mathf.Abs(baseW - node.Width) > 1e-3f || Mathf.Abs(baseH - node.Height) > 1e-3f)
            {
                childFit = new Fit
                {
                    Scale = node.Type == "GROUP" || node.Type == "BOOLEAN_OPERATION",
                    From = new Vector2(baseW, baseH),
                    To = new Vector2(node.Width, node.Height),
                };
            }

            if (childSource != null)
            {
                foreach (FigNode child in childSource.Children)
                {
                    if (!Emit(child, node.Index, childInst, childFit, node, depth + 1))
                    {
                        node.SubtreeEnd = building.Count;
                        return false;
                    }
                }
            }

            node.SubtreeEnd = building.Count;
            return true;
        }

        private static bool IsStacked(FigView view)
        {
            string mode = view?.EnumName("stackMode");
            return mode == "HORIZONTAL" || mode == "VERTICAL" || mode == "GRID";
        }

        /// <summary>The instance's expansion context: main, overrides by path, properties.</summary>
        private Inst Expand(FigNode instance, FigView view, Inst outer, List<string> ownPathSoFar, FigSceneNode node)
        {
            string sym = FigUtil.GuidString(view.Get("overriddenSymbolID")) ?? FigUtil.GuidString(view.Path("symbolData.symbolID"));
            if (outer != null)
            {
                string swapDef = PropRef(view, "OVERRIDDEN_SYMBOL_ID");
                if (swapDef != null && outer.Props.TryGetValue(PropRoot(swapDef) ?? swapDef, out KiwiValue swap))
                {
                    sym = PropGuid(swap) ?? sym;
                }
            }

            if (sym == null || file.Find(sym) == null || (outer != null && outer.Symbols.Contains(sym)))
            {
                return null;
            }

            List<string> ownPath = outer != null ? new List<string>(ownPathSoFar) : new List<string>();
            Scope scope = ScopeOf(sym);
            var inst = new Inst
            {
                RootSceneId = outer?.RootSceneId ?? instance.Id,
                Owner = outer?.Owner ?? instance,
                Path = ownPath,
            };

            if (outer != null)
            {
                foreach (KeyValuePair<string, List<Row>> pair in outer.Overrides)
                {
                    var p = new List<string>(pair.Key.Split(';'));
                    if (StartsWith(p, ownPath) && p.Count > ownPath.Count)
                    {
                        Normalize(p, ownPath.Count, scope);
                    }

                    string key = Key(p);
                    if (!inst.Overrides.TryGetValue(key, out List<Row> list))
                    {
                        inst.Overrides[key] = list = new List<Row>();
                    }

                    list.AddRange(pair.Value);
                }

                foreach (KeyValuePair<string, KiwiValue> prop in outer.Props)
                {
                    inst.Props[prop.Key] = prop.Value;
                }

                inst.Symbols.AddRange(outer.Symbols);
            }

            // A nested instance resized from its size in the main: its derived layout is stale.
            bool resized = false;
            if (outer != null)
            {
                KiwiValue a = instance.Change.Get("size");
                KiwiValue b = view.Get("size");
                resized = a != null && b != null && (Mathf.Abs(a.Get("x").AsFloat - b.Get("x").AsFloat) > 1e-3f || Mathf.Abs(a.Get("y").AsFloat - b.Get("y").AsFloat) > 1e-3f);
            }

            int sourceDepth = outer?.Symbols.Count ?? 0;
            int ordinal = 0;
            List<KiwiValue>[] lists = { view.Path("symbolData.symbolOverrides")?.Items, view.Get("derivedSymbolData")?.Items };
            for (int li = 0; li < lists.Length; li++)
            {
                if (lists[li] == null)
                {
                    continue;
                }

                foreach (KiwiValue ov in lists[li])
                {
                    int rowOrdinal = ordinal++;
                    List<KiwiValue> guids = ov.Path("guidPath.guids")?.Items;
                    if (guids == null || guids.Count == 0)
                    {
                        continue;
                    }

                    var rel = new List<string>(guids.Count);
                    foreach (KiwiValue g in guids)
                    {
                        string s = FigUtil.GuidString(g);
                        if (s != null)
                        {
                            rel.Add(s);
                        }
                    }

                    if (rel.Count == 0 || (rel.Count == 1 && rel[0] == sym))
                    {
                        continue;
                    }

                    Normalize(rel, 0, scope);
                    var full = new List<string>(ownPath);
                    full.AddRange(rel);
                    string key = Key(full);
                    if (!inst.Overrides.TryGetValue(key, out List<Row> list))
                    {
                        inst.Overrides[key] = list = new List<Row>();
                    }

                    list.Add(new Row { Value = ov, Derived = li == 1, Stale = li == 1 && resized, SourceDepth = sourceDepth, Ordinal = rowOrdinal });
                }
            }

            foreach (List<Row> list in inst.Overrides.Values)
            {
                // Deeper sources first; the outer instance's rows apply last and win.
                list.Sort((x, y) => x.SourceDepth != y.SourceDepth ? y.SourceDepth.CompareTo(x.SourceDepth) : x.Ordinal.CompareTo(y.Ordinal));
            }

            List<KiwiValue> assignments = view.Get("componentPropAssignments")?.Items;
            if (assignments != null)
            {
                foreach (KiwiValue a in assignments)
                {
                    string def = FigUtil.GuidString(a.Get("defID"));
                    KiwiValue value = Assigned(a);
                    if (def == null || value == null)
                    {
                        continue;
                    }

                    string root = PropRoot(def) ?? def;
                    if (!inst.Props.ContainsKey(root))
                    {
                        inst.Props[root] = value;
                    }
                }
            }

            inst.Symbols.Add(sym);
            return inst;
        }

        private static bool StartsWith(List<string> path, List<string> prefix)
        {
            if (path.Count < prefix.Count)
            {
                return false;
            }

            for (int i = 0; i < prefix.Count; i++)
            {
                if (path[i] != prefix[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The value an assignment sets: <c>value</c>, or <c>varValue.value</c> when that is empty.</summary>
        private static KiwiValue Assigned(KiwiValue a)
        {
            KiwiValue v = a.Get("value");
            if (v != null && !(v.Kind == KiwiValueKind.Object && v.Members.Count == 0))
            {
                return v;
            }

            return a.Path("varValue.value");
        }

        private static string PropGuid(KiwiValue v)
        {
            return FigUtil.GuidString(v.Get("guidValue")) ?? FigUtil.GuidString(v.Path("symbolIdValue.guid"));
        }

        private static string PropRef(FigView view, string field)
        {
            List<KiwiValue> entries = view.Path("parameterConsumptionMap.entries")?.Items;
            if (entries == null)
            {
                return null;
            }

            foreach (KiwiValue e in entries)
            {
                if (e.Get("variableField")?.EnumName == field)
                {
                    return FigUtil.GuidString(e.Path("variableData.value.propRefValue.defId"));
                }
            }

            return null;
        }

        private void ApplyPropRefs(FigView view, Inst inst, ref bool visible, out string text)
        {
            text = null;
            List<KiwiValue> entries = view.Path("parameterConsumptionMap.entries")?.Items;
            if (entries == null)
            {
                return;
            }

            foreach (KiwiValue e in entries)
            {
                string def = FigUtil.GuidString(e.Path("variableData.value.propRefValue.defId"));
                if (def == null || !inst.Props.TryGetValue(PropRoot(def) ?? def, out KiwiValue value))
                {
                    continue;
                }

                switch (e.Get("variableField")?.EnumName)
                {
                    case "VISIBLE":
                        if (value.Get("boolValue") is KiwiValue b)
                        {
                            visible = b.AsBool;
                        }

                        break;
                    case "TEXT_DATA":
                        text = (value.Get("textValue") ?? value.Get("textDataValue"))?.Get("characters")?.String ?? text;
                        break;
                }
            }
        }

        /// <summary>The view with its characters replaced by a component property's text.</summary>
        private static FigView WithText(FigView view, string text)
        {
            KiwiValue current = view.Get("textData");
            if (current == null || current.Get("characters")?.String == text)
            {
                return view;
            }

            KiwiValue copy = current.Clone();
            copy.Set("characters", KiwiValue.FromString(text));
            KiwiValue row = KiwiValue.FromObject(view.Base.Def, new List<KiwiMember>());
            row.Set("textData", copy);
            var rows = view.Rows != null ? new List<KiwiValue>(view.Rows) : new List<KiwiValue>();
            rows.Add(row);
            return new FigView(view.Base, rows, view.StaleRows);
        }

        /// <summary>
        /// Where a <c>w × h</c> layer goes when its parent is drawn at another size and the file says
        /// nothing about it: frames by the layer's constraints, groups by scaling (figcraft's Fit).
        /// </summary>
        private static void ApplyFit(Fit fit, FigSceneNode node, FigView view)
        {
            FigAffine t = node.Local;
            Rect bb = FigGeometry.TransformBounds(t, new Rect(0, 0, node.Width, node.Height));
            float kx, ky, x0, y0;
            bool centred;
            if (fit.Scale)
            {
                kx = fit.From.x > 1e-6f ? fit.To.x / fit.From.x : 1f;
                ky = fit.From.y > 1e-6f ? fit.To.y / fit.From.y : 1f;
                x0 = bb.center.x * kx;
                y0 = bb.center.y * ky;
                centred = true;
            }
            else
            {
                Vector2 xs = Axis(view.EnumName("horizontalConstraint"), bb.xMin, bb.xMax, fit.From.x, fit.To.x);
                Vector2 ys = Axis(view.EnumName("verticalConstraint"), bb.yMin, bb.yMax, fit.From.y, fit.To.y);
                kx = bb.width > 1e-6f ? Mathf.Max(0, (xs.y - xs.x) / bb.width) : 1f;
                ky = bb.height > 1e-6f ? Mathf.Max(0, (ys.y - ys.x) / bb.height) : 1f;
                x0 = xs.x;
                y0 = ys.x;
                centred = false;
            }

            float w = node.Width * Along(t.A, t.D, kx, ky, kx);
            float h = node.Height * Along(t.B, t.E, kx, ky, ky);
            var lin = new FigAffine { A = t.A, B = t.B, D = t.D, E = t.E };
            Rect nb = FigGeometry.TransformBounds(lin, new Rect(0, 0, w, h));
            Vector2 at = centred ? nb.center : new Vector2(nb.xMin, nb.yMin);
            lin.C = x0 - at.x;
            lin.F = y0 - at.y;
            node.Width = w;
            node.Height = h;
            node.Local = lin;
        }

        /// <summary>Where a constrained edge pair [lo, hi] goes when the parent axis goes from old to new.</summary>
        private static Vector2 Axis(string constraint, float lo, float hi, float old, float neu)
        {
            switch (constraint)
            {
                case "MAX":
                    return new Vector2(lo + neu - old, hi + neu - old);
                case "STRETCH":
                    return new Vector2(lo, hi + neu - old);
                case "CENTER":
                    return new Vector2(lo + (neu - old) / 2f, hi + (neu - old) / 2f);
                case "SCALE":
                    return old > 1e-6f ? new Vector2(lo * neu / old, hi * neu / old) : new Vector2(lo, hi);
                default:
                    return new Vector2(lo, hi);
            }
        }

        /// <summary>How much a layer axis (x, y) stretches under scales kx, ky: it keeps rotation, stretches along itself.</summary>
        private static float Along(float x, float y, float kx, float ky, float fallback)
        {
            float len = Mathf.Sqrt(x * x + y * y);
            if (len <= 1e-9f)
            {
                return fallback;
            }

            float k = Mathf.Sqrt(kx * x * kx * x + ky * y * ky * y) / len;
            return float.IsNaN(k) || float.IsInfinity(k) ? fallback : k;
        }

        // ─── Placement and paint ─────────────────────────────────────────────────

        private static void Place(FigSceneNode node)
        {
            node.World = node.ParentWorld.Then(node.Local);
            var corners = new Vector2[4];
            corners[0] = node.World.Apply(0, 0);
            corners[1] = node.World.Apply(node.Width, 0);
            corners[2] = node.World.Apply(node.Width, node.Height);
            corners[3] = node.World.Apply(0, node.Height);
            node.Corners = corners;
            node.Bounds = BoundsOf(corners);
        }

        private static Rect BoundsOf(Vector2[] pts)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (Vector2 p in pts)
            {
                x0 = Mathf.Min(x0, p.x);
                y0 = Mathf.Min(y0, p.y);
                x1 = Mathf.Max(x1, p.x);
                y1 = Mathf.Max(y1, p.y);
            }

            return Rect.MinMaxRect(x0, y0, x1, y1);
        }

        /// <summary>Read the render data of <paramref name="node"/> from its view.</summary>
        private void Resolve(FigSceneNode node)
        {
            FigView v = node.View;
            string type = node.Type;
            node.Opacity = Mathf.Clamp01(v.Float("opacity", 1f));
            node.Blend = FigPaint.ReadBlend(v.EnumName("blendMode"));
            node.Locked = v.Bool("locked", false);
            node.IsMask = v.Bool("mask", false);
            node.ClipsContent = (type == "FRAME" || type == "SYMBOL" || type == "INSTANCE" || type == "COMPONENT_SET") && !v.Bool("frameMaskDisabled", false);
            node.SkipChildren = type == "BOOLEAN_OPERATION";
            node.StrokeAboveChildren = node.IsFrameLike;

            node.Fills = ReadPaints(v.List("fillPaints"));
            node.Strokes = ReadPaints(v.List("strokePaints"));
            node.Effects = null;
            List<KiwiValue> effects = v.List("effects");
            if (effects != null)
            {
                foreach (KiwiValue e in effects)
                {
                    FigEffect fx = FigEffect.Read(e);
                    if (fx != null)
                    {
                        (node.Effects ??= new List<FigEffect>()).Add(fx);
                        if (fx.Kind == FigEffectKind.BackgroundBlur)
                        {
                            MaxBackgroundBlur = Mathf.Max(MaxBackgroundBlur, fx.Radius);
                        }
                    }
                }
            }

            // Fill shape.
            float w = node.Width, h = node.Height;
            node.FillPaths = null;
            float radius = v.Float("cornerRadius", 0);
            node.CornerRadius = radius;
            bool independent = v.Bool("rectangleCornerRadiiIndependent", false);
            float tl = independent ? v.Float("rectangleTopLeftCornerRadius", radius) : radius;
            float tr = independent ? v.Float("rectangleTopRightCornerRadius", radius) : radius;
            float br = independent ? v.Float("rectangleBottomRightCornerRadius", radius) : radius;
            float bl = independent ? v.Float("rectangleBottomLeftCornerRadius", radius) : radius;
            FigPath box = null;
            switch (type)
            {
                case "ELLIPSE":
                    box = FigPath.Ellipse(0, 0, w, h);
                    node.FillPaths = new List<FigPath> { box };
                    break;
                case "RECTANGLE":
                case "ROUNDED_RECTANGLE":
                case "FRAME":
                case "SYMBOL":
                case "INSTANCE":
                case "SECTION":
                case "COMPONENT_SET":
                    box = FigPath.RoundedRectangle(0, 0, w, h, tl, tr, br, bl);
                    node.FillPaths = new List<FigPath> { box };
                    break;
                case "LINE":
                case "TEXT":
                case "TEXT_PATH":
                case "GROUP":
                    break;
                default:
                    node.FillPaths = GeometryPaths(v.List("fillGeometry"));
                    break;
            }

            ResolveText(node, v);

            // Strokes: the file's outline when it has one, else outlined here.
            bool text = IsText(type);
            node.StrokeWeight = Mathf.Max(0, v.Float("strokeWeight", 1f));
            node.StrokeAlign = v.EnumName("strokeAlign") ?? (type == "LINE" || type == "VECTOR" ? "CENTER" : "INSIDE");
            node.StrokePaths = null;
            node.StrokeFromOutline = false;
            if (node.Strokes != null && (node.StrokeWeight > 0 || v.Bool("borderStrokeWeightsIndependent", false)))
            {
                List<FigPath> outline = GeometryPaths(v.List("strokeGeometry"));
                if (outline != null && (!text || (node.Glyphs != null && !node.GlyphsFromLayout)))
                {
                    // Text: only while its cached glyphs are current (the outline is made from them).
                    node.StrokePaths = outline;
                }
                else if (text)
                {
                    // Outline the glyphs at draw time.
                    node.StrokeFromOutline = node.Glyphs != null;
                }
                else if (box != null)
                {
                    node.StrokePaths = new List<FigPath> { PrimitiveStroke(node, v, type == "ELLIPSE", tl, tr, br, bl) };
                }
                else if (type == "LINE")
                {
                    float sw = node.StrokeWeight;
                    node.StrokePaths = new List<FigPath> { FigPath.Rectangle(0, -sw / 2f, Mathf.Max(w, 0.01f), sw) };
                }
                else if (node.FillPaths != null)
                {
                    // Outline the fill shape at draw time (centre-aligned).
                    node.StrokeFromOutline = true;
                }
            }

            ComputePaintBounds(node);
        }

        private static List<FigPaint> ReadPaints(List<KiwiValue> paints)
        {
            if (paints == null)
            {
                return null;
            }

            List<FigPaint> list = null;
            foreach (KiwiValue p in paints)
            {
                FigPaint paint = FigPaint.Read(p, 1f);
                if (paint != null)
                {
                    (list ??= new List<FigPaint>()).Add(paint);
                }
            }

            return list;
        }

        private List<FigPath> GeometryPaths(List<KiwiValue> geometry)
        {
            if (geometry == null || blobs == null)
            {
                return null;
            }

            List<FigPath> list = null;
            foreach (KiwiValue g in geometry)
            {
                KiwiValue index = g.Get("commandsBlob");
                if (index == null || index.Uint >= (ulong)blobs.Count)
                {
                    continue;
                }

                string winding = g.Get("windingRule")?.EnumName ?? "NONZERO";
                FigPath path = FigPath.FromBlob(blobs[(int)index.Uint].Get("bytes")?.Bytes, winding == "ODD" || winding == "EVENODD");
                if (path != null && !path.IsEmpty)
                {
                    (list ??= new List<FigPath>()).Add(path);
                }
            }

            return list;
        }

        /// <summary>The stroke of a rectangle-like or ellipse layer as a ring, aligned inside / centre / outside.</summary>
        private static FigPath PrimitiveStroke(FigSceneNode node, FigView v, bool ellipse, float tl, float tr, float br, float bl)
        {
            float w = node.Width, h = node.Height, sw = node.StrokeWeight;
            if (!ellipse && v.Bool("borderStrokeWeightsIndependent", false))
            {
                // Per-side weights, drawn inside the box.
                float top = v.Float("borderTopWeight", 0), right = v.Float("borderRightWeight", 0);
                float bottom = v.Float("borderBottomWeight", 0), left = v.Float("borderLeftWeight", 0);
                FigPath outer = FigPath.RoundedRectangle(0, 0, w, h, tl, tr, br, bl);
                float iw = w - left - right, ih = h - top - bottom;
                FigPath inner = iw > 0 && ih > 0
                    ? FigPath.RoundedRectangle(left, top, iw, ih, Mathf.Max(0, tl - Mathf.Max(left, top)), Mathf.Max(0, tr - Mathf.Max(right, top)),
                        Mathf.Max(0, br - Mathf.Max(right, bottom)), Mathf.Max(0, bl - Mathf.Max(left, bottom)))
                    : null;
                return FigPath.Ring(outer, inner);
            }

            float outset, inset;
            switch (node.StrokeAlign)
            {
                case "CENTER":
                    outset = sw / 2f;
                    inset = sw / 2f;
                    break;
                case "OUTSIDE":
                    outset = sw;
                    inset = 0;
                    break;
                default:
                    outset = 0;
                    inset = sw;
                    break;
            }

            FigPath outerPath = OffsetShape(w, h, outset, ellipse, tl, tr, br, bl);
            return outerPath == null ? FigPath.Rectangle(0, 0, 0, 0) : FigPath.Ring(outerPath, OffsetShape(w, h, -inset, ellipse, tl, tr, br, bl));
        }

        /// <summary>The box grown (or shrunk, for negative <paramref name="d"/>) by d on every side, radii following.</summary>
        private static FigPath OffsetShape(float w, float h, float d, bool ellipse, float tl, float tr, float br, float bl)
        {
            float ww = w + 2 * d, hh = h + 2 * d;
            if (ww <= 0 || hh <= 0)
            {
                return null;
            }

            return ellipse
                ? FigPath.Ellipse(-d, -d, ww, hh)
                : FigPath.RoundedRectangle(-d, -d, ww, hh, Mathf.Max(0, tl + d), Mathf.Max(0, tr + d), Mathf.Max(0, br + d), Mathf.Max(0, bl + d));
        }

        // ─── Text ────────────────────────────────────────────────────────────────

        private void ResolveText(FigSceneNode node, FigView v)
        {
            node.Glyphs = null;
            node.FallbackText = false;
            node.GlyphsFromLayout = false;
            node.FontNote = null;
            if (!IsText(node.Type))
            {
                return;
            }

            KiwiValue textData = v.Get("textData");
            node.Text = textData?.Get("characters")?.String ?? "";
            node.FontSize = v.Float("fontSize", 12f);
            node.FontFamily = v.Path("fontName.family")?.String;
            node.FontStyle = v.Path("fontName.style")?.String ?? "Regular";
            node.TextAlignH = v.EnumName("textAlignHorizontal") ?? "LEFT";
            node.TextAlignV = v.EnumName("textAlignVertical") ?? "TOP";
            node.TextWraps = (v.EnumName("textAutoResize") ?? "NONE") != "WIDTH_AND_HEIGHT";
            node.TextColor = new Color(0, 0, 0, 1);
            if (node.Fills != null)
            {
                foreach (FigPaint p in node.Fills)
                {
                    if (p.Kind == FigPaintKind.Solid)
                    {
                        node.TextColor = p.Color;
                    }
                }
            }

            // Per-character styles may paint their own fills.
            var styleFills = new Dictionary<uint, List<FigPaint>>();
            List<KiwiValue> table = textData?.Get("styleOverrideTable")?.Items;
            if (table != null)
            {
                foreach (KiwiValue entry in table)
                {
                    KiwiValue id = entry.Get("styleID");
                    List<FigPaint> fills = ReadPaints(entry.Get("fillPaints")?.Items);
                    if (id != null && fills != null)
                    {
                        styleFills[(uint)id.Uint] = fills;
                    }
                }
            }

            // Cached glyph outlines are only good for the text they were laid out for; without them
            // (text edited here, imported from Figma's API, overridden in an instance) the text is
            // laid out with the installed fonts.
            KiwiValue derived = v.Get("derivedTextData");
            bool stale = derived == null || (v.HasRows && v.Overrides("textData") && !v.Overrides("derivedTextData"));
            List<KiwiValue> glyphs = stale ? null : derived.Get("glyphs")?.Items;
            if (glyphs == null || glyphs.Count == 0 || blobs == null)
            {
                if (!LayOut(node, v, textData, table, styleFills))
                {
                    node.FallbackText = !string.IsNullOrEmpty(node.Text);
                }

                return;
            }

            // Glyph outlines are in em units; work out whether they point up (font space) or down.
            float sumY = 0;
            int counted = 0;
            var outlines = new List<FigPath>(glyphs.Count);
            foreach (KiwiValue g in glyphs)
            {
                KiwiValue blob = g.Get("commandsBlob");
                FigPath path = blob != null && blob.Uint < (ulong)blobs.Count ? FigPath.FromBlob(blobs[(int)blob.Uint].Get("bytes")?.Bytes, false) : null;
                outlines.Add(path);
                if (path != null && !path.IsEmpty && counted < 200)
                {
                    sumY += path.Bounds.center.y;
                    counted++;
                }
            }

            float sign = sumY > 0 ? -1f : 1f;
            var placed = new FigPath[glyphs.Count];
            var origins = new Vector2[glyphs.Count];
            for (int i = 0; i < glyphs.Count; i++)
            {
                FigPath path = outlines[i];
                if (path == null || path.IsEmpty)
                {
                    continue;
                }

                KiwiValue g = glyphs[i];
                float size = g.Get("fontSize")?.AsFloat ?? node.FontSize;
                KiwiValue pos = g.Get("position");
                origins[i] = new Vector2(pos?.Get("x")?.AsFloat ?? 0, pos?.Get("y")?.AsFloat ?? 0);
                placed[i] = path.Transformed(new FigAffine { A = size, E = sign * size, C = origins[i].x, F = origins[i].y });
            }

            if (node.Type == "TEXT_PATH")
            {
                PlaceOnPath(node, v, placed, origins, false);
            }

            var runs = new Dictionary<uint, List<FigPath>>();
            for (int i = 0; i < glyphs.Count; i++)
            {
                if (placed[i] == null)
                {
                    continue;
                }

                uint style = (uint)(glyphs[i].Get("styleID")?.Uint ?? 0);
                if (!runs.TryGetValue(style, out List<FigPath> list))
                {
                    runs[style] = list = new List<FigPath>();
                }

                list.Add(placed[i]);
            }

            node.Glyphs = new List<FigGlyphRun>();
            foreach (KeyValuePair<uint, List<FigPath>> run in runs)
            {
                List<FigPaint> fills = styleFills.TryGetValue(run.Key, out List<FigPaint> own) ? own : node.Fills;
                if (fills == null)
                {
                    continue;
                }

                node.Glyphs.Add(new FigGlyphRun { Path = FigPath.Concat(run.Value, false), Fills = fills });
            }
        }

        public static bool IsText(string type) => type == "TEXT" || type == "TEXT_PATH";

        /// <summary>Lay the text out with the installed fonts; false when no font is installed at all.</summary>
        private bool LayOut(FigSceneNode node, FigView v, KiwiValue textData, List<KiwiValue> table, Dictionary<uint, List<FigPaint>> styleFills)
        {
            var spec = new FigTextSpec
            {
                Text = node.Text,
                AlignH = node.TextAlignH,
                AlignV = node.TextAlignV,
                AutoResize = v.EnumName("textAutoResize") ?? "NONE",
                Width = node.Width,
                Height = node.Height,
                ParagraphSpacing = v.Float("paragraphSpacing", 0),
                ParagraphIndent = v.Float("paragraphIndent", 0),
                MaxLines = (int)(v.Get("maxLines")?.AsLong ?? 0),
                Truncate = v.EnumName("textTruncation") == "ENDING",
            };

            spec.Base = new FigTextStyle().With(v.Get);
            List<KiwiValue> ids = textData?.Get("characterStyleIDs")?.Items;
            if (ids != null && ids.Count > 0)
            {
                spec.StyleIds = new uint[ids.Count];
                for (int i = 0; i < ids.Count; i++)
                {
                    spec.StyleIds[i] = (uint)ids[i].Uint;
                }
            }

            if (table != null)
            {
                foreach (KiwiValue entry in table)
                {
                    KiwiValue id = entry.Get("styleID");
                    if (id != null && id.Uint != 0)
                    {
                        spec.Overrides[(uint)id.Uint] = spec.Base.With(entry.Get);
                    }
                }
            }

            FigTextLayout.Result laid;
            try
            {
                laid = FigTextLayout.Layout(spec);
            }
            catch (Exception)
            {
                laid = null;
            }

            if (laid == null)
            {
                return false;
            }

            node.FontNote = laid.Substitutions;
            node.GlyphsFromLayout = true;
            var placed = new FigPath[laid.Glyphs.Count];
            var origins = new Vector2[laid.Glyphs.Count];
            for (int i = 0; i < placed.Length; i++)
            {
                placed[i] = laid.Glyphs[i].Path;
                origins[i] = laid.Glyphs[i].Origin;
            }

            if (node.Type == "TEXT_PATH")
            {
                PlaceOnPath(node, v, placed, origins, true);
            }

            var runs = new Dictionary<uint, List<FigPath>>();
            for (int i = 0; i < placed.Length; i++)
            {
                if (placed[i] == null)
                {
                    continue;
                }

                uint style = laid.Glyphs[i].Style;
                if (!runs.TryGetValue(style, out List<FigPath> list))
                {
                    runs[style] = list = new List<FigPath>();
                }

                list.Add(placed[i]);
            }

            node.Glyphs = new List<FigGlyphRun>();
            foreach (KeyValuePair<uint, List<FigPath>> run in runs)
            {
                List<FigPaint> fills = styleFills.TryGetValue(run.Key, out List<FigPaint> own) ? own : node.Fills;
                if (fills == null)
                {
                    continue;
                }

                laid.Embolden.TryGetValue(run.Key, out float embolden);
                node.Glyphs.Add(new FigGlyphRun { Path = FigPath.Concat(run.Value, false), Fills = fills, Embolden = embolden });
            }

            return true;
        }

        /// <summary>
        /// Text on a path: turn each glyph onto the layer's path like figcraft does (its baseline on the
        /// path where its centre falls). When the cached glyphs already follow the path (their baselines
        /// differ), only turn each one to the path's direction under it.
        /// </summary>
        private void PlaceOnPath(FigSceneNode node, FigView v, FigPath[] placed, Vector2[] origins, bool laidOutHere)
        {
            FigTextPath path = ReadTextPath(node, v);
            if (path == null)
            {
                return;
            }

            float minY = float.MaxValue, maxY = float.MinValue, width = 0;
            int first = -1;
            for (int i = 0; i < placed.Length; i++)
            {
                if (placed[i] == null)
                {
                    continue;
                }

                first = first < 0 ? i : first;
                minY = Mathf.Min(minY, origins[i].y);
                maxY = Mathf.Max(maxY, origins[i].y);
                width = Mathf.Max(width, placed[i].Bounds.xMax);
            }

            if (first < 0)
            {
                return;
            }

            bool alreadyOnPath = !laidOutHere && maxY - minY > 0.5f;
            float shift = node.TextAlignH == "CENTER" ? width / 2f : node.TextAlignH == "RIGHT" ? width : 0f;
            float origin = path.Start * path.Length - shift;
            float baseline = origins[first].y;
            for (int i = 0; i < placed.Length; i++)
            {
                if (placed[i] == null)
                {
                    continue;
                }

                FigAffine m;
                if (alreadyOnPath)
                {
                    Vector2 dir = path.DirectionNear(origins[i], out float distance);
                    m = Rotation(origins[i], origins[i], dir);
                }
                else
                {
                    float x = placed[i].Bounds.center.x, y = origins[i].y;
                    if (!path.At(origin + x, out Vector2 p, out Vector2 dir))
                    {
                        // Past an open path's end: hidden, like figcraft.
                        placed[i] = null;
                        continue;
                    }

                    Vector2 below = new Vector2(-dir.y, dir.x) * (y - baseline);
                    m = Rotation(p + below, new Vector2(x, y), dir);
                }

                placed[i] = placed[i].Transformed(m);
            }
        }

        /// <summary>Move <paramref name="from"/> to <paramref name="to"/>, turned to <paramref name="dir"/>.</summary>
        private static FigAffine Rotation(Vector2 to, Vector2 from, Vector2 dir)
        {
            float c = dir.x, s = dir.y;
            return new FigAffine
            {
                A = c,
                B = -s,
                C = to.x - (c * from.x - s * from.y),
                D = s,
                E = c,
                F = to.y - (s * from.x + c * from.y),
            };
        }

        private FigTextPath ReadTextPath(FigSceneNode node, FigView v)
        {
            KiwiValue start = v.Get("textPathStart");
            KiwiValue data = v.Get("vectorData");
            KiwiValue blobIndex = data?.Get("vectorNetworkBlob");
            if (start == null || blobIndex == null || blobs == null || blobIndex.Uint >= (ulong)blobs.Count)
            {
                return null;
            }

            float nx = data.Path("normalizedSize.x")?.AsFloat ?? node.Width;
            float ny = data.Path("normalizedSize.y")?.AsFloat ?? node.Height;
            float sx = nx > 1e-6f ? node.Width / nx : 1f, sy = ny > 1e-6f ? node.Height / ny : 1f;
            return FigTextPath.FromNetwork(blobs[(int)blobIndex.Uint].Get("bytes")?.Bytes, sx, sy,
                start.Get("tValue")?.AsFloat ?? 0f, start.Get("forward")?.AsBool ?? true);
        }

        private static void ComputePaintBounds(FigSceneNode node)
        {
            Rect local = new Rect(0, 0, node.Width, node.Height);
            if (node.FillPaths != null)
            {
                foreach (FigPath p in node.FillPaths)
                {
                    local = FigGeometry.Union(local, p.Bounds);
                }
            }

            if (node.StrokePaths != null)
            {
                foreach (FigPath p in node.StrokePaths)
                {
                    local = FigGeometry.Union(local, p.Bounds);
                }
            }
            else if (node.StrokeFromOutline || node.Type == "LINE")
            {
                float sw = node.StrokeWeight;
                local = Rect.MinMaxRect(local.xMin - sw, local.yMin - sw, local.xMax + sw, local.yMax + sw);
            }

            if (node.Glyphs != null)
            {
                foreach (FigGlyphRun run in node.Glyphs)
                {
                    Rect g = run.Path.Bounds;
                    float e = run.Embolden;
                    local = FigGeometry.Union(local, Rect.MinMaxRect(g.xMin - e, g.yMin - e, g.xMax + e, g.yMax + e));
                }
            }

            float reach = 0;
            if (node.Effects != null)
            {
                foreach (FigEffect e in node.Effects)
                {
                    reach = Mathf.Max(reach, e.Reach);
                }
            }

            local = Rect.MinMaxRect(local.xMin - reach, local.yMin - reach, local.xMax + reach, local.yMax + reach);
            node.PaintBounds = FigGeometry.TransformBounds(node.World, local);
        }

        private void ComputeSubtreeBounds()
        {
            for (int i = Nodes.Length - 1; i >= 0; i--)
            {
                FigSceneNode n = Nodes[i];
                Rect b = n.PaintBounds;
                // Children after their parent: walk the direct children ranges.
                for (int c = i + 1; c < n.SubtreeEnd; c = Nodes[c].SubtreeEnd)
                {
                    b = FigGeometry.Union(b, Nodes[c].SubtreeBounds);
                }

                if (n.ClipsContent)
                {
                    b = FigGeometry.Union(n.PaintBounds, FigGeometry.Intersect(b, n.Bounds));
                }

                n.SubtreeBounds = b;
            }
        }

        public void UpdateContentBounds()
        {
            bool first = true;
            Rect bounds = new Rect(0, 0, 100, 100);
            foreach (FigSceneNode n in Nodes)
            {
                if (n.ParentIndex >= 0)
                {
                    continue;
                }

                bounds = first ? n.SubtreeBounds : FigGeometry.Union(bounds, n.SubtreeBounds);
                first = false;
            }

            ContentBounds = bounds;
        }

        // ─── Copy-on-write edits ─────────────────────────────────────────────────

        /// <summary>
        /// A new snapshot where the layer at <paramref name="index"/> was re-read from the file
        /// (placement and paint), its descendants re-placed. Returns the world rect that changed.
        /// </summary>
        public Rect Refresh(int index, bool paint)
        {
            if (index < 0 || index >= Nodes.Length)
            {
                return default;
            }

            var nodes = (FigSceneNode[])Nodes.Clone();
            FigSceneNode old = nodes[index];
            Rect dirty = old.SubtreeBounds;
            FigSceneNode root = old.Copy();
            KiwiValue size = root.View.Get("size");
            if (!root.Expanded)
            {
                root.Width = Mathf.Max(0, size?.Get("x")?.AsFloat ?? 0);
                root.Height = Mathf.Max(0, size?.Get("y")?.AsFloat ?? 0);
                root.Local = FigAffine.FromMatrix(root.View.Get("transform"));
            }

            nodes[index] = root;
            Place(root);
            if (paint)
            {
                Resolve(root);
            }
            else
            {
                ComputePaintBounds(root);
            }

            FigAffine delta = root.World.Then(old.World.Inverse());
            for (int i = index + 1; i < root.SubtreeEnd; i++)
            {
                FigSceneNode n = nodes[i].Copy();
                n.ParentWorld = delta.Then(n.ParentWorld);
                n.World = delta.Then(n.World);
                var corners = new Vector2[4];
                for (int k = 0; k < 4; k++)
                {
                    corners[k] = delta.Apply(n.Corners[k]);
                }

                n.Corners = corners;
                n.Bounds = BoundsOf(corners);
                ComputePaintBounds(n);
                nodes[i] = n;
            }

            Nodes = nodes;
            // Subtree bounds of the edited range and its ancestors.
            for (int i = root.SubtreeEnd - 1; i >= index; i--)
            {
                RecomputeSubtreeBounds(i);
            }

            for (int p = root.ParentIndex; p >= 0; p = Nodes[p].ParentIndex)
            {
                Nodes[p] = Nodes[p].Copy();
                RecomputeSubtreeBounds(p);
            }

            Version++;
            UpdateContentBounds();
            return FigGeometry.Union(dirty, Nodes[index].SubtreeBounds);
        }

        private void RecomputeSubtreeBounds(int i)
        {
            FigSceneNode n = Nodes[i];
            Rect b = n.PaintBounds;
            for (int c = i + 1; c < n.SubtreeEnd; c = Nodes[c].SubtreeEnd)
            {
                b = FigGeometry.Union(b, Nodes[c].SubtreeBounds);
            }

            if (n.ClipsContent)
            {
                b = FigGeometry.Union(n.PaintBounds, FigGeometry.Intersect(b, n.Bounds));
            }

            n.SubtreeBounds = b;
        }

        // ─── Queries ─────────────────────────────────────────────────────────────

        /// <summary>The top-most layer under <paramref name="p"/> (locked ones skipped), or -1.</summary>
        public int HitTest(Vector2 p, bool includeLocked = false)
        {
            FigSceneNode[] nodes = Nodes;
            for (int i = nodes.Length - 1; i >= 0; i--)
            {
                FigSceneNode n = nodes[i];
                if ((!includeLocked && n.Locked) || !n.Bounds.Contains(p))
                {
                    continue;
                }

                bool clipped = false;
                for (int a = n.ParentIndex; a >= 0; a = nodes[a].ParentIndex)
                {
                    if ((nodes[a].ClipsContent && !FigGeometry.Contains(nodes[a].Corners, p)) || nodes[a].SkipChildren)
                    {
                        clipped = true;
                        break;
                    }
                }

                if (!clipped && FigGeometry.Contains(n.Corners, p))
                {
                    return i;
                }
            }

            return -1;
        }

        public List<int> Chain(int index)
        {
            var chain = new List<int>();
            for (int i = index; i >= 0; i = Nodes[i].ParentIndex)
            {
                chain.Add(i);
            }

            chain.Reverse();
            return chain;
        }

        public bool IsAncestor(int ancestor, int index)
        {
            return ancestor >= 0 && index >= ancestor && index < Nodes[ancestor].SubtreeEnd;
        }
    }
}
