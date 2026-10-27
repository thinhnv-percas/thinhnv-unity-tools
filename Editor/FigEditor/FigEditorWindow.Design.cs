using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// The Design tab, section by section like figcraft / Figma: alignment, position and rotation,
    /// dimensions, auto layout, appearance (visibility, opacity, blend, corner radii), typography,
    /// fills, strokes, effects and export. Every edit is undoable and re-renders only that layer.
    /// Layers inside an instance are shown read-only.
    /// </summary>
    public partial class FigEditorWindow
    {
        private static readonly string[] BlendModes =
        {
            "PASS_THROUGH", "NORMAL", "DARKEN", "MULTIPLY", "LINEAR_BURN", "COLOR_BURN", "LIGHTEN", "SCREEN", "LINEAR_DODGE", "COLOR_DODGE",
            "OVERLAY", "SOFT_LIGHT", "HARD_LIGHT", "DIFFERENCE", "EXCLUSION", "HUE", "SATURATION", "COLOR", "LUMINOSITY",
        };

        private static readonly string[] EffectTypes = { "DROP_SHADOW", "INNER_SHADOW", "FOREGROUND_BLUR", "BACKGROUND_BLUR" };
        private static readonly string[] EffectLabels = { "Drop shadow", "Inner shadow", "Layer blur", "Background blur" };
        private static readonly float[] ExportScales = { 0.5f, 1f, 2f, 3f, 4f };

        private Vector2 designScroll;
        private bool keepRatio;
        private int exportScale = 2;
        private readonly HashSet<string> openEffects = new();
        private readonly HashSet<string> openPaints = new();

        private void DrawDesignPanel(float width)
        {
            designScroll = EditorGUILayout.BeginScrollView(designScroll);
            float labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 18;

            FigSceneNode s = SelectedSceneNode;
            FigNode n = Selected;
            if (s == null && n == null)
            {
                DrawPageSection();
            }
            else
            {
                FigView view = s?.View ?? new FigView(n.Change);
                bool readOnly = n == null || (s != null && s.Expanded);
                DrawDesignHeader(s, n, readOnly);
                using (new EditorGUI.DisabledScope(readOnly))
                {
                    DrawAlignSection();
                    DrawLayoutSection(s, n, view);
                    DrawAutoLayoutSection(view);
                    DrawAppearanceSection(s, n, view);
                    if (FigScene.IsText(s?.Type ?? n.Type))
                    {
                        DrawTypographySection(n, view);
                    }

                    DrawPaintsSection(n, view, "fillPaints", "Fill");
                    DrawPaintsSection(n, view, "strokePaints", "Stroke");
                    DrawEffectsSection(n, view);
                }

                DrawExportSection(s);
            }

            EditorGUIUtility.labelWidth = labelWidth;
            GUILayout.Space(24);
            EditorGUILayout.EndScrollView();
        }

        // ─── Section chrome ──────────────────────────────────────────────────────

        private static bool SectionHeader(string title, string addTooltip = null)
        {
            GUILayout.Space(10);
            Rect r = GUILayoutUtility.GetRect(10, 20, GUILayout.ExpandWidth(true));
            GUI.Label(new Rect(r.x + 12, r.y, r.width - 40, r.height), title, FigTheme.Bold);
            bool add = addTooltip != null && FigTheme.IconButton(new Rect(r.xMax - 30, r.y, 22, 20), "plus", addTooltip);
            GUILayout.Space(4);
            return add;
        }

        private static void SectionEnd()
        {
            GUILayout.Space(10);
            Rect r = GUILayoutUtility.GetRect(10, 1, GUILayout.ExpandWidth(true));
            FigTheme.Separator(r);
        }

        private static void BeginRow()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(12);
        }

        private static void EndRow()
        {
            GUILayout.Space(10);
            GUILayout.EndHorizontal();
            GUILayout.Space(2);
        }

        private static float NumberField(string prefix, float value, float width = 0, string tooltip = null)
        {
            var content = new GUIContent(prefix, tooltip ?? prefix);
            return width > 0 ? EditorGUILayout.FloatField(content, value, GUILayout.Width(width)) : EditorGUILayout.FloatField(content, value, GUILayout.MinWidth(40));
        }

        private static string Format(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        // ─── Header and page ─────────────────────────────────────────────────────

        private void DrawPageSection()
        {
            FigNode page = file.Find(boardPageId);
            SectionHeader("Page");
            if (page != null)
            {
                KiwiValue bg = page.Change.Get("backgroundColor");
                BeginRow();
                GUILayout.Label("Background", FigTheme.Dim, GUILayout.Width(80));
                Color c = bg != null ? FigPaint.ReadColor(bg) : scene?.Background ?? Color.gray;
                EditorGUI.BeginChangeCheck();
                Color picked = EditorGUILayout.ColorField(GUIContent.none, c, true, false, false);
                if (EditorGUI.EndChangeCheck())
                {
                    RecordFields("Page Background", "pagebg", page.Change, "backgroundColor");
                    KiwiValue color = bg ?? FigFile.Default(page.Change.Def, "backgroundColor");
                    if (color != null)
                    {
                        SetFloat(color, "r", picked.r);
                        SetFloat(color, "g", picked.g);
                        SetFloat(color, "b", picked.b);
                        SetFloat(color, "a", 1f);
                        page.Change.Set("backgroundColor", color);
                        MarkDirty();
                        InvalidateBoard();
                    }
                }

                EndRow();
            }

            SectionEnd();
            GUILayout.Space(8);
            GUILayout.Label("  Select a layer on the canvas or in the layers panel to edit it.", FigTheme.Dim);
            GUILayout.Label($"  {scene?.Nodes.Length ?? 0} layers drawn on this page · {file.Nodes.Count} in the file", FigTheme.Faint);
        }

        private void DrawDesignHeader(FigSceneNode s, FigNode n, bool readOnly)
        {
            string type = s?.Type ?? n.Type;
            GUILayout.Space(8);
            BeginRow();
            Rect icon = GUILayoutUtility.GetRect(18, 18, GUILayout.Width(18));
            bool component = type == "SYMBOL" || type == "INSTANCE" || (s != null && s.Expanded);
            FigTheme.DrawIcon(icon, FigTheme.TypeIcon(type, (s?.View ?? new FigView(n.Change)).EnumName("stackMode")), component ? FigTheme.Component : FigTheme.TextDim, 14);
            GUILayout.Label(TypeLabel(type), FigTheme.Bold);
            GUILayout.FlexibleSpace();
            if (boardSelection.Count > 1)
            {
                GUILayout.Label(boardSelection.Count + " selected", FigTheme.Faint);
            }

            EndRow();

            if (type == "INSTANCE" && (s?.View ?? new FigView(n.Change)) is FigView iv)
            {
                string main = FigUtil.GuidString(iv.Get("overriddenSymbolID")) ?? FigUtil.GuidString(iv.Path("symbolData.symbolID"));
                FigNode mainNode = file.Find(main);
                BeginRow();
                GUILayout.Label(mainNode != null ? "❖ " + mainNode.Label : "Main component not in this file", FigTheme.Dim);
                GUILayout.FlexibleSpace();
                if (mainNode != null && GUILayout.Button("Go to main", EditorStyles.miniButton, GUILayout.Width(80)))
                {
                    SetBoardSelection(main, false);
                    pendingZoomTo = main;
                }

                EndRow();
            }

            if (readOnly && s != null && s.Expanded)
            {
                BeginRow();
                GUILayout.Label("Inside the instance “" + s.Owner.Label + "”: edit its main component to change it.", FigTheme.Faint);
                EndRow();
            }

            SectionEnd();
        }

        private static string TypeLabel(string type)
        {
            switch (type)
            {
                case "SYMBOL":
                    return "Component";
                case "COMPONENT_SET":
                    return "Component set";
                case "INSTANCE":
                    return "Instance";
                case "ROUNDED_RECTANGLE":
                    return "Rectangle";
                case "BOOLEAN_OPERATION":
                    return "Boolean";
                case "REGULAR_POLYGON":
                    return "Polygon";
                case "TEXT_PATH":
                    return "Text on path";
                default:
                    return type.Length > 0 ? type.Substring(0, 1) + type.Substring(1).ToLowerInvariant() : type;
            }
        }

        // ─── Alignment ───────────────────────────────────────────────────────────

        private void DrawAlignSection()
        {
            GUILayout.Space(6);
            Rect r = GUILayoutUtility.GetRect(10, 24, GUILayout.ExpandWidth(true));
            string[] icons = { "align_left", "align_hcenter", "align_right", "align_top", "align_vcenter", "align_bottom" };
            string[] tips = { "Align left", "Align horizontal centers", "Align right", "Align top", "Align vertical centers", "Align bottom" };
            for (int i = 0; i < 6; i++)
            {
                if (FigTheme.IconButton(new Rect(r.x + 12 + i * 30, r.y, 26, 24), icons[i], tips[i]))
                {
                    int mode = i;
                    Defer(() => AlignSelection(mode));
                }
            }

            SectionEnd();
        }

        /// <summary>Align the selection to its bounds (several layers) or to the parent (one layer).</summary>
        private void AlignSelection(int mode)
        {
            List<string> ids = TopMostSelection();
            if (ids.Count == 0)
            {
                return;
            }

            Rect target;
            if (ids.Count > 1)
            {
                target = SelectionBounds() ?? default;
            }
            else
            {
                FigSceneNode only = scene.Find(ids[0]);
                if (only == null || only.ParentIndex < 0)
                {
                    return;
                }

                target = scene.Nodes[only.ParentIndex].Bounds;
            }

            if (!BeginUndo("Align", null))
            {
                return;
            }

            foreach (string id in ids)
            {
                undo.Capture(scene.Find(id)?.Source.Change, "transform");
            }

            CommitUndo("Align");
            foreach (string id in ids)
            {
                FigSceneNode n = scene.Find(id);
                KiwiValue t = n?.Source.Change.Get("transform");
                if (t == null || n.Locked)
                {
                    continue;
                }

                Rect b = n.Bounds;
                Vector2 d = Vector2.zero;
                switch (mode)
                {
                    case 0: d.x = target.xMin - b.xMin; break;
                    case 1: d.x = target.center.x - b.center.x; break;
                    case 2: d.x = target.xMax - b.xMax; break;
                    case 3: d.y = target.yMin - b.yMin; break;
                    case 4: d.y = target.center.y - b.center.y; break;
                    default: d.y = target.yMax - b.yMax; break;
                }

                Vector2 local = n.ParentWorld.Inverse().ApplyVector(d);
                float x = (t.Get("m02")?.AsFloat ?? 0) + local.x, y = (t.Get("m12")?.AsFloat ?? 0) + local.y;
                if (snapToPixel)
                {
                    x = Mathf.Round(x);
                    y = Mathf.Round(y);
                }

                SetFloat(t, "m02", x);
                SetFloat(t, "m12", y);
                RefreshNode(n.Index, false);
            }

            MarkDirty();
        }

        // ─── Position, rotation, dimensions ──────────────────────────────────────

        private void DrawLayoutSection(FigSceneNode s, FigNode n, FigView view)
        {
            KiwiValue change = n?.Change;
            FigAffine t = FigAffine.FromMatrix(view.Get("transform"));
            float w = s?.Width ?? view.Get("size")?.Get("x")?.AsFloat ?? 0;
            float h = s?.Height ?? view.Get("size")?.Get("y")?.AsFloat ?? 0;

            SectionHeader("Position");
            BeginRow();
            EditorGUI.BeginChangeCheck();
            float x = NumberField("X", t.C);
            GUILayout.Space(8);
            float y = NumberField("Y", t.F);
            if (EditorGUI.EndChangeCheck() && change?.Get("transform") is KiwiValue tr)
            {
                RecordFields("Move", "position:" + n.Id, change, "transform");
                SetFloat(tr, "m02", x);
                SetFloat(tr, "m12", y);
                MarkDirty();
                InvalidateBoardNode(n.Id);
            }

            EndRow();

            BeginRow();
            float rotation = -Mathf.Atan2(t.D, t.A) * Mathf.Rad2Deg;
            EditorGUI.BeginChangeCheck();
            float newRotation = NumberField("⟳", (float)Math.Round(rotation, 2), 0, "Rotation (degrees)");
            if (EditorGUI.EndChangeCheck() && change?.Get("transform") is KiwiValue rt)
            {
                RecordFields("Rotate", "rotate:" + n.Id, change, "transform");
                Rotate(rt, w, h, -newRotation * Mathf.Deg2Rad);
                MarkDirty();
                InvalidateBoardNode(n.Id);
            }

            GUILayout.Space(8);
            if (GUILayout.Button(new GUIContent("Flip H", "Flip horizontally"), EditorStyles.miniButtonLeft) && change?.Get("transform") is KiwiValue fh)
            {
                RecordFields("Flip", null, change, "transform");
                Flip(fh, w, h, true);
                MarkDirty();
                InvalidateBoardNode(n.Id);
            }

            if (GUILayout.Button(new GUIContent("Flip V", "Flip vertically"), EditorStyles.miniButtonRight) && change?.Get("transform") is KiwiValue fv)
            {
                RecordFields("Flip", null, change, "transform");
                Flip(fv, w, h, false);
                MarkDirty();
                InvalidateBoardNode(n.Id);
            }

            EndRow();
            SectionEnd();

            SectionHeader("Dimensions");
            BeginRow();
            bool resizable = s == null || s.Resizable;
            using (new EditorGUI.DisabledScope(!resizable))
            {
                EditorGUI.BeginChangeCheck();
                float nw = NumberField("W", w);
                GUILayout.Space(8);
                float nh = NumberField("H", h);
                if (EditorGUI.EndChangeCheck() && change?.Get("size") is KiwiValue size)
                {
                    if (keepRatio && w > 0 && h > 0)
                    {
                        if (!Mathf.Approximately(nw, w))
                        {
                            nh = nw * h / w;
                        }
                        else if (!Mathf.Approximately(nh, h))
                        {
                            nw = nh * w / h;
                        }
                    }

                    RecordFields("Resize", "size:" + n.Id, change, "size", "fillGeometry", "strokeGeometry", "derivedTextData", "textAutoResize");
                    SetFloat(size, "x", Mathf.Max(0.01f, nw));
                    SetFloat(size, "y", Mathf.Max(0.01f, nh));
                    change.Remove("fillGeometry");
                    change.Remove("strokeGeometry");
                    if (n.Type == "TEXT")
                    {
                        change.Remove("derivedTextData");
                        FigFile.SetEnum(change, "textAutoResize", Mathf.Approximately(nh, h) ? "HEIGHT" : "NONE");
                    }

                    MarkDirty();
                    InvalidateBoardNode(n.Id);
                }
            }

            if (FigTheme.IconButton(GUILayoutUtility.GetRect(22, 18, GUILayout.Width(22)), "link", keepRatio ? "Proportions locked" : "Lock proportions", keepRatio))
            {
                keepRatio = !keepRatio;
            }

            EndRow();
            if (!resizable)
            {
                BeginRow();
                GUILayout.Label("Vectors, booleans and groups resize with their contents.", FigTheme.Faint);
                EndRow();
            }

            string type = s?.Type ?? n.Type;
            if (type == "FRAME" || type == "SYMBOL" || type == "INSTANCE")
            {
                BeginRow();
                EditorGUI.BeginChangeCheck();
                bool clip = EditorGUILayout.ToggleLeft(new GUIContent("Clip content", "Children outside the frame are hidden"), !view.Bool("frameMaskDisabled", false));
                if (EditorGUI.EndChangeCheck() && change != null)
                {
                    RecordFields("Clip Content", null, change, "frameMaskDisabled");
                    change.Set("frameMaskDisabled", KiwiValue.FromBool(!clip));
                    MarkDirty();
                    InvalidateBoardNode(n.Id);
                }

                EndRow();
            }

            SectionEnd();
        }

        /// <summary>Set the rotation, keeping the scale and the layer's centre in place.</summary>
        private static void Rotate(KiwiValue t, float w, float h, float angle)
        {
            FigAffine old = FigAffine.FromMatrix(t);
            float sx = Mathf.Sqrt(old.A * old.A + old.D * old.D), sy = Mathf.Sqrt(old.B * old.B + old.E * old.E);
            Vector2 center = old.Apply(w * 0.5f, h * 0.5f);
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            var neu = new FigAffine { A = c * sx, B = -s * sy, D = s * sx, E = c * sy };
            Vector2 at = neu.Apply(w * 0.5f, h * 0.5f);
            SetMatrix(t, neu.A, neu.B, center.x - at.x, neu.D, neu.E, center.y - at.y);
        }

        private static void Flip(KiwiValue t, float w, float h, bool horizontal)
        {
            FigAffine old = FigAffine.FromMatrix(t);
            // Mirror in the layer's own space around its centre.
            var mirror = horizontal ? new FigAffine { A = -1, E = 1, C = w } : new FigAffine { A = 1, E = -1, F = h };
            FigAffine neu = old.Then(mirror);
            SetMatrix(t, neu.A, neu.B, neu.C, neu.D, neu.E, neu.F);
        }

        private static void SetMatrix(KiwiValue t, float a, float b, float c, float d, float e, float f)
        {
            SetFloat(t, "m00", a);
            SetFloat(t, "m01", b);
            SetFloat(t, "m02", c);
            SetFloat(t, "m10", d);
            SetFloat(t, "m11", e);
            SetFloat(t, "m12", f);
        }

        private void DrawAutoLayoutSection(FigView view)
        {
            string mode = view.EnumName("stackMode");
            if (mode == null || mode == "NONE")
            {
                return;
            }

            SectionHeader("Auto layout");
            BeginRow();
            FigTheme.DrawIcon(GUILayoutUtility.GetRect(18, 18, GUILayout.Width(18)), mode == "VERTICAL" ? "stack_v" : "stack_h", FigTheme.Text, 14);
            GUILayout.Label(mode == "VERTICAL" ? "Vertical" : mode == "HORIZONTAL" ? "Horizontal" : "Grid", FigTheme.Label);
            GUILayout.FlexibleSpace();
            GUILayout.Label("Gap " + Format(view.Float("stackSpacing", 0)), FigTheme.Dim);
            EndRow();
            BeginRow();
            float left = view.Float("stackHorizontalPadding", 0), top = view.Float("stackVerticalPadding", 0);
            float right = view.Float("stackPaddingRight", left), bottom = view.Float("stackPaddingBottom", top);
            GUILayout.Label($"Padding {Format(top)} {Format(right)} {Format(bottom)} {Format(left)}", FigTheme.Dim);
            EndRow();
            BeginRow();
            GUILayout.Label("Auto layout is shown as Figma laid it out; it is not recomputed here.", FigTheme.Faint);
            EndRow();
            SectionEnd();
        }

        // ─── Appearance ──────────────────────────────────────────────────────────

        private void DrawAppearanceSection(FigSceneNode s, FigNode n, FigView view)
        {
            KiwiValue change = n?.Change;
            SectionHeader("Appearance");
            BeginRow();
            bool visible = view.Bool("visible", true);
            if (FigTheme.IconButton(GUILayoutUtility.GetRect(24, 20, GUILayout.Width(24)), visible ? "eye" : "eye_off", visible ? "Hide" : "Show") && change != null)
            {
                RecordFields("Show/Hide", null, change, "visible");
                change.Set("visible", KiwiValue.FromBool(!visible));
                MarkDirty();
                OnStructureChanged();
            }

            EditorGUI.BeginChangeCheck();
            float opacity = NumberField("%", Mathf.Round(view.Float("opacity", 1f) * 1000f) / 10f, 70, "Layer opacity (%)");
            if (EditorGUI.EndChangeCheck() && change != null)
            {
                RecordFields("Opacity", "opacity:" + n.Id, change, "opacity");
                change.Set("opacity", KiwiValue.FromFloat(Mathf.Clamp01(opacity / 100f)));
                MarkDirty();
                InvalidateBoardNode(n.Id);
            }

            GUILayout.Space(8);
            string blend = view.EnumName("blendMode") ?? "PASS_THROUGH";
            int bi = Array.IndexOf(BlendModes, blend);
            EditorGUI.BeginChangeCheck();
            int picked = EditorGUILayout.Popup(Math.Max(0, bi), Array.ConvertAll(BlendModes, Pretty));
            if (EditorGUI.EndChangeCheck() && change != null && picked != bi)
            {
                RecordFields("Blend Mode", null, change, "blendMode");
                FigFile.SetEnum(change, "blendMode", BlendModes[picked]);
                MarkDirty();
                InvalidateBoardNode(n.Id);
            }

            EndRow();

            string type = s?.Type ?? n.Type;
            bool rounded = type == "RECTANGLE" || type == "ROUNDED_RECTANGLE" || type == "FRAME" || type == "SYMBOL" || type == "INSTANCE" || type == "COMPONENT_SET";
            if (rounded)
            {
                bool independent = view.Bool("rectangleCornerRadiiIndependent", false);
                BeginRow();
                if (!independent)
                {
                    EditorGUI.BeginChangeCheck();
                    float radius = NumberField("◜", view.Float("cornerRadius", 0), 0, "Corner radius");
                    if (EditorGUI.EndChangeCheck() && change != null)
                    {
                        RecordFields("Corner Radius", "radius:" + n.Id, change, "cornerRadius", "fillGeometry", "strokeGeometry");
                        change.Set("cornerRadius", KiwiValue.FromFloat(Mathf.Max(0, radius)));
                        change.Remove("fillGeometry");
                        change.Remove("strokeGeometry");
                        MarkDirty();
                        InvalidateBoardNode(n.Id);
                    }
                }
                else
                {
                    GUILayout.Label("Corners", FigTheme.Dim);
                }

                GUILayout.FlexibleSpace();
                if (FigTheme.IconButton(GUILayoutUtility.GetRect(24, 20, GUILayout.Width(24)), "corners", "Independent corners", independent) && change != null)
                {
                    RecordFields("Independent Corners", null, change, "rectangleCornerRadiiIndependent", "rectangleTopLeftCornerRadius",
                        "rectangleTopRightCornerRadius", "rectangleBottomRightCornerRadius", "rectangleBottomLeftCornerRadius");
                    float r0 = view.Float("cornerRadius", 0);
                    change.Set("rectangleCornerRadiiIndependent", KiwiValue.FromBool(!independent));
                    if (!independent)
                    {
                        foreach (string f in new[] { "rectangleTopLeftCornerRadius", "rectangleTopRightCornerRadius", "rectangleBottomRightCornerRadius", "rectangleBottomLeftCornerRadius" })
                        {
                            if (change.Get(f) == null)
                            {
                                change.Set(f, KiwiValue.FromFloat(r0));
                            }
                        }
                    }

                    MarkDirty();
                    InvalidateBoardNode(n.Id);
                }

                EndRow();
                if (independent)
                {
                    string[] fields = { "rectangleTopLeftCornerRadius", "rectangleTopRightCornerRadius", "rectangleBottomLeftCornerRadius", "rectangleBottomRightCornerRadius" };
                    string[] labels = { "↖", "↗", "↙", "↘" };
                    for (int row = 0; row < 2; row++)
                    {
                        BeginRow();
                        for (int col = 0; col < 2; col++)
                        {
                            int i = row * 2 + col;
                            EditorGUI.BeginChangeCheck();
                            float v = NumberField(labels[i], view.Float(fields[i], view.Float("cornerRadius", 0)));
                            if (EditorGUI.EndChangeCheck() && change != null)
                            {
                                RecordFields("Corner Radius", "corner" + i + ":" + n.Id, change, fields[i], "fillGeometry", "strokeGeometry");
                                change.Set(fields[i], KiwiValue.FromFloat(Mathf.Max(0, v)));
                                change.Remove("fillGeometry");
                                change.Remove("strokeGeometry");
                                MarkDirty();
                                InvalidateBoardNode(n.Id);
                            }

                            if (col == 0)
                            {
                                GUILayout.Space(8);
                            }
                        }

                        EndRow();
                    }
                }
            }

            SectionEnd();
        }

        private static string Pretty(string enumName)
        {
            string s = enumName.Replace('_', ' ').ToLowerInvariant();
            return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s;
        }

        // ─── Typography ──────────────────────────────────────────────────────────

        private void DrawTypographySection(FigNode n, FigView view)
        {
            KiwiValue change = n?.Change;
            SectionHeader("Text");
            if (n?.Type == "TEXT_PATH")
            {
                BeginRow();
                GUILayout.Label("Text on a path. Edited text is drawn off the path until Figma lays it out again.", EditorStyles.wordWrappedMiniLabel);
                EndRow();
            }

            BeginRow();
            EditorGUI.BeginChangeCheck();
            GUI.SetNextControlName("FigTextArea");
            string text = EditorGUILayout.TextArea(view.Path("textData.characters")?.String ?? "", GUILayout.MinHeight(44));
            if (EditorGUI.EndChangeCheck() && n != null)
            {
                RecordFields("Edit Text", "text:" + n.Id, change, "textData", "derivedTextData");
                FigFile.SetText(n, text);
                MarkDirty();
                InvalidateBoardNode(n.Id);
            }

            EndRow();

            BeginRow();
            EditorGUIUtility.labelWidth = 0.01f;
            EditorGUI.BeginChangeCheck();
            string family = EditorGUILayout.DelayedTextField(view.Path("fontName.family")?.String ?? "", GUILayout.MinWidth(60));
            string style = EditorGUILayout.DelayedTextField(view.Path("fontName.style")?.String ?? "", GUILayout.Width(80));
            if (EditorGUI.EndChangeCheck() && change != null)
            {
                RecordFields("Font", null, change, "fontName", "derivedTextData");
                KiwiValue font = change.Get("fontName") ?? FigFile.Default(change.Def, "fontName");
                if (font != null)
                {
                    font.Set("family", KiwiValue.FromString(family));
                    font.Set("style", KiwiValue.FromString(style));
                    font.Set("postscript", KiwiValue.FromString(""));
                    change.Set("fontName", font);
                    change.Remove("derivedTextData");
                    MarkDirty();
                    InvalidateBoardNode(n.Id);
                }
            }

            EditorGUIUtility.labelWidth = 18;
            EndRow();

            BeginRow();
            EditorGUI.BeginChangeCheck();
            float size = NumberField("Aa", view.Float("fontSize", 12), 0, "Font size");
            if (EditorGUI.EndChangeCheck() && change != null)
            {
                RecordFields("Font Size", "fontsize:" + n.Id, change, "fontSize", "derivedTextData");
                change.Set("fontSize", KiwiValue.FromFloat(Mathf.Max(1, size)));
                change.Remove("derivedTextData");
                MarkDirty();
                InvalidateBoardNode(n.Id);
            }

            GUILayout.Space(8);
            DrawNumberWithUnits(n, view, "lineHeight", "↕", "Line height");
            GUILayout.Space(8);
            DrawNumberWithUnits(n, view, "letterSpacing", "↔", "Letter spacing");
            EndRow();

            BeginRow();
            string[] hNames = { "LEFT", "CENTER", "RIGHT", "JUSTIFIED" };
            string[] hIcons = { "align_left", "align_hcenter", "align_right", "menu" };
            string currentH = view.EnumName("textAlignHorizontal") ?? "LEFT";
            for (int i = 0; i < hNames.Length; i++)
            {
                if (FigTheme.IconButton(GUILayoutUtility.GetRect(26, 22, GUILayout.Width(26)), hIcons[i], "Align " + hNames[i].ToLowerInvariant(), currentH == hNames[i]) && change != null)
                {
                    SetTextEnum(n, "textAlignHorizontal", hNames[i]);
                }
            }

            GUILayout.Space(8);
            string[] vNames = { "TOP", "CENTER", "BOTTOM" };
            string[] vIcons = { "align_top", "align_vcenter", "align_bottom" };
            string currentV = view.EnumName("textAlignVertical") ?? "TOP";
            for (int i = 0; i < vNames.Length; i++)
            {
                if (FigTheme.IconButton(GUILayoutUtility.GetRect(26, 22, GUILayout.Width(26)), vIcons[i], "Align " + vNames[i].ToLowerInvariant(), currentV == vNames[i]) && change != null)
                {
                    SetTextEnum(n, "textAlignVertical", vNames[i]);
                }
            }

            EndRow();

            BeginRow();
            string[] resize = { "WIDTH_AND_HEIGHT", "HEIGHT", "NONE" };
            string[] resizeLabels = { "Auto width", "Auto height", "Fixed size" };
            int ri = Math.Max(0, Array.IndexOf(resize, view.EnumName("textAutoResize") ?? "NONE"));
            EditorGUI.BeginChangeCheck();
            int pick = GUILayout.Toolbar(ri, resizeLabels, EditorStyles.miniButton);
            if (EditorGUI.EndChangeCheck() && change != null && pick != ri)
            {
                SetTextEnum(n, "textAutoResize", resize[pick]);
            }

            EndRow();
            if (view.Get("derivedTextData") == null)
            {
                BeginRow();
                GUILayout.Label("Drawn with the installed font; Figma lays it out again when the file opens.", FigTheme.Faint);
                EndRow();
            }

            SectionEnd();
        }

        private void SetTextEnum(FigNode n, string field, string value)
        {
            RecordFields("Text", null, n.Change, field, "derivedTextData");
            FigFile.SetEnum(n.Change, field, value);
            n.Change.Remove("derivedTextData");
            MarkDirty();
            InvalidateBoardNode(n.Id);
        }

        /// <summary>A <c>Number</c> struct field (value + units) such as line height or letter spacing.</summary>
        private void DrawNumberWithUnits(FigNode n, FigView view, string field, string prefix, string tooltip)
        {
            KiwiValue number = view.Get(field);
            string units = number?.Get("units")?.EnumName ?? "RAW";
            float value = number?.Get("value")?.AsFloat ?? 0;
            string suffix = units == "PERCENT" ? "%" : units == "PIXELS" ? "px" : "";
            EditorGUI.BeginChangeCheck();
            float v = NumberField(prefix, value, 0, tooltip + (suffix.Length > 0 ? " (" + suffix + ")" : ""));
            if (EditorGUI.EndChangeCheck() && n != null && number != null)
            {
                RecordFields(tooltip, field + ":" + n.Id, n.Change, field, "derivedTextData");
                KiwiValue copy = number.Clone();
                SetFloat(copy, "value", v);
                n.Change.Set(field, copy);
                n.Change.Remove("derivedTextData");
                MarkDirty();
                InvalidateBoardNode(n.Id);
            }
        }

        // ─── Fills and strokes ───────────────────────────────────────────────────

        private void DrawPaintsSection(FigNode n, FigView view, string field, string title)
        {
            KiwiValue change = n?.Change;
            List<KiwiValue> paints = view.List(field);
            if (SectionHeader(title, "Add " + title.ToLowerInvariant()) && change != null)
            {
                RecordFields("Add " + title, null, change, field);
                KiwiValue paint = FigFile.SolidPaint(change, field, field == "fillPaints" ? 0.85f : 0f, field == "fillPaints" ? 0.85f : 0f, field == "fillPaints" ? 0.85f : 0f, 1f);
                if (paint != null)
                {
                    List<KiwiValue> list = change.Get(field)?.Items;
                    if (list == null)
                    {
                        change.Set(field, KiwiValue.FromArray(new List<KiwiValue> { paint }));
                    }
                    else
                    {
                        list.Add(paint);
                    }

                    if (field == "strokePaints" && change.Get("strokeWeight") == null)
                    {
                        change.Set("strokeWeight", KiwiValue.FromFloat(1));
                    }

                    MarkDirty();
                    InvalidateBoardNode(n.Id);
                    GUIUtility.ExitGUI();
                }
            }

            if (paints != null)
            {
                // Top-most paint first, like Figma.
                for (int i = paints.Count - 1; i >= 0; i--)
                {
                    if (DrawPaintRow(n, field, paints[i], i))
                    {
                        GUIUtility.ExitGUI();
                    }
                }
            }

            if (field == "strokePaints" && paints != null && paints.Count > 0)
            {
                BeginRow();
                EditorGUI.BeginChangeCheck();
                float weight = NumberField("≡", view.Float("strokeWeight", 1), 0, "Stroke weight");
                if (EditorGUI.EndChangeCheck() && change != null)
                {
                    RecordFields("Stroke Weight", "strokeweight:" + n.Id, change, "strokeWeight", "strokeGeometry");
                    change.Set("strokeWeight", KiwiValue.FromFloat(Mathf.Max(0, weight)));
                    change.Remove("strokeGeometry");
                    MarkDirty();
                    InvalidateBoardNode(n.Id);
                }

                GUILayout.Space(8);
                string[] aligns = { "INSIDE", "CENTER", "OUTSIDE" };
                int ai = Math.Max(0, Array.IndexOf(aligns, view.EnumName("strokeAlign") ?? "INSIDE"));
                EditorGUI.BeginChangeCheck();
                int pick = EditorGUILayout.Popup(ai, new[] { "Inside", "Center", "Outside" });
                if (EditorGUI.EndChangeCheck() && change != null && pick != ai)
                {
                    RecordFields("Stroke Align", null, change, "strokeAlign", "strokeGeometry");
                    FigFile.SetEnum(change, "strokeAlign", aligns[pick]);
                    change.Remove("strokeGeometry");
                    MarkDirty();
                    InvalidateBoardNode(n.Id);
                }

                EndRow();
            }

            SectionEnd();
        }

        /// <summary>One paint: swatch / preview, value, opacity, visibility, remove. Returns true when the list changed.</summary>
        private bool DrawPaintRow(FigNode n, string field, KiwiValue paint, int index)
        {
            KiwiValue change = n?.Change;
            string type = paint.Get("type")?.EnumName ?? "SOLID";
            bool visible = paint.Get("visible")?.AsBool ?? true;
            string key = (n?.Id ?? "") + "/" + field + index;
            bool open = openPaints.Contains(key) && type != "IMAGE";
            bool changedList = false;
            BeginRow();
            if (type != "IMAGE" && FigTheme.IconButton(GUILayoutUtility.GetRect(20, 20, GUILayout.Width(20)), open ? "chevron_down" : "chevron_right", "Edit paint"))
            {
                TogglePaintEditor(key);
            }

            Color oldColor = GUI.color;
            if (!visible)
            {
                GUI.color = new Color(1, 1, 1, 0.5f);
            }

            switch (type)
            {
                case "SOLID":
                {
                    KiwiValue color = paint.Get("color");
                    Color c = color != null ? FigPaint.ReadColor(color) : Color.black;
                    c.a = 1;
                    EditorGUI.BeginChangeCheck();
                    Color picked = EditorGUILayout.ColorField(GUIContent.none, c, true, false, false, GUILayout.Width(36));
                    string hex = ColorUtility.ToHtmlStringRGB(c);
                    string typed = EditorGUILayout.DelayedTextField(hex, GUILayout.MinWidth(54));
                    if (EditorGUI.EndChangeCheck() && change != null && color != null)
                    {
                        if (typed != hex && ColorUtility.TryParseHtmlString("#" + typed.TrimStart('#'), out Color parsed))
                        {
                            picked = parsed;
                        }

                        RecordFields("Paint Colour", field + index + ":" + n.Id, change, field);
                        KiwiValue live = change.Get(field)?.Items?[index]?.Get("color");
                        if (live != null)
                        {
                            SetFloat(live, "r", picked.r);
                            SetFloat(live, "g", picked.g);
                            SetFloat(live, "b", picked.b);
                        }

                        MarkDirty();
                        InvalidateBoardNode(n.Id);
                    }

                    break;
                }
                case "IMAGE":
                {
                    byte[] hash = paint.Path("image.hash")?.Bytes;
                    Texture2D tex = hash != null ? ImageTexture(FigUtil.Hex(hash)) : null;
                    Rect r = GUILayoutUtility.GetRect(36, 20, GUILayout.Width(36));
                    if (tex != null)
                    {
                        GUI.DrawTexture(r, tex, ScaleMode.ScaleAndCrop);
                    }

                    string[] modes = { "FILL", "FIT", "STRETCH", "TILE" };
                    int mi = Math.Max(0, Array.IndexOf(modes, paint.Get("imageScaleMode")?.EnumName ?? "FILL"));
                    EditorGUI.BeginChangeCheck();
                    int pick = EditorGUILayout.Popup(mi, new[] { "Fill", "Fit", "Crop", "Tile" }, GUILayout.MinWidth(54));
                    if (EditorGUI.EndChangeCheck() && change != null && pick != mi)
                    {
                        RecordFields("Image Scale Mode", null, change, field);
                        FigFile.SetEnum(change.Get(field).Items[index], "imageScaleMode", modes[pick]);
                        MarkDirty();
                        InvalidateBoardNode(n.Id);
                    }

                    if (hash != null && GUILayout.Button(new GUIContent("…", "Replace or export in Assets"), EditorStyles.miniButton, GUILayout.Width(20)))
                    {
                        rail = Rail.Assets;
                        imageFocus = FigUtil.Hex(hash);
                        imageFocusScroll = true;
                    }

                    break;
                }
                default:
                {
                    // Gradients: a swatch of the ramp (click to edit) and the gradient type.
                    Rect swatch = GUILayoutUtility.GetRect(36, 20, GUILayout.Width(36));
                    DrawGradientSwatch(swatch, paint);
                    if (Event.current.type == EventType.MouseDown && swatch.Contains(Event.current.mousePosition))
                    {
                        TogglePaintEditor(key);
                        Event.current.Use();
                    }

                    int gi = Math.Max(0, Array.IndexOf(PaintTypes, type));
                    EditorGUI.BeginChangeCheck();
                    int pick = EditorGUILayout.Popup(gi, PaintTypeLabels, GUILayout.MinWidth(54));
                    if (EditorGUI.EndChangeCheck() && change != null && pick != gi)
                    {
                        ConvertPaint(n, field, index, PaintTypes[pick]);
                        changedList = true;
                    }

                    break;
                }
            }

            EditorGUI.BeginChangeCheck();
            float opacity = NumberField("%", Mathf.Round((paint.Get("opacity")?.AsFloat ?? 1f) * 1000f) / 10f, 58, "Paint opacity (%)");
            if (EditorGUI.EndChangeCheck() && change != null)
            {
                RecordFields("Paint Opacity", field + index + "op:" + n.Id, change, field);
                change.Get(field).Items[index].Set("opacity", KiwiValue.FromFloat(Mathf.Clamp01(opacity / 100f)));
                MarkDirty();
                InvalidateBoardNode(n.Id);
            }

            GUI.color = oldColor;
            bool changed = false;
            if (FigTheme.IconButton(GUILayoutUtility.GetRect(22, 20, GUILayout.Width(22)), visible ? "eye" : "eye_off", visible ? "Hide paint" : "Show paint") && change != null)
            {
                RecordFields("Toggle Paint", null, change, field);
                change.Get(field).Items[index].Set("visible", KiwiValue.FromBool(!visible));
                MarkDirty();
                InvalidateBoardNode(n.Id);
            }

            if (FigTheme.IconButton(GUILayoutUtility.GetRect(22, 20, GUILayout.Width(22)), "minus", "Remove") && change != null)
            {
                RecordFields("Remove Paint", null, change, field);
                change.Get(field).Items.RemoveAt(index);
                MarkDirty();
                InvalidateBoardNode(n.Id);
                changed = true;
            }

            EndRow();
            if (open && !changed && !changedList)
            {
                changedList = DrawPaintEditor(n, field, paint, index, key);
            }

            return changed || changedList;
        }

        // ─── Effects ─────────────────────────────────────────────────────────────

        private void DrawEffectsSection(FigNode n, FigView view)
        {
            KiwiValue change = n?.Change;
            List<KiwiValue> effects = view.List("effects");
            if (SectionHeader("Effects", "Add effect") && change != null)
            {
                KiwiField list = change.Def.FieldByName("effects");
                if (list?.TypeDef != null)
                {
                    RecordFields("Add Effect", null, change, "effects");
                    KiwiValue e = KiwiSchema.DefaultElement(list);
                    FigFile.SetEnum(e, "type", "DROP_SHADOW");
                    KiwiValue color = FigFile.Default(e.Def, "color");
                    if (color != null)
                    {
                        color.Set("a", KiwiValue.FromFloat(0.25f));
                        e.Set("color", color);
                    }

                    KiwiValue offset = FigFile.Default(e.Def, "offset");
                    if (offset != null)
                    {
                        offset.Set("y", KiwiValue.FromFloat(4));
                        e.Set("offset", offset);
                    }

                    e.Set("radius", KiwiValue.FromFloat(4));
                    e.Set("visible", KiwiValue.FromBool(true));
                    FigFile.SetEnum(e, "blendMode", "NORMAL");
                    if (change.Get("effects")?.Items is List<KiwiValue> items)
                    {
                        items.Add(e);
                    }
                    else
                    {
                        change.Set("effects", KiwiValue.FromArray(new List<KiwiValue> { e }));
                    }

                    MarkDirty();
                    InvalidateBoardNode(n.Id);
                    GUIUtility.ExitGUI();
                }
            }

            if (effects != null)
            {
                for (int i = 0; i < effects.Count; i++)
                {
                    if (DrawEffectRow(n, effects[i], i))
                    {
                        GUIUtility.ExitGUI();
                    }
                }
            }

            SectionEnd();
        }

        private bool DrawEffectRow(FigNode n, KiwiValue effect, int index)
        {
            KiwiValue change = n?.Change;
            string key = (n?.Id ?? "") + "/fx" + index;
            string type = effect.Get("type")?.EnumName ?? "DROP_SHADOW";
            bool visible = effect.Get("visible")?.AsBool ?? true;
            bool open = openEffects.Contains(key);
            BeginRow();
            if (FigTheme.IconButton(GUILayoutUtility.GetRect(20, 20, GUILayout.Width(20)), open ? "chevron_down" : "chevron_right", "Settings"))
            {
                if (open)
                {
                    openEffects.Remove(key);
                }
                else
                {
                    openEffects.Add(key);
                }
            }

            int ti = Array.IndexOf(EffectTypes, type);
            EditorGUI.BeginChangeCheck();
            int pick = EditorGUILayout.Popup(Math.Max(0, ti), EffectLabels);
            if (EditorGUI.EndChangeCheck() && change != null && pick != ti)
            {
                RecordFields("Effect Type", null, change, "effects");
                FigFile.SetEnum(change.Get("effects").Items[index], "type", EffectTypes[pick]);
                MarkDirty();
                InvalidateBoardNode(n.Id);
            }

            bool changed = false;
            if (FigTheme.IconButton(GUILayoutUtility.GetRect(22, 20, GUILayout.Width(22)), visible ? "eye" : "eye_off", visible ? "Hide effect" : "Show effect") && change != null)
            {
                RecordFields("Toggle Effect", null, change, "effects");
                change.Get("effects").Items[index].Set("visible", KiwiValue.FromBool(!visible));
                MarkDirty();
                InvalidateBoardNode(n.Id);
            }

            if (FigTheme.IconButton(GUILayoutUtility.GetRect(22, 20, GUILayout.Width(22)), "minus", "Remove effect") && change != null)
            {
                RecordFields("Remove Effect", null, change, "effects");
                change.Get("effects").Items.RemoveAt(index);
                MarkDirty();
                InvalidateBoardNode(n.Id);
                changed = true;
            }

            EndRow();
            if (!open || changed)
            {
                return changed;
            }

            bool shadow = type == "DROP_SHADOW" || type == "INNER_SHADOW";
            if (shadow)
            {
                KiwiValue offset = effect.Get("offset");
                BeginRow();
                GUILayout.Space(22);
                EditorGUI.BeginChangeCheck();
                float x = NumberField("X", offset?.Get("x")?.AsFloat ?? 0);
                GUILayout.Space(6);
                float y = NumberField("Y", offset?.Get("y")?.AsFloat ?? 0);
                if (EditorGUI.EndChangeCheck() && change != null)
                {
                    RecordFields("Effect Offset", key + "off", change, "effects");
                    KiwiValue live = change.Get("effects").Items[index];
                    KiwiValue o = live.Get("offset") ?? FigFile.Default(live.Def, "offset");
                    if (o != null)
                    {
                        SetFloat(o, "x", x);
                        SetFloat(o, "y", y);
                        live.Set("offset", o);
                    }

                    MarkDirty();
                    InvalidateBoardNode(n.Id);
                }

                EndRow();
            }

            BeginRow();
            GUILayout.Space(22);
            EditorGUI.BeginChangeCheck();
            float blur = NumberField("◌", effect.Get("radius")?.AsFloat ?? 0, 0, "Blur");
            float spread = 0;
            if (shadow)
            {
                GUILayout.Space(6);
                spread = NumberField("⊕", effect.Get("spread")?.AsFloat ?? 0, 0, "Spread");
            }

            if (EditorGUI.EndChangeCheck() && change != null)
            {
                RecordFields("Effect", key + "blur", change, "effects");
                KiwiValue live = change.Get("effects").Items[index];
                live.Set("radius", KiwiValue.FromFloat(Mathf.Max(0, blur)));
                if (shadow)
                {
                    live.Set("spread", KiwiValue.FromFloat(spread));
                }

                MarkDirty();
                InvalidateBoardNode(n.Id);
            }

            EndRow();
            if (shadow)
            {
                BeginRow();
                GUILayout.Space(22);
                KiwiValue color = effect.Get("color");
                EditorGUI.BeginChangeCheck();
                Color picked = EditorGUILayout.ColorField(GUIContent.none, color != null ? FigPaint.ReadColor(color) : new Color(0, 0, 0, 0.25f), true, true, false);
                if (EditorGUI.EndChangeCheck() && change != null)
                {
                    RecordFields("Effect Colour", key + "color", change, "effects");
                    KiwiValue live = change.Get("effects").Items[index];
                    KiwiValue c = live.Get("color") ?? FigFile.Default(live.Def, "color");
                    if (c != null)
                    {
                        SetFloat(c, "r", picked.r);
                        SetFloat(c, "g", picked.g);
                        SetFloat(c, "b", picked.b);
                        SetFloat(c, "a", picked.a);
                        live.Set("color", c);
                    }

                    MarkDirty();
                    InvalidateBoardNode(n.Id);
                }

                EndRow();
            }

            return false;
        }

        // ─── Export ──────────────────────────────────────────────────────────────

        private void DrawExportSection(FigSceneNode s)
        {
            if (s == null)
            {
                return;
            }

            SectionHeader("Export");
            BeginRow();
            exportScale = EditorGUILayout.Popup(exportScale, Array.ConvertAll(ExportScales, z => Format(z) + "x"), GUILayout.Width(56));
            GUILayout.Label("PNG", FigTheme.Dim, GUILayout.Width(32));
            if (GUILayout.Button("Export " + (string.IsNullOrEmpty(s.Name) ? s.Type.ToLowerInvariant() : s.Name), EditorStyles.miniButton))
            {
                string id = s.Id;
                float zoom = ExportScales[exportScale];
                Defer(() => ExportPng(id, zoom));
            }

            EndRow();
            SectionEnd();
        }

        /// <summary>Render a layer (with its subtree and effects) on transparent and save it as PNG.</summary>
        private void ExportPng(string id, float zoom)
        {
            FigSceneNode n = scene?.Find(id);
            if (n == null)
            {
                return;
            }

            string path = EditorUtility.SaveFilePanel("Export PNG", Path.GetDirectoryName(filePath), SafeFileName(string.IsNullOrEmpty(n.Name) ? "layer" : n.Name), "png");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            Rect b = n.SubtreeBounds;
            int x = Mathf.FloorToInt(b.xMin * zoom), y = Mathf.FloorToInt(b.yMin * zoom);
            int w = Mathf.Clamp(Mathf.CeilToInt(b.xMax * zoom) - x, 1, 8192), h = Mathf.Clamp(Mathf.CeilToInt(b.yMax * zoom) - y, 1, 8192);
            var renderer = new FigRenderer();
            var images = new Dictionary<string, FigImageData>();
            renderer.Images = hash =>
            {
                if (!images.TryGetValue(hash, out FigImageData data))
                {
                    data = DecodeForExport(file.Member(FigFile.ImagePrefix + hash)?.Data);
                    images[hash] = data;
                }

                return data;
            };

            EditorUtility.DisplayProgressBar("Fig Editor", "Rendering " + n.Name + "…", 0.5f);
            try
            {
                Color32[] topDown = renderer.RenderOnly(scene.Nodes, new[] { n.Index }, zoom, x, y, w, h);
                var pixels = new Color32[topDown.Length];
                for (int row = 0; row < h; row++)
                {
                    Array.Copy(topDown, row * w, pixels, (h - 1 - row) * w, w);
                }

                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.SetPixels32(pixels);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
                DestroyImmediate(tex);
                RefreshIfInProject(path);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static FigImageData DecodeForExport(byte[] bytes)
        {
            if (bytes == null)
            {
                return null;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(bytes, false))
                {
                    return null;
                }

                Color32[] bottomUp = tex.GetPixels32();
                var topDown = new Color32[bottomUp.Length];
                for (int y = 0; y < tex.height; y++)
                {
                    Array.Copy(bottomUp, (tex.height - 1 - y) * tex.width, topDown, y * tex.width, tex.width);
                }

                return new FigImageData(tex.width, tex.height, topDown);
            }
            finally
            {
                DestroyImmediate(tex);
            }
        }

        // ─── Prototype ───────────────────────────────────────────────────────────

        private Vector2 prototypeScroll;

        private void DrawPrototypePanel(float width)
        {
            prototypeScroll = EditorGUILayout.BeginScrollView(prototypeScroll);
            FigSceneNode s = SelectedSceneNode;
            FigView view = s?.View ?? (Selected != null ? new FigView(Selected.Change) : null);
            SectionHeader("Interactions");
            List<KiwiValue> interactions = view?.List("prototypeInteractions");
            if (interactions == null || interactions.Count == 0)
            {
                BeginRow();
                GUILayout.Label(view == null ? "Select a layer to see its prototype interactions." : "No interactions on this layer.", FigTheme.Dim);
                EndRow();
            }
            else
            {
                foreach (KiwiValue interaction in interactions)
                {
                    string trigger = Pretty(interaction.Path("event.interactionType")?.EnumName ?? "ON_CLICK");
                    List<KiwiValue> actions = interaction.Get("actions")?.Items;
                    if (actions == null || actions.Count == 0)
                    {
                        BeginRow();
                        GUILayout.Label(trigger + " → (nothing)", FigTheme.Label);
                        EndRow();
                        continue;
                    }

                    foreach (KiwiValue action in actions)
                    {
                        string nav = Pretty(action.Get("navigationType")?.EnumName ?? action.Get("connectionType")?.EnumName ?? "NAVIGATE");
                        string target = FigUtil.GuidString(action.Get("transitionNodeID"));
                        string targetName = file.Find(target)?.Label ?? action.Get("connectionURL")?.String ?? "";
                        string transition = action.Get("transitionType")?.EnumName;
                        BeginRow();
                        GUILayout.Label(trigger, FigTheme.Dim, GUILayout.Width(80));
                        GUILayout.Label(nav + (targetName.Length > 0 ? " → " + targetName : ""), FigTheme.Label);
                        GUILayout.FlexibleSpace();
                        if (file.Find(target) != null && GUILayout.Button("→", EditorStyles.miniButton, GUILayout.Width(22)))
                        {
                            SetBoardSelection(target, false);
                            pendingZoomTo = target;
                        }

                        EndRow();
                        if (transition != null)
                        {
                            BeginRow();
                            GUILayout.Space(80);
                            GUILayout.Label(Pretty(transition), FigTheme.Faint);
                            EndRow();
                        }
                    }
                }
            }

            SectionEnd();

            // Flow starting points: frames on this page that carry one.
            SectionHeader("Flows");
            int flows = 0;
            FigNode page = file.Find(boardPageId);
            if (page != null)
            {
                foreach (FigNode frame in page.Children)
                {
                    KiwiValue start = frame.Change.Get("prototypeStartingPoint");
                    if (start == null)
                    {
                        continue;
                    }

                    flows++;
                    BeginRow();
                    GUILayout.Label("▶ " + (start.Get("name")?.String ?? "Flow") + "  ·  " + frame.Label, FigTheme.Label);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("→", EditorStyles.miniButton, GUILayout.Width(22)))
                    {
                        SetBoardSelection(frame.Id, false);
                        pendingZoomTo = frame.Id;
                    }

                    EndRow();
                }
            }

            if (flows == 0)
            {
                BeginRow();
                GUILayout.Label("No flow starting points on this page.", FigTheme.Dim);
                EndRow();
            }

            SectionEnd();
            EditorGUILayout.EndScrollView();
        }
    }
}
