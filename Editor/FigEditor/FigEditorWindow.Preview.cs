using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// The Preview tab: a wireframe of a frame (or page) and its descendants, from each layer's
    /// <c>transform</c> and <c>size</c>, filled with its first visible solid fill or image. It is a
    /// layout check, not a renderer: vectors draw as their bounds, effects and strokes are skipped.
    /// Click selects a layer, double-click makes it the preview root, wheel zooms, drag pans.
    /// </summary>
    public partial class FigEditorWindow
    {
        private const int MaxPreviewItems = 20000;
        private const int EllipseSegments = 32;

        private static readonly Color OutlineColor = new Color(0.5f, 0.5f, 0.5f, 0.55f);
        private static readonly Color HighlightColor = new Color(0.2f, 0.6f, 1f, 1f);
        private static readonly Color CanvasColor = new Color(0.16f, 0.16f, 0.16f, 1f);

        [SerializeField] private string previewRootId;
        [SerializeField] private bool previewLabels = true;

        private bool previewDirty = true;
        private float previewZoom = 1f;
        private Vector2 previewPan;
        private bool previewTruncated;
        private Rect previewBounds;
        private readonly List<PreviewItem> previewItems = new();

        private struct Affine
        {
            public float A, B, C, D, E, F; // x' = A x + B y + C ; y' = D x + E y + F

            public static readonly Affine Identity = new Affine { A = 1, E = 1 };

            public Vector2 Apply(float x, float y) => new Vector2(A * x + B * y + C, D * x + E * y + F);

            public Affine Then(Affine local)
            {
                return new Affine
                {
                    A = A * local.A + B * local.D,
                    B = A * local.B + B * local.E,
                    C = A * local.C + B * local.F + C,
                    D = D * local.A + E * local.D,
                    E = D * local.B + E * local.E,
                    F = D * local.C + E * local.F + F,
                };
            }

            public bool AxisAligned => Mathf.Abs(B) < 1e-4f && Mathf.Abs(D) < 1e-4f && A > 0 && E > 0;
        }

        private struct PreviewItem
        {
            public string Id;
            public Vector2[] Points;
            public Color Fill;
            public bool HasFill;
            public string ImageHash;
            public string Text;
            public bool AxisAligned;
        }

        private void DrawPreviewTab(Rect rect)
        {
            FigNode root = file.Find(previewRootId);
            if (root == null)
            {
                root = Selected ?? FirstPage();
                previewRootId = root?.Id;
                previewDirty = true;
            }

            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label(root != null ? "Root: " + root.Label + "  (" + ShortType(root.Type) + ")" : "No layer", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(Selected == null || Selected == root))
            {
                if (GUILayout.Button("Use Selection", EditorStyles.toolbarButton))
                {
                    SetPreviewRoot(selectedId);
                }
            }

            using (new EditorGUI.DisabledScope(root?.Parent == null))
            {
                if (GUILayout.Button("Up", EditorStyles.toolbarButton))
                {
                    SetPreviewRoot(root.Parent.Id);
                }
            }

            if (GUILayout.Button("Fit", EditorStyles.toolbarButton))
            {
                previewZoom = 1f;
                previewPan = Vector2.zero;
            }

            previewLabels = GUILayout.Toggle(previewLabels, "Labels", EditorStyles.toolbarButton);
            GUILayout.EndHorizontal();

            if (root == null)
            {
                return;
            }

            if (previewDirty)
            {
                BuildPreview(root);
                previewDirty = false;
            }

            var area = new Rect(rect.x, rect.y + ToolbarHeight, rect.width, rect.height - ToolbarHeight);
            if (previewTruncated)
            {
                EditorGUI.HelpBox(new Rect(area.x + 4, area.yMax - 24, area.width - 8, 20), $"Showing the first {MaxPreviewItems} layers.", MessageType.None);
            }

            DrawPreviewCanvas(area);
        }

        private FigNode FirstPage()
        {
            foreach (FigNode root in file.Roots)
            {
                foreach (FigNode child in root.Children)
                {
                    if (child.Type == "CANVAS")
                    {
                        return child;
                    }
                }
            }

            return file.Roots.Count > 0 ? file.Roots[0] : null;
        }

        private void SetPreviewRoot(string id)
        {
            previewRootId = id;
            previewDirty = true;
            previewZoom = 1f;
            previewPan = Vector2.zero;
        }

        private void BuildPreview(FigNode root)
        {
            previewItems.Clear();
            previewTruncated = false;
            var stack = new Stack<(FigNode node, Affine world)>();

            // The root sits at the origin; its own transform places it in its parent, not here.
            stack.Push((root, Affine.Identity));
            bool isRoot = true;
            while (stack.Count > 0)
            {
                (FigNode node, Affine world) = stack.Pop();
                if (!isRoot && node.Change.Get("visible") is KiwiValue visible && !visible.AsBool)
                {
                    continue;
                }

                if (!isRoot)
                {
                    world = world.Then(LocalTransform(node));
                }

                isRoot = false;
                AddPreviewItem(node, world);
                if (previewItems.Count >= MaxPreviewItems)
                {
                    previewTruncated = true;
                    break;
                }

                // Children are pushed last-first so the first child draws first (bottom-most).
                for (int i = node.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push((node.Children[i], world));
                }
            }

            if (previewItems.Count == 0)
            {
                previewBounds = new Rect(0, 0, 100, 100);
                return;
            }

            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            foreach (PreviewItem item in previewItems)
            {
                foreach (Vector2 p in item.Points)
                {
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                }
            }

            previewBounds = Rect.MinMaxRect(min.x, min.y, Mathf.Max(max.x, min.x + 1), Mathf.Max(max.y, min.y + 1));
        }

        private static Affine LocalTransform(FigNode node)
        {
            KiwiValue t = node.Change.Get("transform");
            if (t == null)
            {
                return Affine.Identity;
            }

            return new Affine
            {
                A = t.Get("m00")?.AsFloat ?? 1,
                B = t.Get("m01")?.AsFloat ?? 0,
                C = t.Get("m02")?.AsFloat ?? 0,
                D = t.Get("m10")?.AsFloat ?? 0,
                E = t.Get("m11")?.AsFloat ?? 1,
                F = t.Get("m12")?.AsFloat ?? 0,
            };
        }

        private void AddPreviewItem(FigNode node, Affine world)
        {
            KiwiValue size = node.Change.Get("size");
            if (size == null)
            {
                return;
            }

            float w = size.Get("x")?.AsFloat ?? 0;
            float h = size.Get("y")?.AsFloat ?? 0;
            if (w <= 0 && h <= 0)
            {
                return;
            }

            var item = new PreviewItem { Id = node.Id, AxisAligned = world.AxisAligned };
            if (node.Type == "ELLIPSE")
            {
                item.Points = new Vector2[EllipseSegments];
                for (int i = 0; i < EllipseSegments; i++)
                {
                    float a = i * Mathf.PI * 2f / EllipseSegments;
                    item.Points[i] = world.Apply(w * 0.5f * (1 + Mathf.Cos(a)), h * 0.5f * (1 + Mathf.Sin(a)));
                }
            }
            else
            {
                item.Points = new[] { world.Apply(0, 0), world.Apply(w, 0), world.Apply(w, h), world.Apply(0, h) };
            }

            float layerOpacity = node.Change.Get("opacity")?.AsFloat ?? 1f;
            List<KiwiValue> fills = node.Change.Get("fillPaints")?.Items;
            if (fills != null)
            {
                // The top-most visible paint wins (paints are listed bottom to top).
                for (int i = fills.Count - 1; i >= 0; i--)
                {
                    KiwiValue paint = fills[i];
                    if (paint.Get("visible") is KiwiValue v && !v.AsBool)
                    {
                        continue;
                    }

                    string type = paint.Get("type")?.EnumName;
                    float opacity = (paint.Get("opacity")?.AsFloat ?? 1f) * layerOpacity;
                    if (type == "IMAGE" && paint.Path("image.hash")?.Bytes is byte[] hash && hash.Length > 0)
                    {
                        item.ImageHash = FigUtil.Hex(hash);
                        item.Fill = new Color(0.6f, 0.6f, 0.6f, 0.5f * opacity);
                        item.HasFill = true;
                        break;
                    }

                    // Gradients show their first stop.
                    KiwiValue color = paint.Get("color");
                    if (type != "SOLID" && paint.Get("stops")?.Items is List<KiwiValue> stops && stops.Count > 0)
                    {
                        color = stops[0].Get("color") ?? color;
                    }

                    if (color != null)
                    {
                        Color c = ToColor(color);
                        c.a *= opacity;
                        item.Fill = c;
                        item.HasFill = c.a > 0.001f;
                        break;
                    }
                }
            }

            if (node.Type == "TEXT")
            {
                item.Text = node.Change.Path("textData.characters")?.String;
            }

            previewItems.Add(item);
        }

        private void DrawPreviewCanvas(Rect area)
        {
            Event e = Event.current;
            EditorGUI.DrawRect(area, CanvasColor);

            float fit = Mathf.Min((area.width - 32) / previewBounds.width, (area.height - 32) / previewBounds.height);
            float scale = Mathf.Max(1e-4f, fit * previewZoom);
            Vector2 center = area.center + previewPan;
            Vector2 boundsCenter = previewBounds.center;

            System.Func<Vector2, Vector2> toScreen = p => center + (p - boundsCenter) * scale;
            System.Func<Vector2, Vector2> toDesign = s => boundsCenter + (s - center) / scale;

            HandlePreviewInput(area, e, toDesign);

            if (e.type != EventType.Repaint)
            {
                return;
            }

            GUI.BeginClip(area);
            Vector2 offset = area.position;
            var points = new Vector3[EllipseSegments];
            var outline = new Vector3[EllipseSegments + 1];
            Color oldColor = Handles.color;
            var visible = new Rect(0, 0, area.width, area.height);

            foreach (PreviewItem item in previewItems)
            {
                int n = item.Points.Length;
                Vector2 smin = new Vector2(float.MaxValue, float.MaxValue);
                Vector2 smax = new Vector2(float.MinValue, float.MinValue);
                for (int i = 0; i < n; i++)
                {
                    Vector2 s = toScreen(item.Points[i]) - offset;
                    points[i] = s;
                    outline[i] = s;
                    smin = Vector2.Min(smin, s);
                    smax = Vector2.Max(smax, s);
                }

                var screenRect = Rect.MinMaxRect(smin.x, smin.y, smax.x, smax.y);
                if (!screenRect.Overlaps(visible) || (screenRect.width < 0.5f && screenRect.height < 0.5f))
                {
                    continue;
                }

                outline[n] = outline[0];
                Vector3[] fillPoints = n == points.Length ? points : Slice(points, n);
                Texture2D tex = item.ImageHash != null && item.AxisAligned ? ImageTexture(item.ImageHash) : null;
                if (tex != null)
                {
                    GUI.DrawTexture(screenRect, tex, ScaleMode.ScaleAndCrop, true, 0, new Color(1, 1, 1, Mathf.Clamp01(item.Fill.a * 2f)), 0, 0);
                }
                else if (item.HasFill)
                {
                    Handles.color = item.Fill;
                    Handles.DrawAAConvexPolygon(fillPoints);
                }

                bool selected = item.Id == selectedId;
                Handles.color = selected ? HighlightColor : OutlineColor;
                Handles.DrawAAPolyLine(selected ? 3f : 1f, n + 1, outline);

                if (previewLabels && item.Text != null && item.AxisAligned && screenRect.height >= 9f && screenRect.width >= 12f)
                {
                    float fontScale = Mathf.Clamp(screenRect.height, 9f, 14f);
                    var style = PreviewTextStyle;
                    style.fontSize = (int)fontScale;
                    GUI.Label(screenRect, item.Text, style);
                }
            }

            Handles.color = oldColor;
            GUI.EndClip();
        }

        private static Vector3[] Slice(Vector3[] source, int count)
        {
            var result = new Vector3[count];
            System.Array.Copy(source, result, count);
            return result;
        }

        private GUIStyle previewTextStyle;

        private GUIStyle PreviewTextStyle => previewTextStyle ??= new GUIStyle(EditorStyles.label)
        {
            wordWrap = true,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.UpperLeft,
            normal = { textColor = new Color(1f, 1f, 1f, 0.85f) },
        };

        private void HandlePreviewInput(Rect area, Event e, System.Func<Vector2, Vector2> toDesign)
        {
            if (!area.Contains(e.mousePosition) && e.type != EventType.MouseDrag)
            {
                return;
            }

            switch (e.type)
            {
                case EventType.ScrollWheel:
                {
                    float factor = e.delta.y > 0 ? 1f / 1.15f : 1.15f;
                    float newZoom = Mathf.Clamp(previewZoom * factor, 0.05f, 200f);
                    // Zoom around the cursor: keep the point under it fixed.
                    Vector2 fromCenter = e.mousePosition - (area.center + previewPan);
                    previewPan -= fromCenter * (newZoom / previewZoom - 1f);
                    previewZoom = newZoom;
                    e.Use();
                    Repaint();
                    break;
                }
                case EventType.MouseDrag:
                    // Pan with the middle or right button, or Alt + left.
                    if (e.button == 0 && !e.alt)
                    {
                        break;
                    }

                    previewPan += e.delta;
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseDown:
                {
                    if (e.button != 0 || e.alt)
                    {
                        break;
                    }

                    Vector2 p = toDesign(e.mousePosition);
                    string hit = HitTest(p);
                    if (hit != null)
                    {
                        if (e.clickCount == 2)
                        {
                            SetPreviewRoot(hit);
                        }

                        Select(hit);
                    }

                    e.Use();
                    Repaint();
                    break;
                }
            }
        }

        /// <summary>The top-most layer whose outline contains design point <paramref name="p"/>.</summary>
        private string HitTest(Vector2 p)
        {
            for (int i = previewItems.Count - 1; i >= 0; i--)
            {
                if (Contains(previewItems[i].Points, p))
                {
                    return previewItems[i].Id;
                }
            }

            return null;
        }

        private static bool Contains(Vector2[] polygon, Vector2 p)
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
    }
}
