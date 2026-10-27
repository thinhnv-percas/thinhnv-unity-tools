using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// The Inspect tab's raw editor: every field of a node change (or of the message), editable
    /// through the file's own schema — add / remove fields, grow and shrink arrays, inline
    /// controls for colours, vectors, GUIDs and byte arrays. Also the layer operations shared by
    /// the panels (reorder, duplicate, delete) and the export helpers.
    /// </summary>
    public partial class FigEditorWindow
    {
        private const int ArrayPageSize = 50;
        private const int MaxInlineHex = 64;

        private readonly Dictionary<string, bool> foldouts = new();
        private readonly Dictionary<string, int> arrayPages = new();

        private Vector2 inspectScroll;

        /// <summary>
        /// The Inspect tab: the selected layer's fields exactly as the file stores them (or, for a
        /// layer inside an instance, its main's fields and the override rows applied to it).
        /// </summary>
        private void DrawInspectPanel(float width)
        {
            inspectScroll = EditorGUILayout.BeginScrollView(inspectScroll);
            float labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Min(140, width * 0.45f);
            FigNode n = Selected;
            FigSceneNode s = SelectedSceneNode;
            GUILayout.Space(8);
            if (n != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{n.Type} · {n.Id}", FigTheme.Dim);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("JSON…", EditorStyles.miniButton, GUILayout.Width(52)))
                {
                    Defer(() => ExportJson(n.Change, n.Label));
                }

                GUILayout.EndHorizontal();
                GUILayout.Label($"{n.Change.Members.Count} fields — edits here are not undoable.", FigTheme.Faint);
                GUILayout.Space(4);
                DrawObjectMembers(n.Change, "node/" + n.Id);
            }
            else if (s != null && s.Expanded)
            {
                GUILayout.Label("Inside an instance: the main component's layer, read-only.", FigTheme.Dim);
                GUILayout.Label($"{s.View.Rows?.Count ?? 0} override / derived rows apply to it.", FigTheme.Faint);
                using (new EditorGUI.DisabledScope(true))
                {
                    DrawObjectMembers(s.View.Base, "expanded/" + s.Id);
                    if (s.View.Rows != null)
                    {
                        for (int i = 0; i < s.View.Rows.Count; i++)
                        {
                            GUILayout.Space(6);
                            GUILayout.Label("Row " + (i + 1), FigTheme.Bold);
                            DrawObjectMembers(s.View.Rows[i], "expanded/" + s.Id + "/row" + i);
                        }
                    }
                }
            }
            else
            {
                GUILayout.Label("Message fields (besides nodeChanges and blobs)", FigTheme.Bold);
                foreach (KiwiMember m in new List<KiwiMember>(file.Message.Members))
                {
                    if (m.Field.Name == "nodeChanges" || m.Field.Name == "blobs")
                    {
                        EditorGUILayout.LabelField(m.Field.Name, m.Value.Summary());
                        continue;
                    }

                    DrawField(m.Field, m.Value, "message/" + m.Field.Name, m.Field.Name, null);
                }
            }

            EditorGUIUtility.labelWidth = labelWidth;
            GUILayout.Space(16);
            EditorGUILayout.EndScrollView();
        }

        private static Color ToColor(KiwiValue color)
        {
            return new Color(color.Get("r")?.AsFloat ?? 0, color.Get("g")?.AsFloat ?? 0, color.Get("b")?.AsFloat ?? 0, color.Get("a")?.AsFloat ?? 1);
        }

        private static void SetFloat(KiwiValue obj, string field, float value)
        {
            KiwiValue v = obj.Get(field);
            if (v != null && v.Kind == KiwiValueKind.Float)
            {
                v.FloatValue = value;
            }
            else
            {
                obj.Set(field, KiwiValue.FromFloat(value));
            }
        }

        // ─── Generic schema-driven editor ────────────────────────────────────────

        private void DrawObjectMembers(KiwiValue obj, string path)
        {
            List<KiwiMember> members = obj.Members;
            bool isMessage = obj.Def.Kind == KiwiDefKind.Message;
            int removeAt = -1;

            for (int i = 0; i < members.Count; i++)
            {
                KiwiMember m = members[i];
                string childPath = path + "/" + m.Field.Name;
                bool remove = false;
                DrawField(m.Field, m.Value, childPath, m.Field.Name, isMessage ? (Action)(() => remove = true) : null);
                if (remove)
                {
                    removeAt = i;
                }
            }

            if (removeAt >= 0)
            {
                string removed = members[removeAt].Field.Name;
                members.RemoveAt(removeAt);
                MarkDirty();
                AfterRawEdit(path + "/" + removed);
                GUIUtility.ExitGUI();
            }

            if (isMessage)
            {
                DrawAddFieldButton(obj, path);
            }
        }

        private void DrawAddFieldButton(KiwiValue obj, string path)
        {
            var missing = new List<KiwiField>();
            foreach (KiwiField f in obj.Def.Fields)
            {
                if (obj.IndexOf(f.Name) < 0)
                {
                    missing.Add(f);
                }
            }

            if (missing.Count == 0)
            {
                return;
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 15f);
            if (GUILayout.Button($"+ Add field ({missing.Count})", EditorStyles.miniButton, GUILayout.Width(130)))
            {
                missing.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                var menu = new GenericMenu();
                foreach (KiwiField f in missing)
                {
                    KiwiField field = f;
                    // Menus split on '/', so group by first letter to keep long lists usable.
                    string item = missing.Count > 30 ? char.ToUpperInvariant(f.Name[0]) + "/" + f.Name + "  (" + f.TypeName + ")" : f.Name + "  (" + f.TypeName + ")";
                    menu.AddItem(new GUIContent(item), false, () =>
                    {
                        try
                        {
                            obj.Members.Add(new KiwiMember(field, KiwiSchema.DefaultField(field)));
                            foldouts[path + "/" + field.Name] = true;
                            MarkDirty();
                            AfterRawEdit(path + "/" + field.Name);
                        }
                        catch (Exception e)
                        {
                            EditorUtility.DisplayDialog("Fig Editor", e.Message, "OK");
                        }

                        Repaint();
                    });
                }

                menu.ShowAsContext();
            }

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Draw one field (or array element) value. <paramref name="onRemove"/> adds a remove button.</summary>
        private void DrawField(KiwiField field, KiwiValue value, string path, string label, Action onRemove)
        {
            bool isArray = field.IsArray && value.Kind == KiwiValueKind.Array;
            bool isObject = !field.IsArray && value.Kind == KiwiValueKind.Object;

            if (isArray || isObject)
            {
                EditorGUILayout.BeginHorizontal();
                bool open = foldouts.TryGetValue(path, out bool o) && o;
                string summary = isArray ? $"{field.TypeName}  [{value.Items.Count}]" : value.Summary(60);
                bool newOpen = EditorGUILayout.Foldout(open, new GUIContent(label, field.TypeName), true);
                if (newOpen != open)
                {
                    foldouts[path] = newOpen;
                }

                if (isObject && DrawStructShortcut(value))
                {
                    MarkDirty();
                    AfterRawEdit(path);
                }
                else
                {
                    GUILayout.Label(summary, EditorStyles.miniLabel, GUILayout.MinWidth(40));
                }

                DrawRemoveButton(onRemove);
                EditorGUILayout.EndHorizontal();

                if (newOpen)
                {
                    EditorGUI.indentLevel++;
                    if (isArray)
                    {
                        DrawArrayItems(field, value, path);
                    }
                    else
                    {
                        DrawObjectMembers(value, path);
                    }

                    EditorGUI.indentLevel--;
                }

                return;
            }

            EditorGUILayout.BeginHorizontal();
            if (DrawScalar(field, value, label, path))
            {
                MarkDirty();
                AfterRawEdit(path);
            }

            DrawRemoveButton(onRemove);
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawRemoveButton(Action onRemove)
        {
            if (onRemove != null && GUILayout.Button(new GUIContent("×", "Remove this field"), EditorStyles.miniButton, GUILayout.Width(20)))
            {
                onRemove();
            }
        }

        /// <summary>Inline editors for the small structs that have a natural Unity control. Returns true on change.</summary>
        private bool DrawStructShortcut(KiwiValue value)
        {
            switch (value.Def.Name)
            {
                case "Color":
                {
                    if (value.Get("r") == null || value.Get("a") == null)
                    {
                        return false;
                    }

                    EditorGUI.BeginChangeCheck();
                    Color c = EditorGUILayout.ColorField(GUIContent.none, ToColor(value), true, true, false, GUILayout.MinWidth(60));
                    if (!EditorGUI.EndChangeCheck())
                    {
                        return false;
                    }

                    SetFloat(value, "r", c.r);
                    SetFloat(value, "g", c.g);
                    SetFloat(value, "b", c.b);
                    SetFloat(value, "a", c.a);
                    return true;
                }
                case "Vector":
                {
                    if (value.Get("x") == null || value.Get("y") == null)
                    {
                        return false;
                    }

                    EditorGUI.BeginChangeCheck();
                    int indent = EditorGUI.indentLevel;
                    EditorGUI.indentLevel = 0;
                    Vector2 v = EditorGUILayout.Vector2Field(GUIContent.none, new Vector2(value.Get("x").AsFloat, value.Get("y").AsFloat), GUILayout.MinWidth(80));
                    EditorGUI.indentLevel = indent;
                    if (!EditorGUI.EndChangeCheck())
                    {
                        return false;
                    }

                    SetFloat(value, "x", v.x);
                    SetFloat(value, "y", v.y);
                    return true;
                }
                case "GUID":
                {
                    if (value.Get("sessionID") == null || value.Get("localID") == null)
                    {
                        return false;
                    }

                    string current = FigUtil.GuidString(value);
                    EditorGUI.BeginChangeCheck();
                    int indent = EditorGUI.indentLevel;
                    EditorGUI.indentLevel = 0;
                    string typed = EditorGUILayout.DelayedTextField(current, GUILayout.MinWidth(60));
                    EditorGUI.indentLevel = indent;
                    bool changed = EditorGUI.EndChangeCheck();
                    if (file.Find(current) != null && GUILayout.Button(new GUIContent("→", "Select this layer"), EditorStyles.miniButton, GUILayout.Width(22)))
                    {
                        Defer(() => Select(current));
                    }

                    if (!changed)
                    {
                        return false;
                    }

                    string[] parts = typed.Split(':');
                    if (parts.Length != 2 || !uint.TryParse(parts[0], out uint session) || !uint.TryParse(parts[1], out uint local))
                    {
                        return false;
                    }

                    value.Get("sessionID").Number = session;
                    value.Get("localID").Number = local;
                    return true;
                }
                default:
                    return false;
            }
        }

        private void DrawArrayItems(KiwiField field, KiwiValue value, string path)
        {
            List<KiwiValue> items = value.Items;
            int pages = Mathf.Max(1, (items.Count + ArrayPageSize - 1) / ArrayPageSize);
            int page = arrayPages.TryGetValue(path, out int p) ? Mathf.Clamp(p, 0, pages - 1) : 0;
            int start = page * ArrayPageSize;
            int end = Mathf.Min(items.Count, start + ArrayPageSize);
            int removeAt = -1;

            for (int i = start; i < end; i++)
            {
                int index = i;
                bool remove = false;
                DrawField(ElementField(field), items[i], path + "/" + index, "[" + index + "]", () => remove = true);
                if (remove)
                {
                    removeAt = index;
                }
            }

            if (removeAt >= 0)
            {
                items.RemoveAt(removeAt);
                MarkDirty();
                AfterRawEdit(path);
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 15f);
            if (GUILayout.Button(new GUIContent("+", "Add an element (a copy of the last one, or a default value)"), EditorStyles.miniButton, GUILayout.Width(24)))
            {
                try
                {
                    items.Add(items.Count > 0 ? items[items.Count - 1].Clone() : KiwiSchema.DefaultElement(field));
                    arrayPages[path] = (items.Count - 1) / ArrayPageSize;
                    MarkDirty();
                    AfterRawEdit(path);
                }
                catch (Exception e)
                {
                    EditorUtility.DisplayDialog("Fig Editor", e.Message, "OK");
                }
            }

            if (pages > 1)
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("◀", EditorStyles.miniButtonLeft, GUILayout.Width(24)))
                {
                    arrayPages[path] = Mathf.Max(0, page - 1);
                }

                GUILayout.Label($"{start}–{end - 1} of {items.Count}", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                if (GUILayout.Button("▶", EditorStyles.miniButtonRight, GUILayout.Width(24)))
                {
                    arrayPages[path] = Mathf.Min(pages - 1, page + 1);
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private readonly Dictionary<KiwiField, KiwiField> elementFields = new();

        /// <summary>A non-array view of an array field, so its elements draw like single values.</summary>
        private KiwiField ElementField(KiwiField field)
        {
            if (!elementFields.TryGetValue(field, out KiwiField element))
            {
                element = new KiwiField { Name = field.Name, Type = field.Type, TypeDef = field.TypeDef, IsArray = false, Value = field.Value };
                elementFields[field] = element;
            }

            return element;
        }

        /// <summary>Draws a scalar control; returns true when the value changed.</summary>
        private bool DrawScalar(KiwiField field, KiwiValue value, string label, string path)
        {
            var content = new GUIContent(label, field.TypeName);
            switch (value.Kind)
            {
                case KiwiValueKind.Bool:
                {
                    EditorGUI.BeginChangeCheck();
                    bool b = EditorGUILayout.Toggle(content, value.AsBool);
                    if (!EditorGUI.EndChangeCheck())
                    {
                        return false;
                    }

                    value.Number = b ? 1 : 0;
                    return true;
                }
                case KiwiValueKind.Int:
                case KiwiValueKind.Uint:
                {
                    bool isBlob = field.Name.EndsWith("Blob", StringComparison.Ordinal) && value.Kind == KiwiValueKind.Uint;
                    EditorGUI.BeginChangeCheck();
                    long n = value.Kind == KiwiValueKind.Uint && field.Type == KiwiType.Uint64
                        ? (long)Math.Min(value.Uint, long.MaxValue)
                        : value.Number;
                    long typed = EditorGUILayout.LongField(content, n);
                    bool changed = EditorGUI.EndChangeCheck();
                    if (isBlob)
                    {
                        DrawBlobButtons((int)Math.Min(value.Uint, int.MaxValue), field.Name);
                    }

                    if (!changed)
                    {
                        return false;
                    }

                    value.Number = ClampForType(field.Type, typed);
                    return true;
                }
                case KiwiValueKind.Float:
                {
                    EditorGUI.BeginChangeCheck();
                    float f = EditorGUILayout.FloatField(content, value.FloatValue);
                    if (!EditorGUI.EndChangeCheck())
                    {
                        return false;
                    }

                    value.FloatValue = f;
                    return true;
                }
                case KiwiValueKind.String:
                {
                    string s = value.String ?? "";
                    EditorGUI.BeginChangeCheck();
                    string typed;
                    if (s.Length > 60 || s.IndexOf('\n') >= 0)
                    {
                        EditorGUILayout.BeginVertical();
                        EditorGUILayout.LabelField(content);
                        typed = EditorGUILayout.TextArea(s, GUILayout.MinHeight(36));
                        EditorGUILayout.EndVertical();
                    }
                    else
                    {
                        typed = EditorGUILayout.DelayedTextField(content, s);
                    }

                    if (!EditorGUI.EndChangeCheck())
                    {
                        return false;
                    }

                    value.String = typed;
                    return true;
                }
                case KiwiValueKind.Enum:
                {
                    List<KiwiField> options = value.Def.Fields;
                    var names = new GUIContent[options.Count + 1];
                    int selected = options.Count;
                    for (int i = 0; i < options.Count; i++)
                    {
                        names[i] = new GUIContent(options[i].Name);
                        if (options[i].Value == value.EnumValue)
                        {
                            selected = i;
                        }
                    }

                    // The last entry shows a raw value the schema has no name for.
                    names[options.Count] = new GUIContent(selected == options.Count ? "#" + value.EnumValue : " ");
                    EditorGUI.BeginChangeCheck();
                    int picked = EditorGUILayout.Popup(content, selected, names);
                    if (!EditorGUI.EndChangeCheck() || picked >= options.Count)
                    {
                        return false;
                    }

                    value.Number = options[picked].Value;
                    return true;
                }
                case KiwiValueKind.Bytes:
                    return DrawBytes(content, value, path);
                default:
                    EditorGUILayout.LabelField(content, new GUIContent(value.Summary()));
                    return false;
            }
        }

        private static long ClampForType(KiwiType type, long v)
        {
            switch (type)
            {
                case KiwiType.Byte:
                    return Math.Max(0, Math.Min(255, v));
                case KiwiType.Int:
                    return Math.Max(int.MinValue, Math.Min(int.MaxValue, v));
                case KiwiType.Uint:
                    return Math.Max(0, Math.Min(uint.MaxValue, v));
                case KiwiType.Uint64:
                    return Math.Max(0, v);
                default:
                    return v;
            }
        }

        private bool DrawBytes(GUIContent content, KiwiValue value, string path)
        {
            byte[] bytes = value.Bytes;
            EditorGUILayout.PrefixLabel(content);
            bool changed = false;
            if (bytes.Length <= MaxInlineHex)
            {
                EditorGUI.BeginChangeCheck();
                int indent = EditorGUI.indentLevel;
                EditorGUI.indentLevel = 0;
                string typed = EditorGUILayout.DelayedTextField(FigUtil.Hex(bytes));
                EditorGUI.indentLevel = indent;
                if (EditorGUI.EndChangeCheck())
                {
                    byte[] parsed = FigUtil.FromHex(typed.Trim());
                    if (parsed != null)
                    {
                        value.Ref = parsed;
                        changed = true;
                    }
                }
            }
            else
            {
                GUILayout.Label(FigUtil.FormatSize(bytes.Length), EditorStyles.miniLabel);
            }

            if (GUILayout.Button("Export", EditorStyles.miniButtonLeft, GUILayout.Width(50)))
            {
                string name = path.Substring(path.LastIndexOf('/') + 1);
                Defer(() => ExportBytes(bytes, name + ".bin"));
            }

            if (GUILayout.Button("Import", EditorStyles.miniButtonRight, GUILayout.Width(50)))
            {
                Defer(() =>
                {
                    string source = EditorUtility.OpenFilePanel("Import bytes", "", "");
                    if (!string.IsNullOrEmpty(source))
                    {
                        value.Ref = File.ReadAllBytes(source);
                        MarkDirty();
                        AfterRawEdit(path);
                    }
                });
            }

            return changed;
        }

        private void DrawBlobButtons(int index, string fieldName)
        {
            List<KiwiValue> blobs = file.Message.Get("blobs")?.Items;
            byte[] bytes = blobs != null && index >= 0 && index < blobs.Count ? blobs[index].Get("bytes")?.Bytes : null;
            if (bytes == null)
            {
                return;
            }

            GUILayout.Label(FigUtil.FormatSize(bytes.Length), EditorStyles.miniLabel, GUILayout.Width(60));
            if (GUILayout.Button(new GUIContent("Export", "Export the blob this field points at"), EditorStyles.miniButton, GUILayout.Width(50)))
            {
                Defer(() => ExportBytes(bytes, fieldName + "-" + index + ".bin"));
            }
        }

        /// <summary>Edits under these fields change the tree, so the index is rebuilt.</summary>
        private void AfterRawEdit(string path)
        {
            if (path.Contains("/parentIndex") || path.Contains("/guid") || path.Contains("/phase") || path.Contains("/name") || path.Contains("/type"))
            {
                string keep = selectedId;
                Defer(() =>
                {
                    file.RebuildIndex();
                    OnStructureChanged();
                    if (file.Find(keep) == null)
                    {
                        selectedId = null;
                    }
                });
            }

            // Raw edits don't go through undo; they may touch geometry, so the layer re-reads fully.
            InvalidateBoardNode(selectedId);
        }

        // ─── Layer operations ────────────────────────────────────────────────────

        private void MoveNode(FigNode node, int delta)
        {
            FigNode current = file.Find(node.Id);
            List<FigNode> siblings = current?.Parent?.Children ?? file.Roots;
            int index = current != null ? siblings.IndexOf(current) : -1;
            int other = index + delta;
            if (index < 0 || other < 0 || other >= siblings.Count)
            {
                return;
            }

            if (BeginUndo(delta < 0 ? "Move Up" : "Move Down", null))
            {
                undo.Capture(current.Change, "parentIndex");
                undo.Capture(siblings[other].Change, "parentIndex");
                CommitUndo(delta < 0 ? "Move Up" : "Move Down");
            }

            if (file.MoveAmongSiblings(current, delta))
            {
                MarkDirty();
                OnStructureChanged();
            }
        }

        private void DuplicateNode(FigNode node)
        {
            FigNode current = file.Find(node.Id);
            if (current == null)
            {
                return;
            }

            if (current.Parent == null)
            {
                EditorUtility.DisplayDialog("Fig Editor", "The document root can't be duplicated.", "OK");
                return;
            }

            RecordStructure("Duplicate " + current.Label);
            FigNode copy = file.Duplicate(current);
            MarkDirty();
            OnStructureChanged();
            if (copy != null)
            {
                Select(copy.Id);
            }
        }

        private void DeleteNode(FigNode node)
        {
            FigNode current = file.Find(node.Id);
            if (current == null)
            {
                return;
            }

            if (current.Parent == null)
            {
                EditorUtility.DisplayDialog("Fig Editor", "The document root can't be deleted.", "OK");
                return;
            }

            // Undo brings it back, so only deleting a component (which orphans its instances) asks.
            if (current.Type == "SYMBOL" &&
                !EditorUtility.DisplayDialog("Fig Editor", $"Delete the component \"{current.Label}\"? Its instances will lose their main component.", "Delete", "Cancel"))
            {
                return;
            }

            RecordStructure("Delete " + current.Label);
            string parent = current.Parent.Id;
            file.Delete(current);
            MarkDirty();
            OnStructureChanged();
            Select(parent);
        }

        // ─── Export helpers ──────────────────────────────────────────────────────

        private void ExportJson(KiwiValue value, string name)
        {
            string path = EditorUtility.SaveFilePanel("Export JSON", Path.GetDirectoryName(filePath), SafeFileName(name), "json");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            File.WriteAllText(path, file.ToJson(value));
            RefreshIfInProject(path);
        }

        private void ExportBytes(byte[] bytes, string name)
        {
            string ext = Path.GetExtension(name).TrimStart('.');
            string path = EditorUtility.SaveFilePanel("Export", Path.GetDirectoryName(filePath), SafeFileName(Path.GetFileNameWithoutExtension(name)), ext);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            File.WriteAllBytes(path, bytes);
            RefreshIfInProject(path);
        }

        private static string SafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(name) ? "export" : name;
        }
    }
}
