using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>The layer tree: a virtualized list of the visible rows, with search.</summary>
    public partial class FigEditorWindow
    {
        private const float RowHeight = 18f;
        private const float IndentWidth = 14f;
        private const int MaxSearchResults = 5000;

        private static readonly Color SelectionColor = new Color(0.24f, 0.48f, 0.90f, 0.45f);
        private static readonly Color StripeColor = new Color(0f, 0f, 0f, 0.06f);

        [SerializeField] private string search = "";

        private HashSet<string> expanded = new();
        private readonly List<FigNode> rows = new();
        private bool rowsDirty = true;
        private bool scrollToSelection;
        private Vector2 treeScroll;
        private string rowsSearch;
        private bool searchTruncated;

        private void RebuildRows()
        {
            rows.Clear();
            searchTruncated = false;
            rowsSearch = search;
            if (file == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                Func<FigNode, bool> match = SearchPredicate(search.Trim());
                foreach (FigNode root in file.Roots)
                {
                    var stack = new Stack<FigNode>();
                    stack.Push(root);
                    while (stack.Count > 0)
                    {
                        FigNode n = stack.Pop();
                        if (match(n))
                        {
                            if (rows.Count >= MaxSearchResults)
                            {
                                searchTruncated = true;
                                return;
                            }

                            rows.Add(n);
                        }

                        for (int i = n.Children.Count - 1; i >= 0; i--)
                        {
                            stack.Push(n.Children[i]);
                        }
                    }
                }

                return;
            }

            var pending = new Stack<FigNode>();
            for (int i = file.Roots.Count - 1; i >= 0; i--)
            {
                pending.Push(file.Roots[i]);
            }

            while (pending.Count > 0)
            {
                FigNode n = pending.Pop();
                rows.Add(n);
                if (n.Children.Count > 0 && expanded.Contains(n.Id))
                {
                    for (int i = n.Children.Count - 1; i >= 0; i--)
                    {
                        pending.Push(n.Children[i]);
                    }
                }
            }
        }

        /// <summary>
        /// Search syntax: plain text matches layer names; <c>type:TEXT</c> matches the layer type,
        /// <c>id:12:34</c> the GUID, <c>text:hello</c> text content and <c>img:ab12</c> layers using
        /// an image whose hash starts with that.
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
                    Dictionary<string, List<string>> users = ImageUsers();
                    var ids = new HashSet<string>();
                    foreach (KeyValuePair<string, List<string>> pair in users)
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

        private void DrawTree(Rect rect)
        {
            if (rowsDirty || rowsSearch != search)
            {
                RebuildRows();
                rowsDirty = false;
            }

            // Search bar.
            var searchRect = new Rect(rect.x + 4, rect.y + 2, rect.width - 8, 18);
            EditorGUI.BeginChangeCheck();
            string newSearch = EditorGUI.TextField(searchRect, search, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck())
            {
                search = newSearch;
                treeScroll = Vector2.zero;
            }

            var hintRect = new Rect(rect.x + 4, searchRect.yMax, rect.width - 8, 16);
            string hint = string.IsNullOrWhiteSpace(search)
                ? "name · type:TEXT · id:1:2 · text:… · img:hash"
                : searchTruncated ? $"first {rows.Count} matches" : $"{rows.Count} matches";
            GUI.Label(hintRect, hint, EditorStyles.centeredGreyMiniLabel);

            var listRect = new Rect(rect.x, hintRect.yMax + 2, rect.width, rect.yMax - hintRect.yMax - 2);
            var content = new Rect(0, 0, listRect.width - 14, rows.Count * RowHeight);

            if (scrollToSelection && Event.current.type == EventType.Layout)
            {
                scrollToSelection = false;
                int index = rows.FindIndex(n => n.Id == selectedId);
                if (index >= 0)
                {
                    float y = index * RowHeight;
                    if (y < treeScroll.y || y + RowHeight > treeScroll.y + listRect.height)
                    {
                        treeScroll.y = Mathf.Max(0, y - listRect.height / 2);
                    }
                }
            }

            treeScroll = GUI.BeginScrollView(listRect, treeScroll, content);
            int first = Mathf.Max(0, (int)(treeScroll.y / RowHeight));
            int last = Mathf.Min(rows.Count, first + (int)(listRect.height / RowHeight) + 2);
            bool searching = !string.IsNullOrWhiteSpace(search);
            for (int i = first; i < last; i++)
            {
                DrawRow(new Rect(0, i * RowHeight, content.width, RowHeight), rows[i], i, searching);
            }

            GUI.EndScrollView();

            HandleTreeKeys(listRect);
        }

        private void DrawRow(Rect rect, FigNode node, int index, bool searching)
        {
            Event e = Event.current;
            bool selected = node.Id == selectedId;
            if (e.type == EventType.Repaint)
            {
                if (selected)
                {
                    EditorGUI.DrawRect(rect, SelectionColor);
                }
                else if (index % 2 == 1)
                {
                    EditorGUI.DrawRect(rect, StripeColor);
                }
            }

            float indent = searching ? 4 : 4 + node.Depth * IndentWidth;
            var foldRect = new Rect(rect.x + indent, rect.y, 14, rect.height);
            bool hasChildren = node.Children.Count > 0 && !searching;
            if (hasChildren)
            {
                bool isOpen = expanded.Contains(node.Id);
                bool open = EditorGUI.Foldout(foldRect, isOpen, GUIContent.none, true);
                if (open != isOpen)
                {
                    ToggleExpanded(node, open, e.alt);
                }
            }

            bool hidden = node.Change.Get("visible") is KiwiValue v && !v.AsBool;
            string label = searching ? $"{node.Label}  <color=#888>{PathOf(node)}</color>" : node.Label;
            var labelRect = new Rect(foldRect.xMax, rect.y, rect.width - foldRect.xMax - 70, rect.height);
            var typeRect = new Rect(rect.xMax - 70, rect.y, 66, rect.height);

            Color old = GUI.color;
            if (hidden)
            {
                GUI.color = new Color(old.r, old.g, old.b, 0.5f);
            }

            GUI.Label(labelRect, label, TreeLabelStyle);
            GUI.Label(typeRect, ShortType(node.Type), EditorStyles.miniLabel);
            GUI.color = old;

            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition) && !foldRect.Contains(e.mousePosition))
            {
                if (e.button == 0)
                {
                    selectedId = node.Id;
                    GUI.FocusControl(null);
                    if (e.clickCount == 2)
                    {
                        if (searching)
                        {
                            search = "";
                            Select(node.Id);
                        }
                        else if (hasChildren)
                        {
                            ToggleExpanded(node, !expanded.Contains(node.Id), false);
                        }
                    }

                    e.Use();
                }
                else if (e.button == 1)
                {
                    selectedId = node.Id;
                    ShowRowMenu(node);
                    e.Use();
                }
            }
        }

        private GUIStyle treeLabelStyle;

        private GUIStyle TreeLabelStyle => treeLabelStyle ??= new GUIStyle(EditorStyles.label) { richText = true, clipping = TextClipping.Clip };

        private static string ShortType(string type)
        {
            switch (type)
            {
                case "ROUNDED_RECTANGLE":
                    return "RECT";
                case "BOOLEAN_OPERATION":
                    return "BOOLEAN";
                case "REGULAR_POLYGON":
                    return "POLYGON";
                case "SYMBOL":
                    return "COMPONENT";
                default:
                    return type;
            }
        }

        private static string PathOf(FigNode node)
        {
            var parts = new List<string>();
            for (FigNode p = node.Parent; p != null && parts.Count < 4; p = p.Parent)
            {
                parts.Add(p.Label);
            }

            parts.Reverse();
            return string.Join(" / ", parts);
        }

        private void ToggleExpanded(FigNode node, bool open, bool recursive)
        {
            var stack = new Stack<FigNode>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                FigNode n = stack.Pop();
                if (open)
                {
                    expanded.Add(n.Id);
                }
                else
                {
                    expanded.Remove(n.Id);
                }

                if (!recursive)
                {
                    break;
                }

                foreach (FigNode child in n.Children)
                {
                    stack.Push(child);
                }
            }

            rowsDirty = true;
            Repaint();
        }

        private void HandleTreeKeys(Rect listRect)
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown || GUIUtility.keyboardControl != 0 || rows.Count == 0)
            {
                return;
            }

            if (!listRect.Contains(e.mousePosition) && mouseOverWindow != this)
            {
                return;
            }

            int index = rows.FindIndex(n => n.Id == selectedId);
            FigNode current = index >= 0 ? rows[index] : null;
            switch (e.keyCode)
            {
                case KeyCode.UpArrow:
                    selectedId = rows[Mathf.Max(0, index - 1)].Id;
                    scrollToSelection = true;
                    break;
                case KeyCode.DownArrow:
                    selectedId = rows[Mathf.Min(rows.Count - 1, index + 1)].Id;
                    scrollToSelection = true;
                    break;
                case KeyCode.RightArrow:
                    if (current != null && current.Children.Count > 0)
                    {
                        ToggleExpanded(current, true, e.alt);
                    }

                    break;
                case KeyCode.LeftArrow:
                    if (current != null && expanded.Contains(current.Id) && current.Children.Count > 0)
                    {
                        ToggleExpanded(current, false, e.alt);
                    }
                    else if (current?.Parent != null)
                    {
                        Select(current.Parent.Id);
                    }

                    break;
                default:
                    return;
            }

            e.Use();
            Repaint();
        }

        private void ShowRowMenu(FigNode node)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Expand All"), false, () => ToggleExpanded(node, true, true));
            menu.AddItem(new GUIContent("Collapse All"), false, () => ToggleExpanded(node, false, true));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Preview"), false, () =>
            {
                selectedId = node.Id;
                tab = 1;
                previewDirty = true;
            });
            menu.AddItem(new GUIContent("Copy GUID"), false, () => EditorGUIUtility.systemCopyBuffer = node.Id);
            menu.AddItem(new GUIContent("Copy Name"), false, () => EditorGUIUtility.systemCopyBuffer = node.Name);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Move Up"), false, () => Defer(() => MoveNode(node, -1)));
            menu.AddItem(new GUIContent("Move Down"), false, () => Defer(() => MoveNode(node, 1)));
            menu.AddItem(new GUIContent("Duplicate"), false, () => Defer(() => DuplicateNode(node)));
            menu.AddItem(new GUIContent("Delete"), false, () => Defer(() => DeleteNode(node)));
            menu.ShowAsContext();
        }
    }
}
