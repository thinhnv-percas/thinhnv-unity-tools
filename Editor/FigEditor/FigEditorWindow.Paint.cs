using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// The paint editor under a Fill / Stroke row, like Figma's colour picker: the paint type
    /// (solid or a gradient), and for gradients the ramp with draggable stops, each stop's
    /// position and colour, adding and removing stops, flipping and turning the gradient.
    /// </summary>
    public partial class FigEditorWindow
    {
        private static readonly string[] PaintTypes = { "GRADIENT_LINEAR", "GRADIENT_RADIAL", "GRADIENT_ANGULAR", "GRADIENT_DIAMOND" };
        private static readonly string[] PaintTypeLabels = { "Linear", "Radial", "Angular", "Diamond" };
        private static readonly string[] EditorTypes = { "SOLID", "GRADIENT_LINEAR", "GRADIENT_RADIAL", "GRADIENT_ANGULAR", "GRADIENT_DIAMOND" };
        private static readonly string[] EditorTypeLabels = { "Solid", "Linear", "Radial", "Angular", "Diamond" };
        private const int MaxPreviews = 64;

        private readonly Dictionary<string, Texture2D> gradientPreviews = new();
        private Texture2D checkerTexture;
        private string stopDragKey;
        private int stopDragIndex = -1;

        private void TogglePaintEditor(string key)
        {
            if (!openPaints.Remove(key))
            {
                openPaints.Add(key);
            }

            GUI.FocusControl(null);
            Repaint();
        }

        /// <summary>The editor under an open paint row. Returns true when the paint list changed shape.</summary>
        private bool DrawPaintEditor(FigNode n, string field, KiwiValue paint, int index, string key)
        {
            KiwiValue change = n?.Change;
            string type = paint.Get("type")?.EnumName ?? "SOLID";
            BeginRow();
            GUILayout.Space(20);
            int ti = Math.Max(0, Array.IndexOf(EditorTypes, type));
            EditorGUI.BeginChangeCheck();
            int pick = GUILayout.Toolbar(ti, EditorTypeLabels, EditorStyles.miniButton);
            bool converted = false;
            if (EditorGUI.EndChangeCheck() && change != null && pick != ti)
            {
                ConvertPaint(n, field, index, EditorTypes[pick]);
                converted = true;
            }

            EndRow();
            if (converted || type == "SOLID")
            {
                return converted;
            }

            List<KiwiValue> stops = paint.Get("stops")?.Items;
            if (stops == null || stops.Count == 0)
            {
                return false;
            }

            // The ramp with its stop handles: drag a handle to move the stop, click the ramp to add one.
            BeginRow();
            GUILayout.Space(20);
            Rect bar = GUILayoutUtility.GetRect(60, 34, GUILayout.ExpandWidth(true));
            EndRow();
            var ramp = new Rect(bar.x + 6, bar.y + 2, bar.width - 12, 18);
            DrawChecker(ramp);
            Texture2D preview = GradientPreview(paint, false);
            if (preview != null && Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(ramp, preview, ScaleMode.StretchToFill, true);
            }

            Event e = Event.current;
            for (int si = 0; si < stops.Count; si++)
            {
                float position = Mathf.Clamp01(stops[si].Get("position")?.AsFloat ?? 0f);
                var handle = new Rect(ramp.x + ramp.width * position - 5, ramp.yMax + 1, 10, 11);
                if (e.type == EventType.Repaint)
                {
                    bool active = stopDragKey == key && stopDragIndex == si;
                    EditorGUI.DrawRect(handle, active ? FigTheme.Accent : FigTheme.Text);
                    Color c = StopColor(stops[si]);
                    c.a = 1f;
                    EditorGUI.DrawRect(new Rect(handle.x + 2, handle.y + 2, handle.width - 4, handle.height - 4), c);
                    EditorGUI.DrawRect(new Rect(handle.center.x - 0.5f, ramp.y, 1, ramp.height), new Color(1, 1, 1, 0.5f));
                }

                if (e.type == EventType.MouseDown && e.button == 0 && handle.Contains(e.mousePosition) && change != null)
                {
                    stopDragKey = key;
                    stopDragIndex = si;
                    e.Use();
                }
            }

            if (stopDragKey == key && stopDragIndex >= 0 && stopDragIndex < stops.Count && change != null)
            {
                if (e.type == EventType.MouseDrag)
                {
                    float position = Mathf.Clamp01((e.mousePosition.x - ramp.x) / Mathf.Max(1f, ramp.width));
                    RecordFields("Move Gradient Stop", key + "stopdrag", change, field);
                    KiwiValue live = LiveStop(change, field, index, stopDragIndex);
                    if (live != null)
                    {
                        live.Set("position", KiwiValue.FromFloat(position));
                        AfterStopEdit(n, field, index);
                    }

                    e.Use();
                }
                else if (e.type == EventType.MouseUp)
                {
                    stopDragIndex = SortStops(change, field, index, stopDragIndex);
                    stopDragKey = null;
                    stopDragIndex = -1;
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseDown && e.button == 0 && ramp.Contains(e.mousePosition) && change != null)
            {
                float position = Mathf.Clamp01((e.mousePosition.x - ramp.x) / Mathf.Max(1f, ramp.width));
                AddStop(n, field, index, position);
                e.Use();
                return true;
            }

            // One row per stop: position, colour (with alpha), hex, remove.
            for (int si = 0; si < stops.Count; si++)
            {
                BeginRow();
                GUILayout.Space(20);
                float position = (stops[si].Get("position")?.AsFloat ?? 0f) * 100f;
                EditorGUI.BeginChangeCheck();
                float typedPosition = EditorGUILayout.DelayedFloatField(Mathf.Round(position * 10f) / 10f, GUILayout.Width(40));
                GUILayout.Label("%", FigTheme.Dim, GUILayout.Width(14));
                if (EditorGUI.EndChangeCheck() && change != null)
                {
                    RecordFields("Gradient Stop Position", null, change, field);
                    LiveStop(change, field, index, si)?.Set("position", KiwiValue.FromFloat(Mathf.Clamp01(typedPosition / 100f)));
                    SortStops(change, field, index, si);
                    AfterStopEdit(n, field, index);
                    GUIUtility.ExitGUI();
                }

                Color color = StopColor(stops[si]);
                EditorGUI.BeginChangeCheck();
                Color picked = EditorGUILayout.ColorField(GUIContent.none, color, true, true, false, GUILayout.Width(40));
                string hex = ColorUtility.ToHtmlStringRGB(color);
                string typed = EditorGUILayout.DelayedTextField(hex, GUILayout.MinWidth(54));
                if (EditorGUI.EndChangeCheck() && change != null)
                {
                    if (typed != hex && ColorUtility.TryParseHtmlString("#" + typed.TrimStart('#'), out Color parsed))
                    {
                        picked = new Color(parsed.r, parsed.g, parsed.b, color.a);
                    }

                    RecordFields("Gradient Stop Colour", key + "stop" + si, change, field);
                    KiwiValue live = LiveStop(change, field, index, si)?.Get("color");
                    if (live != null)
                    {
                        SetFloat(live, "r", picked.r);
                        SetFloat(live, "g", picked.g);
                        SetFloat(live, "b", picked.b);
                        SetFloat(live, "a", picked.a);
                    }

                    AfterStopEdit(n, field, index);
                }

                GUILayout.Label(Mathf.RoundToInt(color.a * 100f) + "%", FigTheme.Dim, GUILayout.Width(34));
                using (new EditorGUI.DisabledScope(stops.Count <= 2))
                {
                    if (FigTheme.IconButton(GUILayoutUtility.GetRect(22, 20, GUILayout.Width(22)), "minus", "Remove stop") && change != null && stops.Count > 2)
                    {
                        RecordFields("Remove Gradient Stop", null, change, field);
                        change.Get(field).Items[index].Get("stops").Items.RemoveAt(si);
                        AfterStopEdit(n, field, index);
                        EndRow();
                        return true;
                    }
                }

                EndRow();
            }

            BeginRow();
            GUILayout.Space(20);
            if (GUILayout.Button(new GUIContent("+ Stop", "Add a stop in the middle of the widest gap"), EditorStyles.miniButtonLeft) && change != null)
            {
                AddStop(n, field, index, WidestGap(stops));
                EndRow();
                return true;
            }

            if (GUILayout.Button(new GUIContent("Flip", "Reverse the stops"), EditorStyles.miniButtonMid) && change != null)
            {
                RecordFields("Flip Gradient", null, change, field);
                foreach (KiwiValue stop in change.Get(field).Items[index].Get("stops").Items)
                {
                    stop.Set("position", KiwiValue.FromFloat(1f - (stop.Get("position")?.AsFloat ?? 0f)));
                }

                SortStops(change, field, index, -1);
                AfterStopEdit(n, field, index);
            }

            if (GUILayout.Button(new GUIContent("Rotate 90°", "Turn the gradient a quarter turn"), EditorStyles.miniButtonRight) && change != null)
            {
                RecordFields("Rotate Gradient", null, change, field);
                RotateGradient(change.Get(field).Items[index]);
                AfterStopEdit(n, field, index);
            }

            EndRow();
            GUILayout.Space(4);
            return false;
        }

        // ─── Stops ───────────────────────────────────────────────────────────────

        private static Color StopColor(KiwiValue stop)
        {
            KiwiValue c = stop.Get("color");
            return c != null ? FigPaint.ReadColor(c) : Color.black;
        }

        private static KiwiValue LiveStop(KiwiValue change, string field, int index, int stop)
        {
            List<KiwiValue> stops = change.Get(field)?.Items?[index]?.Get("stops")?.Items;
            return stops != null && stop >= 0 && stop < stops.Count ? stops[stop] : null;
        }

        /// <summary>Keep the stops in position order; returns where stop <paramref name="follow"/> ended up.</summary>
        private static int SortStops(KiwiValue change, string field, int index, int follow)
        {
            List<KiwiValue> stops = change.Get(field)?.Items?[index]?.Get("stops")?.Items;
            if (stops == null)
            {
                return follow;
            }

            KiwiValue followed = follow >= 0 && follow < stops.Count ? stops[follow] : null;
            var sorted = new List<KiwiValue>(stops);
            // Stable: equal positions keep their order.
            var order = new List<int>();
            for (int i = 0; i < sorted.Count; i++)
            {
                order.Add(i);
            }

            order.Sort((a, b) =>
            {
                int c = (sorted[a].Get("position")?.AsFloat ?? 0f).CompareTo(sorted[b].Get("position")?.AsFloat ?? 0f);
                return c != 0 ? c : a.CompareTo(b);
            });
            stops.Clear();
            foreach (int i in order)
            {
                stops.Add(sorted[i]);
            }

            return followed != null ? stops.IndexOf(followed) : follow;
        }

        private void AddStop(FigNode n, string field, int index, float position)
        {
            KiwiValue change = n.Change;
            KiwiValue live = change.Get(field)?.Items?[index];
            List<KiwiValue> stops = live?.Get("stops")?.Items;
            if (stops == null || stops.Count == 0)
            {
                return;
            }

            RecordFields("Add Gradient Stop", null, change, field);
            KiwiValue stop = stops[0].Clone();
            Color c = SampleStops(stops, position);
            KiwiValue color = stop.Get("color");
            if (color != null)
            {
                SetFloat(color, "r", c.r);
                SetFloat(color, "g", c.g);
                SetFloat(color, "b", c.b);
                SetFloat(color, "a", c.a);
            }

            stop.Set("position", KiwiValue.FromFloat(position));
            stops.Add(stop);
            SortStops(change, field, index, -1);
            AfterStopEdit(n, field, index);
        }

        /// <summary>The straight colour of the ramp at <paramref name="t"/>.</summary>
        private static Color SampleStops(List<KiwiValue> stops, float t)
        {
            KiwiValue before = null, after = null;
            foreach (KiwiValue s in stops)
            {
                float p = s.Get("position")?.AsFloat ?? 0f;
                if (p <= t && (before == null || p >= (before.Get("position")?.AsFloat ?? 0f)))
                {
                    before = s;
                }

                if (p >= t && (after == null || p <= (after.Get("position")?.AsFloat ?? 0f)))
                {
                    after = s;
                }
            }

            if (before == null)
            {
                return StopColor(after);
            }

            if (after == null)
            {
                return StopColor(before);
            }

            float p0 = before.Get("position")?.AsFloat ?? 0f, p1 = after.Get("position")?.AsFloat ?? 0f;
            float k = p1 - p0 > 1e-6f ? (t - p0) / (p1 - p0) : 0f;
            return Color.Lerp(StopColor(before), StopColor(after), k);
        }

        private static float WidestGap(List<KiwiValue> stops)
        {
            var positions = new List<float>();
            foreach (KiwiValue s in stops)
            {
                positions.Add(Mathf.Clamp01(s.Get("position")?.AsFloat ?? 0f));
            }

            positions.Sort();
            float best = 0.5f, width = -1f, prev = 0f;
            positions.Add(1f);
            foreach (float p in positions)
            {
                if (p - prev > width)
                {
                    width = p - prev;
                    best = (p + prev) * 0.5f;
                }

                prev = p;
            }

            return best;
        }

        /// <summary>Gradient stops edited: drop the variable-bound copy so the plain stops are what Figma reads.</summary>
        private void AfterStopEdit(FigNode n, string field, int index)
        {
            n.Change.Get(field)?.Items?[index]?.Remove("stopsVar");
            MarkDirty();
            InvalidateBoardNode(n.Id);
            Repaint();
        }

        /// <summary>A quarter turn of the gradient about the layer's centre.</summary>
        private static void RotateGradient(KiwiValue paint)
        {
            KiwiValue t = paint.Get("transform");
            if (t == null)
            {
                return;
            }

            FigAffine m = FigAffine.FromMatrix(t);
            // u' = R(u − c) + c with R a quarter turn, applied before the paint transform.
            var turn = new FigAffine { A = 0, B = -1, C = 1, D = 1, E = 0, F = 0 };
            FigAffine r = m.Then(turn);
            SetMatrix(t, r.A, r.B, r.C, r.D, r.E, r.F);
        }

        /// <summary>Change a paint between solid and the gradient kinds, keeping its colours.</summary>
        private void ConvertPaint(FigNode n, string field, int index, string toType)
        {
            KiwiValue change = n.Change;
            KiwiValue live = change.Get(field)?.Items?[index];
            if (live == null)
            {
                return;
            }

            RecordFields("Paint Type", null, change, field);
            string from = live.Get("type")?.EnumName ?? "SOLID";
            if (toType == "SOLID")
            {
                List<KiwiValue> stops = live.Get("stops")?.Items;
                Color c = stops != null && stops.Count > 0 ? StopColor(stops[0]) : Color.grey;
                KiwiValue color = live.Get("color") ?? FigFile.Default(live.Def, "color");
                if (color != null)
                {
                    SetFloat(color, "r", c.r);
                    SetFloat(color, "g", c.g);
                    SetFloat(color, "b", c.b);
                    SetFloat(color, "a", 1f);
                    live.Set("color", color);
                }

                FigFile.SetEnum(live, "type", "SOLID");
            }
            else
            {
                if (from == "SOLID" || live.Get("stops")?.Items == null || live.Get("stops").Items.Count == 0)
                {
                    // A solid becomes its colour fading out, left to right.
                    KiwiValue sc = live.Get("color");
                    Color c = sc != null ? FigPaint.ReadColor(sc) : Color.black;
                    KiwiField stopsField = live.Def.FieldByName("stops");
                    if (stopsField == null)
                    {
                        return;
                    }

                    var list = new List<KiwiValue>();
                    for (int i = 0; i < 2; i++)
                    {
                        KiwiValue stop = KiwiSchema.DefaultElement(stopsField);
                        KiwiValue color = stop.Get("color") ?? FigFile.Default(stop.Def, "color");
                        if (color != null)
                        {
                            SetFloat(color, "r", c.r);
                            SetFloat(color, "g", c.g);
                            SetFloat(color, "b", c.b);
                            SetFloat(color, "a", i == 0 ? 1f : 0f);
                            stop.Set("color", color);
                        }

                        stop.Set("position", KiwiValue.FromFloat(i));
                        list.Add(stop);
                    }

                    live.Set("stops", KiwiValue.FromArray(list));
                    live.Remove("stopsVar");
                    KiwiValue t = live.Get("transform") ?? FigFile.Default(live.Def, "transform");
                    if (t != null)
                    {
                        SetMatrix(t, 1, 0, 0, 0, 1, 0);
                        live.Set("transform", t);
                    }
                }

                FigFile.SetEnum(live, "type", toType);
            }

            MarkDirty();
            InvalidateBoardNode(n.Id);
            GUIUtility.ExitGUI();
        }

        // ─── Previews ────────────────────────────────────────────────────────────

        private void DrawGradientSwatch(Rect r, KiwiValue paint)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            DrawChecker(r);
            Texture2D preview = GradientPreview(paint, true);
            if (preview != null)
            {
                GUI.DrawTexture(r, preview, ScaleMode.StretchToFill, true);
            }

            FigTheme.Outline(r, FigTheme.Border);
        }

        private void DrawChecker(Rect r)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            if (checkerTexture == null)
            {
                checkerTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Repeat,
                };
                var light = new Color32(255, 255, 255, 255);
                var dark = new Color32(204, 204, 204, 255);
                checkerTexture.SetPixels32(new[] { light, dark, dark, light });
                checkerTexture.Apply(false, false);
            }

            GUI.DrawTextureWithTexCoords(r, checkerTexture, new Rect(0, 0, r.width / 10f, r.height / 10f), false);
        }

        /// <summary>
        /// A small texture of the paint's ramp (cached by its stops): <paramref name="shaped"/> draws
        /// the gradient's own shape for a swatch, otherwise the plain ramp.
        /// </summary>
        private Texture2D GradientPreview(KiwiValue paint, bool shaped)
        {
            FigPaint p = FigPaint.Read(paint, 1f);
            if (p == null || p.Lut == null)
            {
                return null;
            }

            var sb = new StringBuilder(shaped ? "s" : "r");
            sb.Append((int)p.Kind);
            for (int i = 0; i < 256; i += 15)
            {
                sb.Append(',').Append(p.Lut[i * 4 + 3].ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(p.Lut[i * 4].ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(p.Lut[i * 4 + 1].ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(p.Lut[i * 4 + 2].ToString("0.###", CultureInfo.InvariantCulture));
            }

            string key = sb.ToString();
            if (gradientPreviews.TryGetValue(key, out Texture2D cached) && cached != null)
            {
                return cached;
            }

            if (gradientPreviews.Count >= MaxPreviews)
            {
                ClearGradientPreviews();
            }

            int w = shaped ? 36 : 128, h = shaped ? 20 : 1;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w, v = 1f - (y + 0.5f) / h;
                    float t = u;
                    if (shaped && p.Kind != FigPaintKind.LinearGradient)
                    {
                        float dx = u - 0.5f, dy = v - 0.5f;
                        t = p.Kind == FigPaintKind.RadialGradient ? Mathf.Sqrt(dx * dx + dy * dy) * 2f :
                            p.Kind == FigPaintKind.AngularGradient ? Mathf.Repeat(Mathf.Atan2(dy, dx) / (2f * Mathf.PI), 1f) :
                            (Mathf.Abs(dx) + Mathf.Abs(dy)) * 2f;
                    }

                    int li = Mathf.Clamp((int)(t * 255f + 0.5f), 0, 255) * 4;
                    float a = p.Lut[li + 3];
                    pixels[y * w + x] = a <= 0
                        ? new Color32(0, 0, 0, 0)
                        : new Color32(Byte(p.Lut[li] / a), Byte(p.Lut[li + 1] / a), Byte(p.Lut[li + 2] / a), Byte(a));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            gradientPreviews[key] = tex;
            return tex;
        }

        private static byte Byte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        private void ClearGradientPreviews()
        {
            foreach (Texture2D t in gradientPreviews.Values)
            {
                if (t != null)
                {
                    DestroyImmediate(t);
                }
            }

            gradientPreviews.Clear();
            if (checkerTexture != null)
            {
                DestroyImmediate(checkerTexture);
                checkerTexture = null;
            }
        }
    }
}
