using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// The Fig Editor's theme (figcraft / Figma colours: dark with Unity's dark skin, light with the
    /// light skin), its text styles and small widgets, and a set of icons drawn as vector shapes through
    /// <see cref="FigRaster"/> into textures at startup — no built-in icon names to break.
    /// </summary>
    public static class FigTheme
    {
        /// <summary>Dark (figcraft) with Unity's dark skin, light (Figma's light UI) with the light skin, so Unity's own fields stay readable.</summary>
        public static bool Dark => EditorGUIUtility.isProSkin;

        public static Color Rail => Dark ? Hex(0x1E1E1E) : Hex(0xF0F0F0);
        public static Color Panel => Dark ? Hex(0x2C2C2C) : Hex(0xFFFFFF);
        public static Color PanelDark => Dark ? Hex(0x252525) : Hex(0xF5F5F5);
        public static Color Border => Dark ? Hex(0x3A3A3A) : Hex(0xE6E6E6);
        public static Color Hover => Dark ? Hex(0x383838) : Hex(0xF0F0F0);
        public static Color Field => Dark ? Hex(0x383838) : Hex(0xF5F5F5);
        public static Color FieldHover => Dark ? Hex(0x444444) : Hex(0xEBEBEB);
        public static readonly Color Selected = new Color(0.051f, 0.6f, 1f, 0.22f);
        public static readonly Color SelectedComponent = new Color(0.592f, 0.278f, 1f, 0.25f);
        public static readonly Color Accent = Hex(0x0D99FF);
        public static readonly Color Component = Hex(0x9747FF);
        public static Color Text => Dark ? Hex(0xE6E6E6) : Hex(0x1E1E1E);
        public static Color TextDim => Dark ? Hex(0x9A9A9A) : Hex(0x737373);
        public static Color TextFaint => Dark ? Hex(0x6B6B6B) : Hex(0xA0A0A0);
        public static readonly Color Danger = Hex(0xF24822);

        public static Color Hex(int rgb, float a = 1f) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);

        private static GUIStyle label, dim, faint, bold, header, small, centered, title, mono, rowLabel, tabStyle, tabActive, buttonText, badge;
        private static bool stylesDark;

        /// <summary>Styles are rebuilt when the skin changes.</summary>
        private static void CheckSkin()
        {
            if (stylesDark == Dark)
            {
                return;
            }

            stylesDark = Dark;
            label = dim = faint = bold = header = small = centered = title = mono = rowLabel = tabStyle = tabActive = buttonText = badge = null;
        }

        public static GUIStyle Label { get { CheckSkin(); return label ??= Make(EditorStyles.label, Text, 11); } }
        public static GUIStyle Dim { get { CheckSkin(); return dim ??= Make(EditorStyles.label, TextDim, 11); } }
        public static GUIStyle Faint { get { CheckSkin(); return faint ??= Make(EditorStyles.label, TextFaint, 10); } }
        public static GUIStyle Bold { get { CheckSkin(); return bold ??= Make(EditorStyles.boldLabel, Text, 11); } }
        public static GUIStyle Header { get { CheckSkin(); return header ??= Make(EditorStyles.boldLabel, Text, 11); } }
        public static GUIStyle Small { get { CheckSkin(); return small ??= Make(EditorStyles.miniLabel, TextDim, 10); } }
        public static GUIStyle Centered { get { CheckSkin(); return centered ??= Make(EditorStyles.label, TextDim, 11, TextAnchor.MiddleCenter); } }
        public static GUIStyle Title { get { CheckSkin(); return title ??= Make(EditorStyles.boldLabel, Text, 12); } }
        public static GUIStyle Mono { get { CheckSkin(); return mono ??= Make(EditorStyles.label, Text, 11); } }
        public static GUIStyle RowLabel { get { CheckSkin(); return rowLabel ??= Make(EditorStyles.label, Text, 11, TextAnchor.MiddleLeft, TextClipping.Clip); } }
        public static GUIStyle Tab { get { CheckSkin(); return tabStyle ??= Make(EditorStyles.label, TextDim, 11, TextAnchor.MiddleCenter); } }
        public static GUIStyle TabActive { get { CheckSkin(); return tabActive ??= Make(EditorStyles.boldLabel, Text, 11, TextAnchor.MiddleCenter); } }
        public static GUIStyle ButtonText { get { CheckSkin(); return buttonText ??= Make(EditorStyles.boldLabel, Color.white, 11, TextAnchor.MiddleCenter); } }
        public static GUIStyle Badge { get { CheckSkin(); return badge ??= Make(EditorStyles.miniLabel, Color.white, 10, TextAnchor.MiddleCenter); } }

        private static GUIStyle Make(GUIStyle from, Color color, int size, TextAnchor anchor = TextAnchor.MiddleLeft, TextClipping clip = TextClipping.Clip)
        {
            var s = new GUIStyle(from) { fontSize = size, alignment = anchor, clipping = clip, richText = false, wordWrap = false };
            s.normal.textColor = color;
            s.hover.textColor = color;
            s.active.textColor = color;
            s.focused.textColor = color;
            s.padding = new RectOffset(0, 0, 0, 0);
            s.margin = new RectOffset(0, 0, 0, 0);
            return s;
        }

        // ─── Widgets ─────────────────────────────────────────────────────────────

        /// <summary>A flat icon button: hover highlight, an active (pressed) state; returns true on click.</summary>
        public static bool IconButton(Rect r, string icon, string tooltip, bool active = false, Color? tint = null)
        {
            Event e = Event.current;
            bool hover = r.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                if (active)
                {
                    EditorGUI.DrawRect(r, Accent);
                }
                else if (hover)
                {
                    EditorGUI.DrawRect(r, Hover);
                }

                DrawIcon(r, icon, active ? Color.white : tint ?? Text);
            }

            if (!string.IsNullOrEmpty(tooltip))
            {
                GUI.Label(r, new GUIContent("", tooltip));
            }

            if (e.type == EventType.MouseDown && e.button == 0 && hover)
            {
                e.Use();
                return true;
            }

            return false;
        }

        /// <summary>A filled accent button with white text.</summary>
        public static bool AccentButton(Rect r, string text)
        {
            Event e = Event.current;
            bool hover = r.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(r, hover ? Hex(0x2AA5FF) : Accent);
                GUI.Label(r, text, ButtonText);
            }

            if (e.type == EventType.MouseDown && e.button == 0 && hover)
            {
                e.Use();
                return true;
            }

            return false;
        }

        /// <summary>A text button with a hover background; returns true on click.</summary>
        public static bool FlatButton(Rect r, string text, GUIStyle style = null, bool active = false)
        {
            Event e = Event.current;
            bool hover = r.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                if (active || hover)
                {
                    EditorGUI.DrawRect(r, active ? Field : Hover);
                }

                GUI.Label(r, text, style ?? Centered);
            }

            if (e.type == EventType.MouseDown && e.button == 0 && hover)
            {
                e.Use();
                return true;
            }

            return false;
        }

        /// <summary>A one-pixel frame around <paramref name="r"/>.</summary>
        public static void Outline(Rect r, Color c)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1), c);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), c);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 1, r.height), c);
            EditorGUI.DrawRect(new Rect(r.xMax - 1, r.y, 1, r.height), c);
        }

        public static void Separator(Rect r)
        {
            EditorGUI.DrawRect(r, Border);
        }

        // ─── Icons ───────────────────────────────────────────────────────────────

        private const int IconPixels = 32;
        private static readonly Dictionary<string, Texture2D> icons = new();

        public static void DrawIcon(Rect r, string name, Color tint, float size = 16f)
        {
            Texture2D tex = Icon(name);
            if (tex == null)
            {
                return;
            }

            var rect = new Rect(Mathf.Round(r.center.x - size / 2f), Mathf.Round(r.center.y - size / 2f), size, size);
            Color old = GUI.color;
            GUI.color = tint;
            GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, true);
            GUI.color = old;
        }

        public static Texture2D Icon(string name)
        {
            if (icons.TryGetValue(name, out Texture2D tex) && tex != null)
            {
                return tex;
            }

            var shape = new IconShape();
            if (!DefineIcon(name, shape))
            {
                return null;
            }

            tex = shape.Render(IconPixels);
            icons[name] = tex;
            return tex;
        }

        /// <summary>Drop the icon textures (domain reload / window closed).</summary>
        public static void ReleaseIcons()
        {
            foreach (Texture2D t in icons.Values)
            {
                if (t != null)
                {
                    Object.DestroyImmediate(t);
                }
            }

            icons.Clear();
        }

        /// <summary>A small vector drawing in a 16 × 16 box: stroked segments (1.5 wide) and filled polygons.</summary>
        private sealed class IconShape
        {
            public readonly List<Vector2[]> Fills = new();
            public readonly List<Vector2[]> Holes = new();
            public readonly List<Vector2[]> Strokes = new();
            public float Width = 1.5f;

            public void Line(float x0, float y0, float x1, float y1) => Strokes.Add(new[] { new Vector2(x0, y0), new Vector2(x1, y1) });

            public void Poly(bool closed, params float[] xy)
            {
                int n = xy.Length / 2;
                for (int i = 0; i < (closed ? n : n - 1); i++)
                {
                    int j = (i + 1) % n;
                    Line(xy[i * 2], xy[i * 2 + 1], xy[j * 2], xy[j * 2 + 1]);
                }
            }

            public void Fill(params float[] xy)
            {
                var pts = new Vector2[xy.Length / 2];
                for (int i = 0; i < pts.Length; i++)
                {
                    pts[i] = new Vector2(xy[i * 2], xy[i * 2 + 1]);
                }

                Fills.Add(pts);
            }

            public void Circle(float cx, float cy, float r, bool fill)
            {
                const int n = 28;
                var pts = new float[n * 2];
                for (int i = 0; i < n; i++)
                {
                    float a = i * Mathf.PI * 2f / n;
                    pts[i * 2] = cx + Mathf.Cos(a) * r;
                    pts[i * 2 + 1] = cy + Mathf.Sin(a) * r;
                }

                if (fill)
                {
                    Fill(pts);
                }
                else
                {
                    Poly(true, pts);
                }
            }

            public void Rect(float x, float y, float w, float h) => Poly(true, x, y, x + w, y, x + w, y + h, x, y + h);

            public Texture2D Render(int px)
            {
                float k = px / 16f;
                var mask = new FigMask(px, px);
                var toPx = new List<Vector2[]>();
                foreach (Vector2[] f in Fills)
                {
                    toPx.Add(Scale(f, k));
                }

                if (toPx.Count > 0)
                {
                    FigRaster.Fill(toPx, true, mask);
                }

                toPx.Clear();
                foreach (Vector2[] s in Strokes)
                {
                    toPx.Add(Scale(s, k));
                }

                if (toPx.Count > 0)
                {
                    FigRaster.Fill(FigRaster.Stroke(toPx, Width * k), false, mask);
                }

                var pixels = new Color32[px * px];
                for (int y = 0; y < px; y++)
                {
                    for (int x = 0; x < px; x++)
                    {
                        pixels[(px - 1 - y) * px + x] = new Color32(255, 255, 255, mask.A[y * px + x]);
                    }
                }

                var tex = new Texture2D(px, px, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    name = "Fig Editor icon",
                };
                tex.SetPixels32(pixels);
                tex.Apply(false, true);
                return tex;
            }

            private static Vector2[] Scale(Vector2[] pts, float k)
            {
                var r = new Vector2[pts.Length];
                for (int i = 0; i < pts.Length; i++)
                {
                    r[i] = pts[i] * k;
                }

                return r;
            }
        }

        private static bool DefineIcon(string name, IconShape s)
        {
            switch (name)
            {
                case "move":
                    s.Fill(4, 2, 4, 14, 7, 11, 9.5f, 15, 11.5f, 14, 9, 10, 13, 10);
                    break;
                case "hand":
                    s.Poly(false, 5, 9, 5, 4);
                    s.Poly(false, 7.5f, 8, 7.5f, 2.5f);
                    s.Poly(false, 10, 8, 10, 3);
                    s.Poly(false, 12.5f, 9, 12.5f, 5);
                    s.Poly(false, 5, 9, 3.5f, 8, 5.5f, 13, 8, 14.5f, 11, 14.5f, 12.5f, 12, 12.5f, 9);
                    break;
                case "frame":
                    s.Line(5.5f, 2, 5.5f, 14);
                    s.Line(10.5f, 2, 10.5f, 14);
                    s.Line(2, 5.5f, 14, 5.5f);
                    s.Line(2, 10.5f, 14, 10.5f);
                    break;
                case "rect":
                    s.Rect(3, 3, 10, 10);
                    break;
                case "ellipse":
                    s.Circle(8, 8, 5.5f, false);
                    break;
                case "line":
                    s.Line(3, 13, 13, 3);
                    break;
                case "text":
                    s.Line(3.5f, 3.5f, 12.5f, 3.5f);
                    s.Line(8, 3.5f, 8, 13);
                    break;
                case "vector":
                    s.Poly(false, 3, 12, 5, 6, 11, 10, 13, 4);
                    s.Fill(2, 11, 4, 11, 4, 13, 2, 13);
                    s.Fill(12, 3, 14, 3, 14, 5, 12, 5);
                    break;
                case "component":
                    s.Fill(8, 1.5f, 10.5f, 4, 8, 6.5f, 5.5f, 4);
                    s.Fill(8, 9.5f, 10.5f, 12, 8, 14.5f, 5.5f, 12);
                    s.Fill(4, 5.5f, 6.5f, 8, 4, 10.5f, 1.5f, 8);
                    s.Fill(12, 5.5f, 14.5f, 8, 12, 10.5f, 9.5f, 8);
                    break;
                case "instance":
                    s.Poly(true, 8, 2, 14, 8, 8, 14, 2, 8);
                    break;
                case "group":
                    s.Poly(false, 3, 6, 3, 3, 6, 3);
                    s.Poly(false, 10, 3, 13, 3, 13, 6);
                    s.Poly(false, 13, 10, 13, 13, 10, 13);
                    s.Poly(false, 6, 13, 3, 13, 3, 10);
                    break;
                case "section":
                    s.Rect(2.5f, 4.5f, 11, 9);
                    s.Fill(2.5f, 2, 8, 2, 8, 4.5f, 2.5f, 4.5f);
                    break;
                case "image":
                    s.Rect(2.5f, 3.5f, 11, 9);
                    s.Fill(3.5f, 11.5f, 7, 7, 9.5f, 10, 11, 8.5f, 12.5f, 11.5f);
                    s.Circle(10.5f, 6, 1.2f, true);
                    break;
                case "boolean":
                    s.Rect(2.5f, 2.5f, 7.5f, 7.5f);
                    s.Rect(6, 6, 7.5f, 7.5f);
                    break;
                case "star":
                {
                    var pts = new float[20];
                    for (int i = 0; i < 10; i++)
                    {
                        float r = i % 2 == 0 ? 6f : 2.6f;
                        float a = -Mathf.PI / 2 + i * Mathf.PI / 5;
                        pts[i * 2] = 8 + r * Mathf.Cos(a);
                        pts[i * 2 + 1] = 8.5f + r * Mathf.Sin(a);
                    }

                    s.Poly(true, pts);
                    break;
                }
                case "stack_v":
                    s.Fill(3, 3, 13, 3, 13, 5.5f, 3, 5.5f);
                    s.Fill(3, 6.75f, 13, 6.75f, 13, 9.25f, 3, 9.25f);
                    s.Fill(3, 10.5f, 13, 10.5f, 13, 13, 3, 13);
                    break;
                case "stack_h":
                    s.Fill(3, 3, 5.5f, 3, 5.5f, 13, 3, 13);
                    s.Fill(6.75f, 3, 9.25f, 3, 9.25f, 13, 6.75f, 13);
                    s.Fill(10.5f, 3, 13, 3, 13, 13, 10.5f, 13);
                    break;
                case "page":
                    s.Poly(true, 4, 2, 10, 2, 12.5f, 4.5f, 12.5f, 14, 4, 14);
                    break;
                case "chevron_right":
                    s.Poly(false, 6.5f, 4.5f, 10, 8, 6.5f, 11.5f);
                    break;
                case "chevron_down":
                    s.Poly(false, 4.5f, 6.5f, 8, 10, 11.5f, 6.5f);
                    break;
                case "eye":
                    s.Poly(true, 1.5f, 8, 4, 5, 8, 3.8f, 12, 5, 14.5f, 8, 12, 11, 8, 12.2f, 4, 11);
                    s.Circle(8, 8, 2, true);
                    break;
                case "eye_off":
                    s.Poly(false, 1.5f, 8, 4, 10.5f, 8, 11.7f, 12, 10.5f, 14.5f, 8);
                    s.Line(3, 13, 13, 3);
                    break;
                case "lock":
                    s.Fill(3.5f, 7.5f, 12.5f, 7.5f, 12.5f, 14, 3.5f, 14);
                    s.Poly(false, 5.5f, 7.5f, 5.5f, 5, 6.5f, 3.2f, 8, 2.6f, 9.5f, 3.2f, 10.5f, 5, 10.5f, 7.5f);
                    break;
                case "unlock":
                    s.Fill(3.5f, 7.5f, 12.5f, 7.5f, 12.5f, 14, 3.5f, 14);
                    s.Poly(false, 5.5f, 7.5f, 5.5f, 5, 6.5f, 3.2f, 8, 2.6f, 9.5f, 3.2f, 10.5f, 4.5f);
                    break;
                case "plus":
                    s.Line(8, 3, 8, 13);
                    s.Line(3, 8, 13, 8);
                    break;
                case "minus":
                    s.Line(3, 8, 13, 8);
                    break;
                case "search":
                    s.Circle(7, 7, 4, false);
                    s.Line(10, 10, 13.5f, 13.5f);
                    break;
                case "layers":
                    s.Poly(true, 2, 6, 8, 3, 14, 6, 8, 9);
                    s.Poly(false, 2, 10, 8, 13, 14, 10);
                    break;
                case "assets":
                    s.Fill(8, 1.5f, 10.5f, 4, 8, 6.5f, 5.5f, 4);
                    s.Fill(8, 9.5f, 10.5f, 12, 8, 14.5f, 5.5f, 12);
                    s.Fill(4, 5.5f, 6.5f, 8, 4, 10.5f, 1.5f, 8);
                    s.Fill(12, 5.5f, 14.5f, 8, 12, 10.5f, 9.5f, 8);
                    break;
                case "file":
                    s.Poly(true, 4, 2, 10, 2, 12.5f, 4.5f, 12.5f, 14, 4, 14);
                    s.Line(6, 8, 10.5f, 8);
                    s.Line(6, 11, 10.5f, 11);
                    break;
                case "images":
                    s.Rect(2.5f, 3.5f, 11, 9);
                    s.Fill(3.5f, 11.5f, 7, 7, 9.5f, 10, 11, 8.5f, 12.5f, 11.5f);
                    break;
                case "undo":
                    s.Poly(false, 5, 3, 2.5f, 5.5f, 5, 8);
                    s.Poly(false, 2.5f, 5.5f, 10, 5.5f, 12.5f, 7, 13.5f, 9.5f, 12.5f, 12, 10, 13, 6, 13);
                    break;
                case "redo":
                    s.Poly(false, 11, 3, 13.5f, 5.5f, 11, 8);
                    s.Poly(false, 13.5f, 5.5f, 6, 5.5f, 3.5f, 7, 2.5f, 9.5f, 3.5f, 12, 6, 13, 10, 13);
                    break;
                case "menu":
                    s.Line(3, 4.5f, 13, 4.5f);
                    s.Line(3, 8, 13, 8);
                    s.Line(3, 11.5f, 13, 11.5f);
                    break;
                case "play":
                    s.Fill(5, 3, 13, 8, 5, 13);
                    break;
                case "align_left":
                    s.Line(2.5f, 2, 2.5f, 14);
                    s.Fill(4.5f, 4, 12, 4, 12, 7, 4.5f, 7);
                    s.Fill(4.5f, 9, 9, 9, 9, 12, 4.5f, 12);
                    break;
                case "align_hcenter":
                    s.Line(8, 2, 8, 14);
                    s.Fill(3.5f, 4, 12.5f, 4, 12.5f, 7, 3.5f, 7);
                    s.Fill(5.5f, 9, 10.5f, 9, 10.5f, 12, 5.5f, 12);
                    break;
                case "align_right":
                    s.Line(13.5f, 2, 13.5f, 14);
                    s.Fill(4, 4, 11.5f, 4, 11.5f, 7, 4, 7);
                    s.Fill(7, 9, 11.5f, 9, 11.5f, 12, 7, 12);
                    break;
                case "align_top":
                    s.Line(2, 2.5f, 14, 2.5f);
                    s.Fill(4, 4.5f, 7, 4.5f, 7, 12, 4, 12);
                    s.Fill(9, 4.5f, 12, 4.5f, 12, 9, 9, 9);
                    break;
                case "align_vcenter":
                    s.Line(2, 8, 14, 8);
                    s.Fill(4, 3.5f, 7, 3.5f, 7, 12.5f, 4, 12.5f);
                    s.Fill(9, 5.5f, 12, 5.5f, 12, 10.5f, 9, 10.5f);
                    break;
                case "align_bottom":
                    s.Line(2, 13.5f, 14, 13.5f);
                    s.Fill(4, 4, 7, 4, 7, 11.5f, 4, 11.5f);
                    s.Fill(9, 7, 12, 7, 12, 11.5f, 9, 11.5f);
                    break;
                case "corner":
                    s.Poly(false, 3, 13, 3, 8, 4, 5, 6, 3.5f, 9, 3, 13, 3);
                    break;
                case "corners":
                    s.Poly(false, 2.5f, 6, 2.5f, 2.5f, 6, 2.5f);
                    s.Poly(false, 10, 2.5f, 13.5f, 2.5f, 13.5f, 6);
                    s.Poly(false, 13.5f, 10, 13.5f, 13.5f, 10, 13.5f);
                    s.Poly(false, 6, 13.5f, 2.5f, 13.5f, 2.5f, 10);
                    break;
                case "opacity":
                    s.Circle(8, 8, 5.5f, false);
                    s.Fill(8, 2.5f, 8, 13.5f, 6, 13.1f, 4.3f, 12, 3, 10.3f, 2.5f, 8, 3, 5.7f, 4.3f, 4, 6, 2.9f);
                    break;
                case "rotate":
                    s.Poly(false, 12.5f, 8, 12, 5.5f, 10.5f, 3.8f, 8, 3, 5.5f, 3.8f, 4, 5.5f, 3.5f, 8, 4, 10.5f, 5.5f, 12.2f, 8, 13, 10.5f, 12.2f);
                    s.Fill(12.5f, 9.5f, 10, 13.5f, 9, 10.5f);
                    break;
                case "link":
                    s.Poly(false, 7, 9, 5.5f, 10.5f, 4, 10.5f, 3, 9.5f, 3, 8, 4.5f, 6.5f);
                    s.Poly(false, 9, 7, 10.5f, 5.5f, 12, 5.5f, 13, 6.5f, 13, 8, 11.5f, 9.5f);
                    s.Line(6.5f, 9.5f, 9.5f, 6.5f);
                    break;
                case "export":
                    s.Line(8, 2.5f, 8, 10);
                    s.Poly(false, 5, 5.5f, 8, 2.5f, 11, 5.5f);
                    s.Poly(false, 3, 10, 3, 13.5f, 13, 13.5f, 13, 10);
                    break;
                case "dots":
                    s.Circle(4, 8, 1.2f, true);
                    s.Circle(8, 8, 1.2f, true);
                    s.Circle(12, 8, 1.2f, true);
                    break;
                case "target":
                    s.Circle(8, 8, 5, false);
                    s.Circle(8, 8, 1.5f, true);
                    break;
                case "zoom_fit":
                    s.Poly(false, 2.5f, 6, 2.5f, 2.5f, 6, 2.5f);
                    s.Poly(false, 10, 2.5f, 13.5f, 2.5f, 13.5f, 6);
                    s.Poly(false, 13.5f, 10, 13.5f, 13.5f, 10, 13.5f);
                    s.Poly(false, 6, 13.5f, 2.5f, 13.5f, 2.5f, 10);
                    s.Rect(5.5f, 5.5f, 5, 5);
                    break;
                default:
                    return false;
            }

            return true;
        }

        /// <summary>The icon for a layer type in the layers panel.</summary>
        public static string TypeIcon(string type, string stackMode)
        {
            switch (type)
            {
                case "FRAME":
                    return stackMode == "VERTICAL" ? "stack_v" : stackMode == "HORIZONTAL" ? "stack_h" : "frame";
                case "SYMBOL":
                case "COMPONENT_SET":
                    return "component";
                case "INSTANCE":
                    return "instance";
                case "GROUP":
                    return "group";
                case "SECTION":
                    return "section";
                case "TEXT":
                case "TEXT_PATH":
                    return "text";
                case "RECTANGLE":
                case "ROUNDED_RECTANGLE":
                    return "rect";
                case "ELLIPSE":
                    return "ellipse";
                case "LINE":
                    return "line";
                case "BOOLEAN_OPERATION":
                    return "boolean";
                case "STAR":
                case "REGULAR_POLYGON":
                    return "star";
                case "CANVAS":
                    return "page";
                default:
                    return "vector";
            }
        }
    }
}
