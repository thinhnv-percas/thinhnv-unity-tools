using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// The Layers panel: the file's pages, then the current page's layer tree (instances list the
    /// layers of their main component, read-only, like Figma), with search, eye / lock toggles,
    /// inline rename (double-click), keyboard navigation and a context menu.
    /// </summary>
    public partial class FigEditorWindow
    {
        private const float RowHeight = 28f;
        private const float Indent = 16f;
        private const int MaxSearchResults = 5000;

        [SerializeField] private string search = "";
        [SerializeField] private bool pagesOpen = true;

        private HashSet<string> expanded = new();
        private readonly List<LayerRow> rows = new();
        private bool rowsDirty = true;
        private bool scrollToSelection;
        private Vector2 treeScroll;
        private string rowsSearch;
        private string rowsPage;
        private bool searchOpen;
        private string renamingId;
        private string renameText;

        private struct LayerRow
        {
            public string Id;
            public int Depth;
            public string Label;
            public string Type;
            public string Icon;
            public bool HasChildren;
            public bool Open;
            public bool Hidden;
            public bool Locked;
            public bool InInstance;
            public bool Component;
            public FigNode Node;
        }

        private void DrawLayersPanel(Rect r)
        {
            List<FigNode> pages = Pages();
            float y = r.y;

            // Pages.
            var pagesHeader = new Rect(r.x, y, r.width, 32);
            if (FigTheme.IconButton(new Rect(pagesHeader.x + 6, pagesHeader.y + 7, 18, 18), pagesOpen ? "chevron_down" : "chevron_right", null, false, FigTheme.TextDim))
            {
                pagesOpen = !pagesOpen;
            }

            GUI.Label(new Rect(pagesHeader.x + 26, pagesHeader.y, 100, 32), "Pages", FigTheme.Bold);
            y += 32;
            if (pagesOpen)
            {
                float listHeight = Mathf.Min(pages.Count * 28f, 168f);
                var listRect = new Rect(r.x, y, r.width, listHeight);
                var content = new Rect(0, 0, r.width - (pages.Count * 28f > listHeight ? 14 : 0), pages.Count * 28f);
                pagesScroll = GUI.BeginScrollView(listRect, pagesScroll, content);
                for (int i = 0; i < pages.Count; i++)
                {
                    var row = new Rect(8, i * 28, content.width - 16, 26);
                    bool current = pages[i].Id == boardPageId;
                    if (Event.current.type == EventType.Repaint && (current || row.Contains(Event.current.mousePosition)))
                    {
                        EditorGUI.DrawRect(row, current ? FigTheme.Hover : new Color(1, 1, 1, 0.03f));
                    }

                    GUI.Label(new Rect(row.x + 12, row.y, row.width - 12, row.height), string.IsNullOrEmpty(pages[i].Name) ? "Page " + (i + 1) : pages[i].Name, current ? FigTheme.Bold : FigTheme.Label);
                    if (Event.current.type == EventType.MouseDown && row.Contains(Event.current.mousePosition))
                    {
                        if (!current)
                        {
                            selectedId = null;
                            SwitchPage(pages[i].Id);
                            rowsDirty = true;
                        }

                        Event.current.Use();
                    }
                }

                GUI.EndScrollView();
                y += listHeight + 4;
            }

            FigTheme.Separator(new Rect(r.x, y, r.width, 1));
            y += 1;

            // Layers header with search.
            GUI.Label(new Rect(r.x + 14, y, 100, 32), "Layers", FigTheme.Bold);
            if (FigTheme.IconButton(new Rect(r.xMax - 30, y + 6, 22, 20), "search", "Search layers", searchOpen))
            {
                searchOpen = !searchOpen;
                if (!searchOpen)
                {
                    search = "";
                }
            }

            y += 32;
            if (searchOpen)
            {
                GUI.SetNextControlName("FigLayerSearch");
                string typed = EditorGUI.TextField(new Rect(r.x + 10, y, r.width - 20, 20), search, EditorStyles.toolbarSearchField);
                if (typed != search)
                {
                    search = typed;
                    treeScroll = Vector2.zero;
                }

                GUI.Label(new Rect(r.x + 12, y + 20, r.width - 20, 14), "name · type:TEXT · id:1:2 · text:… · img:hash", FigTheme.Faint);
                y += 38;
            }

            DrawLayerTree(new Rect(r.x, y, r.width, r.yMax - y));
        }

        private Vector2 pagesScroll;

        // ─── Rows ────────────────────────────────────────────────────────────────

        private void RebuildRows()
        {
            rows.Clear();
            rowsSearch = search;
            rowsPage = boardPageId;
            FigNode page = file?.Find(boardPageId);
            if (page == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                Func<FigNode, bool> match = SearchPredicate(search.Trim());
                var stack = new Stack<FigNode>();
                for (int i = page.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push(page.Children[i]);
                }

                while (stack.Count > 0 && rows.Count < MaxSearchResults)
                {
                    FigNode n = stack.Pop();
                    if (match(n))
                    {
                        rows.Add(RowFor(n, 0));
                    }

                    for (int i = n.Children.Count - 1; i >= 0; i--)
                    {
                        stack.Push(n.Children[i]);
                    }
                }

                return;
            }

            // Reverse paint order, like Figma: the top-most layer first.
            for (int i = page.Children.Count - 1; i >= 0; i--)
            {
                AddRows(page.Children[i], 0);
            }
        }

        private void AddRows(FigNode n, int depth)
        {
            LayerRow row = RowFor(n, depth);
            rows.Add(row);
            if (!row.Open)
            {
                return;
            }

            if (n.Type == "INSTANCE")
            {
                FigSceneNode inst = scene?.Find(n.Id);
                if (inst != null)
                {
                    AddSceneRows(inst, depth + 1);
                }

                return;
            }

            for (int i = n.Children.Count - 1; i >= 0; i--)
            {
                AddRows(n.Children[i], depth + 1);
            }
        }

        /// <summary>The layers inside an instance, from the expanded scene (read-only).</summary>
        private void AddSceneRows(FigSceneNode parent, int depth)
        {
            var children = new List<FigSceneNode>();
            for (int c = parent.Index + 1; c < parent.SubtreeEnd; c = scene.Nodes[c].SubtreeEnd)
            {
                children.Add(scene.Nodes[c]);
            }

            for (int i = children.Count - 1; i >= 0; i--)
            {
                FigSceneNode s = children[i];
                bool hasChildren = s.SubtreeEnd > s.Index + 1;
                bool open = hasChildren && expanded.Contains(s.Id);
                rows.Add(new LayerRow
                {
                    Id = s.Id,
                    Depth = depth,
                    Label = string.IsNullOrEmpty(s.Name) ? s.Type : s.Name,
                    Type = s.Type,
                    Icon = FigTheme.TypeIcon(s.Type, s.View.EnumName("stackMode")),
                    HasChildren = hasChildren,
                    Open = open,
                    InInstance = true,
                    Component = true,
                    Locked = s.Locked,
                });
                if (open)
                {
                    AddSceneRows(s, depth + 1);
                }
            }
        }

        private LayerRow RowFor(FigNode n, int depth)
        {
            bool instance = n.Type == "INSTANCE";
            bool hasChildren = instance ? (scene?.Find(n.Id) is FigSceneNode s && s.SubtreeEnd > s.Index + 1) : n.Children.Count > 0;
            return new LayerRow
            {
                Id = n.Id,
                Depth = depth,
                Label = n.Label,
                Type = n.Type,
                Icon = FigTheme.TypeIcon(n.Type, n.Change.Get("stackMode")?.EnumName),
                HasChildren = hasChildren,
                Open = hasChildren && expanded.Contains(n.Id) && string.IsNullOrWhiteSpace(search),
                Hidden = n.Change.Get("visible") is KiwiValue v && !v.AsBool,
                Locked = n.Change.Get("locked")?.AsBool ?? false,
                Component = n.Type == "SYMBOL" || n.Type == "INSTANCE" || n.Type == "COMPONENT_SET",
                Node = n,
            };
        }

        /// <summary>
        /// Search: plain text matches layer names; <c>type:TEXT</c> the type, <c>id:12:34</c> the GUID,
        /// <c>text:hello</c> text content and <c>img:ab12</c> layers using an image whose hash starts so.
        /// </summary>
        private Func<FigNode, bool> SearchPredicate(string query)
        {
            int colon = query.IndexOf(':');
            string key = colon > 0 ? query.Substring(0, colon).ToLowerInvariant() : null;
            string value = colon > 0 ? query.Substring(colon + 1).Trim() : query;
            const StringComparison ignoreCase = StringComparison.OrdinalIgnoreCase;
            switch (key)
            {
                case "type":
                    return n => n.Type.IndexOf(value, ignoreCase) >= 0;
                case "id":
                    return n => n.Id.StartsWith(value, StringComparison.Ordinal);
                case "text":
                    return n => (n.Change.Path("textData.characters")?.String ?? "").IndexOf(value, ignoreCase) >= 0;
                case "img":
                case "image":
                {
                    var ids = new HashSet<string>();
                    foreach (KeyValuePair<string, List<string>> pair in ImageUsers())
                    {
                        if (pair.Key.StartsWith(value.ToLowerInvariant(), StringComparison.Ordinal))
                        {
                            ids.UnionWith(pair.Value);
                        }
                    }

                    return n => ids.Contains(n.Id);
                }
                default:
                    return n => n.Name.IndexOf(query, ignoreCase) >= 0;
            }
        }

        // ─── Tree drawing ────────────────────────────────────────────────────────

        private void DrawLayerTree(Rect rect)
        {
            if (rowsDirty || rowsSearch != search || rowsPage != boardPageId)
            {
                RebuildRows();
                rowsDirty = false;
            }

            var content = new Rect(0, 0, rect.width - 14, rows.Count * RowHeight + 8);
            if (scrollToSelection && Event.current.type == EventType.Layout)
            {
                scrollToSelection = false;
                int index = rows.FindIndex(row => row.Id == selectedId);
                if (index >= 0)
                {
                    float y = index * RowHeight;
                    if (y < treeScroll.y || y + RowHeight > treeScroll.y + rect.height)
                    {
                        treeScroll.y = Mathf.Max(0, y - rect.height / 2);
                    }
                }
            }

            treeScroll = GUI.BeginScrollView(rect, treeScroll, content);
            int first = Mathf.Max(0, (int)(treeScroll.y / RowHeight));
            int last = Mathf.Min(rows.Count, first + (int)(rect.height / RowHeight) + 2);
            bool searching = !string.IsNullOrWhiteSpace(search);
            for (int i = first; i < last; i++)
            {
                DrawLayerRow(new Rect(6, i * RowHeight, content.width - 6, RowHeight - 2), rows[i], searching);
            }

            GUI.EndScrollView();
            HandleLayerKeys(rect);
        }

        private void DrawLayerRow(Rect r, LayerRow row, bool searching)
        {
            Event e = Event.current;
            bool selected = boardSelection.Contains(row.Id) || row.Id == selectedId;
            bool hover = r.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                if (selected)
                {
                    EditorGUI.DrawRect(r, row.Component ? FigTheme.SelectedComponent : FigTheme.Selected);
                }
                else if (hover || row.Id == hoverId)
                {
                    EditorGUI.DrawRect(r, new Color(1, 1, 1, 0.04f));
                }
            }

            float x = r.x + 4 + (searching ? 0 : row.Depth * Indent);
            var chevron = new Rect(x, r.y + 5, 16, 18);
            if (row.HasChildren && !searching)
            {
                FigTheme.DrawIcon(chevron, row.Open ? "chevron_down" : "chevron_right", FigTheme.TextDim, 14f);
            }

            var iconRect = new Rect(x + 16, r.y + 6, 16, 16);
            Color tint = row.Component ? FigTheme.Component : FigTheme.TextDim;
            if (row.Hidden)
            {
                tint.a *= 0.45f;
            }

            FigTheme.DrawIcon(iconRect, row.Icon, tint, 14f);

            var labelRect = new Rect(x + 38, r.y, r.xMax - x - 38 - 44, r.height);
            if (renamingId == row.Id)
            {
                GUI.SetNextControlName("FigRename");
                renameText = EditorGUI.TextField(new Rect(labelRect.x, r.y + 4, labelRect.width, 20), renameText);
                EditorGUI.FocusTextInControl("FigRename");
                if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
                {
                    CommitRename();
                    e.Use();
                }
                else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
                {
                    renamingId = null;
                    e.Use();
                }
                else if (e.type == EventType.MouseDown && !r.Contains(e.mousePosition))
                {
                    CommitRename();
                }
            }
            else
            {
                GUIStyle style = selected ? FigTheme.Bold : FigTheme.RowLabel;
                Color old = GUI.color;
                if (row.Hidden)
                {
                    GUI.color = new Color(1, 1, 1, 0.45f);
                }

                GUI.Label(labelRect, searching ? row.Label + "   " + (row.Node != null ? PathOf(row.Node) : "") : row.Label, style);
                GUI.color = old;
            }

            // Eye and lock, shown on hover (and always when set).
            if (!row.InInstance)
            {
                var eye = new Rect(r.xMax - 42, r.y + 4, 20, 20);
                var lockRect = new Rect(r.xMax - 22, r.y + 4, 20, 20);
                if (hover || row.Hidden)
                {
                    if (FigTheme.IconButton(eye, row.Hidden ? "eye_off" : "eye", row.Hidden ? "Show" : "Hide", false, FigTheme.TextDim))
                    {
                        string id = row.Id;
                        Defer(() => ToggleVisible(id));
                        return;
                    }
                }

                if (hover || row.Locked)
                {
                    if (FigTheme.IconButton(lockRect, row.Locked ? "lock" : "unlock", row.Locked ? "Unlock" : "Lock", false, FigTheme.TextDim))
                    {
                        string id = row.Id;
                        Defer(() => ToggleLocked(id));
                        return;
                    }
                }
            }

            if (e.type != EventType.MouseDown || !hover)
            {
                return;
            }

            if (row.HasChildren && !searching && chevron.Contains(e.mousePosition))
            {
                ToggleExpanded(row.Id, !row.Open, e.alt);
                e.Use();
                return;
            }

            if (e.button == 0)
            {
                if (e.clickCount == 2 && !row.InInstance)
                {
                    renamingId = row.Id;
                    renameText = row.Label;
                }
                else if (e.shift || e.control || e.command)
                {
                    SetBoardSelection(row.Id, true);
                }
                else
                {
                    SetBoardSelection(row.Id, false);
                    pendingZoomTo = null;
                }

                if (searching && e.clickCount == 2)
                {
                    search = "";
                    Select(row.Id);
                }

                e.Use();
            }
            else if (e.button == 1)
            {
                if (!boardSelection.Contains(row.Id))
                {
                    SetBoardSelection(row.Id, false);
                }

                ShowLayerMenu(row);
                e.Use();
            }
        }

        private static string PathOf(FigNode node)
        {
            var parts = new List<string>();
            for (FigNode p = node.Parent; p != null && p.Type != "CANVAS" && parts.Count < 3; p = p.Parent)
            {
                parts.Add(p.Label);
            }

            parts.Reverse();
            return string.Join(" / ", parts);
        }

        private void ToggleExpanded(string id, bool open, bool recursive)
        {
            if (open)
            {
                expanded.Add(id);
            }
            else
            {
                expanded.Remove(id);
            }

            if (recursive && file?.Find(id) is FigNode n)
            {
                var stack = new Stack<FigNode>(n.Children);
                while (stack.Count > 0)
                {
                    FigNode c = stack.Pop();
                    if (open)
                    {
                        expanded.Add(c.Id);
                    }
                    else
                    {
                        expanded.Remove(c.Id);
                    }

                    foreach (FigNode cc in c.Children)
                    {
                        stack.Push(cc);
                    }
                }
            }

            rowsDirty = true;
            Repaint();
        }

        private void CommitRename()
        {
            string id = renamingId;
            string text = renameText;
            renamingId = null;
            FigNode node = file.Find(id);
            if (node == null || text == null || text == node.Name)
            {
                return;
            }

            RecordFields("Rename", null, node.Change, "name");
            node.Change.Set("name", KiwiValue.FromString(text));
            MarkDirty();
            rowsDirty = true;
            InvalidateBoardNode(id);
        }

        private void ToggleVisible(string id)
        {
            FigNode node = file.Find(id);
            if (node == null)
            {
                return;
            }

            RecordFields("Show/Hide", null, node.Change, "visible");
            bool visible = node.Change.Get("visible")?.AsBool ?? true;
            node.Change.Set("visible", KiwiValue.FromBool(!visible));
            MarkDirty();
            OnStructureChanged();
        }

        private void ToggleLocked(string id)
        {
            FigNode node = file.Find(id);
            if (node == null)
            {
                return;
            }

            RecordFields("Lock/Unlock", null, node.Change, "locked");
            bool locked = node.Change.Get("locked")?.AsBool ?? false;
            node.Change.Set("locked", KiwiValue.FromBool(!locked));
            MarkDirty();
            rowsDirty = true;
            InvalidateBoardNode(id);
        }

        private void HandleLayerKeys(Rect rect)
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown || GUIUtility.keyboardControl != 0 || rows.Count == 0 || renamingId != null)
            {
                return;
            }

            if (!rect.Contains(e.mousePosition))
            {
                return;
            }

            int index = rows.FindIndex(row => row.Id == selectedId);
            LayerRow current = index >= 0 ? rows[index] : default;
            switch (e.keyCode)
            {
                case KeyCode.UpArrow:
                    SetBoardSelection(rows[Mathf.Max(0, index - 1)].Id, false);
                    break;
                case KeyCode.DownArrow:
                    SetBoardSelection(rows[Mathf.Min(rows.Count - 1, index + 1)].Id, false);
                    break;
                case KeyCode.RightArrow:
                    if (index >= 0 && current.HasChildren)
                    {
                        ToggleExpanded(current.Id, true, e.alt);
                    }

                    break;
                case KeyCode.LeftArrow:
                    if (index >= 0 && current.Open)
                    {
                        ToggleExpanded(current.Id, false, e.alt);
                    }

                    break;
                case KeyCode.F2:
                    if (index >= 0 && !current.InInstance)
                    {
                        renamingId = current.Id;
                        renameText = current.Label;
                    }

                    break;
                default:
                    return;
            }

            scrollToSelection = true;
            e.Use();
            Repaint();
        }

        private void ShowLayerMenu(LayerRow row)
        {
            var menu = new GenericMenu();
            bool editable = !row.InInstance && row.Node != null;
            AddMenuItem(menu, "Rename", editable, () =>
            {
                renamingId = row.Id;
                renameText = row.Label;
            });
            AddMenuItem(menu, "Zoom to Layer", true, () => pendingZoomTo = row.Id);
            menu.AddSeparator("");
            AddMenuItem(menu, "Duplicate", editable, () => RequestBoardAction(DuplicateBoardSelection));
            AddMenuItem(menu, "Delete", editable, () => RequestBoardAction(DeleteBoardSelection));
            AddMenuItem(menu, row.Hidden ? "Show" : "Hide", editable, () => Defer(() => ToggleVisible(row.Id)));
            AddMenuItem(menu, row.Locked ? "Unlock" : "Lock", editable, () => Defer(() => ToggleLocked(row.Id)));
            menu.AddSeparator("");
            AddMenuItem(menu, "Bring Forward", editable, () => Defer(() => MoveNode(row.Node, 1)));
            AddMenuItem(menu, "Send Backward", editable, () => Defer(() => MoveNode(row.Node, -1)));
            menu.AddSeparator("");
            AddMenuItem(menu, "Expand All", row.HasChildren, () => ToggleExpanded(row.Id, true, true));
            AddMenuItem(menu, "Collapse All", row.HasChildren, () => ToggleExpanded(row.Id, false, true));
            if (row.Type == "INSTANCE" && row.Node != null)
            {
                string main = FigUtil.GuidString(row.Node.Change.Get("overriddenSymbolID")) ?? FigUtil.GuidString(row.Node.Change.Path("symbolData.symbolID"));
                AddMenuItem(menu, "Go to Main Component", file.Find(main) != null, () =>
                {
                    SetBoardSelection(main, false);
                    pendingZoomTo = main;
                });
            }

            menu.AddSeparator("");
            AddMenuItem(menu, "Copy GUID", true, () => EditorGUIUtility.systemCopyBuffer = row.Id);
            AddMenuItem(menu, "Copy Name", true, () => EditorGUIUtility.systemCopyBuffer = row.Label);
            menu.ShowAsContext();
        }

        private static void AddMenuItem(GenericMenu menu, string path, bool enabled, GenericMenu.MenuFunction action)
        {
            if (enabled)
            {
                menu.AddItem(new GUIContent(path), false, action);
            }
            else
            {
                menu.AddDisabledItem(new GUIContent(path));
            }
        }
    }
}
