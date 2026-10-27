using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// The Board tab: an interactive canvas for one page, laid out and operated like Figma's.
    ///
    /// Navigation: wheel scrolls, Ctrl/Cmd/Alt + wheel zooms at the cursor, Space-drag or
    /// middle-drag pans, Shift+1 fits the page, Shift+2 / F zooms to the selection, Shift+0 is 100%.
    ///
    /// Selection: click selects at the current level (inside a top-level frame its direct children,
    /// elsewhere the top-level layer); double-click goes one level deeper; Ctrl/Cmd-click selects the
    /// deepest layer; Shift-click adds or removes; drag on empty canvas (or an unselected top-level
    /// frame's background) draws a marquee; Esc selects the parent, Enter the first child.
    ///
    /// Editing: drag to move (Shift locks the axis; smart guides snap to siblings and the parent,
    /// Ctrl/Cmd disables them), drag a handle to resize (Shift keeps the ratio, Alt resizes from the
    /// centre), arrows nudge by 1 (Shift: 10), Delete removes, Ctrl/Cmd+D duplicates,
    /// Ctrl/Cmd+Shift+H toggles visibility. Everything goes through the Fig Editor's undo history,
    /// which Unity's Undo (Ctrl/Cmd+Z) drives.
    ///
    /// The page is drawn from a cached <see cref="FigBoardScene"/> (patched in place while dragging)
    /// by <see cref="FigBoardRenderer"/>.
    /// </summary>
    public partial class FigEditorWindow
    {
        private const float HandleSize = 7f;
        private const float DragThreshold = 3f;
        private const float GuideSnapPixels = 6f;
        private const float MinScale = 0.01f;
        private const float MaxScale = 256f;

        private static readonly Color AccentColor = new Color(0.05f, 0.6f, 1f, 1f);
        private static readonly Color AccentFill = new Color(0.05f, 0.6f, 1f, 0.12f);
        private static readonly Color GuideColor = new Color(0.95f, 0.28f, 0.13f, 1f);
        private static readonly Color HandleFill = Color.white;

        [SerializeField] private string boardPageId;
        [SerializeField] private bool boardSidebar = true;
        [SerializeField] private float boardSidebarWidth = 300f;
        [SerializeField] private bool snapToPixel = true;
        [SerializeField] private bool smartGuides = true;

        private FigBoardScene scene;
        private bool boardDirty = true;
        private readonly HashSet<string> boardChangedIds = new();
        private FigBoardRenderer boardRenderer;
        private float viewScale = 1f;
        private Vector2 viewOffset;
        private bool viewNeedsFit = true;
        private string pendingZoomTo;
        private Vector2 canvasSize;

        private readonly HashSet<string> boardSelection = new();
        private string hoverId;
        private bool spaceHeld;
        private bool focusTextField;
        private int boardControlId;

        private enum BoardDrag
        {
            None,
            Pending,
            Move,
            Resize,
            Marquee,
            Pan,
        }

        private BoardDrag drag;
        private Vector2 dragStartMouse;
        private Vector2 dragStartWorld;
        private bool pendingMarquee;
        private int marqueeContainer = -1;
        private Rect marqueeWorld;
        private readonly HashSet<string> marqueeBase = new();
        private int resizeHandle = -1;

        private struct MoveOrigin
        {
            public string Id;
            public KiwiValue Transform;
            public float X;
            public float Y;
            public FigAffine ParentInverse;
        }

        private readonly List<MoveOrigin> moveOrigins = new();
        private Rect moveStartBounds;
        private readonly List<float> guideXs = new();
        private readonly List<float> guideYs = new();
        private readonly List<Rect> guideSources = new();
        private struct Guide
        {
            public Vector2 A;
            public Vector2 B;
        }

        private readonly List<Guide> activeGuides = new();

        private struct ResizeOrigin
        {
            public string Id;
            public float Width;
            public float Height;
            public float X;
            public float Y;
            public FigAffine Local;
            public FigAffine WorldInverse;
        }

        private ResizeOrigin resizeOrigin;
        private struct FrameTitle
        {
            public Rect Rect;
            public string Id;
        }

        private readonly List<FrameTitle> frameTitles = new();

        private FigUndo undo = new();
        private FigUndoProxy undoProxy;

        // ─── Lifecycle (called from the main partial) ────────────────────────────

        private void EnableBoard()
        {
            wantsMouseMove = true;
            boardRenderer ??= new FigBoardRenderer();
            boardRenderer.ImageSource = BoardImage;
            if (undoProxy == null)
            {
                undoProxy = CreateInstance<FigUndoProxy>();
                undoProxy.hideFlags = HideFlags.HideAndDontSave;
            }

            Undo.undoRedoPerformed += OnUnityUndoRedo;
        }

        private void DisableBoard()
        {
            Undo.undoRedoPerformed -= OnUnityUndoRedo;
            boardRenderer?.Dispose();
            boardRenderer = null;
            if (undoProxy != null)
            {
                Undo.ClearUndo(undoProxy);
                DestroyImmediate(undoProxy);
                undoProxy = null;
            }
        }

        /// <summary>A freshly loaded file: no history, no caches, fit the view.</summary>
        private void ResetBoard()
        {
            undo.Clear();
            if (undoProxy != null)
            {
                Undo.ClearUndo(undoProxy);
                undoProxy.Revision = 0;
            }

            boardRenderer?.Clear();
            scene = null;
            boardDirty = true;
            boardSelection.Clear();
            viewNeedsFit = true;
        }

        /// <summary>The scene must be rebuilt (structure, visibility or many layers changed).</summary>
        private void InvalidateBoard()
        {
            boardDirty = true;
        }

        /// <summary>Only <paramref name="id"/> changed (placement or paint): patch its subtree.</summary>
        private void InvalidateBoardNode(string id)
        {
            if (id != null)
            {
                boardChangedIds.Add(id);
            }
        }

        // ─── Undo ────────────────────────────────────────────────────────────────

        /// <summary>Snapshot <paramref name="fields"/> of <paramref name="owner"/> as one undo step (before changing them).</summary>
        private void RecordFields(string label, string mergeKey, KiwiValue owner, params string[] fields)
        {
            if (file == null || owner == null)
            {
                return;
            }

            bool fresh = undo.Begin(label, mergeKey, EditorApplication.timeSinceStartup);
            if (!fresh)
            {
                return;
            }

            foreach (string f in fields)
            {
                undo.Capture(owner, f);
            }

            CommitUndo(label);
        }

        /// <summary>Snapshot the whole layer list (before a delete / duplicate) as one undo step.</summary>
        private void RecordStructure(string label)
        {
            if (file == null)
            {
                return;
            }

            undo.Begin(label, null, EditorApplication.timeSinceStartup);
            undo.CaptureStructure(file);
            CommitUndo(label);
        }

        /// <summary>Begin a multi-owner step; add fields with <see cref="FigUndo.Capture"/>, then <see cref="CommitUndo"/>.</summary>
        private bool BeginUndo(string label, string mergeKey)
        {
            return file != null && undo.Begin(label, mergeKey, EditorApplication.timeSinceStartup);
        }

        private void CommitUndo(string label)
        {
            if (undo.Commit() == null || undoProxy == null)
            {
                return;
            }

            Undo.RecordObject(undoProxy, "Fig Editor: " + label);
            undoProxy.Revision = undo.Revision;
        }

        private void OnUnityUndoRedo()
        {
            if (file == null || undoProxy == null || undoProxy.Revision == undo.Revision)
            {
                return;
            }

            StepUndoTo(undoProxy.Revision);
        }

        private void StepUndoTo(int revision)
        {
            bool changed = false;
            while (undo.Revision > revision && undo.CanUndo)
            {
                undo.Undo(file);
                changed = true;
            }

            while (undo.Revision < revision && undo.CanRedo)
            {
                undo.Redo(file);
                changed = true;
            }

            if (changed)
            {
                AfterUndoRedo();
            }
        }

        private void UndoStep(bool redo)
        {
            FigUndo.Entry entry = redo ? undo.Redo(file) : undo.Undo(file);
            if (entry == null)
            {
                return;
            }

            // Keep Unity's copy of the revision in step without recording a Unity undo step.
            if (undoProxy != null)
            {
                undoProxy.Revision = undo.Revision;
            }

            AfterUndoRedo();
        }

        private void AfterUndoRedo()
        {
            string keep = selectedId;
            file.RebuildIndex();
            OnStructureChanged();
            boardSelection.RemoveWhere(id => file.Find(id) == null);
            if (file.Find(keep) == null)
            {
                selectedId = null;
            }

            MarkDirty();
            Repaint();
        }

        // ─── Tab ─────────────────────────────────────────────────────────────────

        private void DrawBoardTab(Rect rect)
        {
            List<FigNode> pages = Pages();
            FigNode page = ResolvePage(pages);
            DrawBoardToolbar(pages, page);

            var area = new Rect(rect.x, rect.y + ToolbarHeight, rect.width, rect.height - ToolbarHeight);
            float sidebar = boardSidebar ? Mathf.Clamp(boardSidebarWidth, 220f, Mathf.Max(220f, area.width - 240f)) : 0f;
            var canvasRect = new Rect(area.x, area.y, area.width - sidebar, area.height);
            if (page == null)
            {
                EditorGUI.HelpBox(new Rect(canvasRect.x + 8, canvasRect.y + 8, canvasRect.width - 16, 40), "This file has no pages.", MessageType.Info);
                return;
            }

            EnsureScene(page);
            DrawBoardCanvas(canvasRect);

            if (sidebar > 0)
            {
                var sideRect = new Rect(canvasRect.xMax, area.y, sidebar, area.height);
                EditorGUI.DrawRect(new Rect(sideRect.x, sideRect.y, 1, sideRect.height), new Color(0f, 0f, 0f, 0.35f));
                GUILayout.BeginArea(new Rect(sideRect.x + 2, sideRect.y, sideRect.width - 2, sideRect.height));
                if (boardSelection.Count > 1)
                {
                    EditorGUILayout.HelpBox($"{boardSelection.Count} layers selected. The properties show the last one clicked.", MessageType.None);
                }

                DrawLayerTab();
                GUILayout.EndArea();
            }

            if (boardRenderer.HasPendingWork)
            {
                Repaint();
            }
        }

        private List<FigNode> Pages()
        {
            var pages = new List<FigNode>();
            foreach (FigNode root in file.Roots)
            {
                if (root.Type == "CANVAS")
                {
                    pages.Add(root);
                }

                foreach (FigNode child in root.Children)
                {
                    if (child.Type == "CANVAS")
                    {
                        pages.Add(child);
                    }
                }
            }

            return pages;
        }

        private static FigNode PageOf(FigNode node)
        {
            for (FigNode n = node; n != null; n = n.Parent)
            {
                if (n.Type == "CANVAS")
                {
                    return n;
                }
            }

            return null;
        }

        /// <summary>The page to show: the selected layer's (so picking in the tree follows it), else the last one.</summary>
        private FigNode ResolvePage(List<FigNode> pages)
        {
            FigNode selectedPage = PageOf(Selected);
            if (selectedPage != null && selectedPage.Id != boardPageId)
            {
                SwitchPage(selectedPage.Id);
                if (Selected != null && Selected != selectedPage)
                {
                    pendingZoomTo = Selected.Id;
                }
            }

            FigNode page = file.Find(boardPageId);
            if (page == null || page.Type != "CANVAS")
            {
                page = pages.Count > 0 ? pages[0] : null;
                boardPageId = page?.Id;
                viewNeedsFit = true;
            }

            return page;
        }

        private void SwitchPage(string id)
        {
            boardPageId = id;
            boardSelection.Clear();
            boardDirty = true;
            viewNeedsFit = true;
            hoverId = null;
        }

        private void EnsureScene(FigNode page)
        {
            if (scene == null || boardDirty || scene.Page?.Id != page.Id || scene.Page != page)
            {
                scene = FigBoardScene.Build(page);
                boardRenderer.File = file;
                boardDirty = false;
                boardChangedIds.Clear();
                boardSelection.RemoveWhere(id => scene.Find(id) == null && file.Find(id) == null);
                return;
            }

            if (boardChangedIds.Count > 0)
            {
                foreach (string id in boardChangedIds)
                {
                    FigBoardItem item = scene.Find(id);
                    if (item != null)
                    {
                        scene.Refresh(item.Index, true);
                        boardRenderer.Invalidate(id);
                    }
                }

                boardChangedIds.Clear();
                scene.UpdateContentBounds();
            }
        }

        private void DrawBoardToolbar(List<FigNode> pages, FigNode page)
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);

            var names = new string[pages.Count];
            int current = 0;
            for (int i = 0; i < pages.Count; i++)
            {
                names[i] = string.IsNullOrEmpty(pages[i].Name) ? "Page " + (i + 1) : pages[i].Name;
                if (pages[i] == page)
                {
                    current = i;
                }
            }

            if (pages.Count > 0)
            {
                int picked = EditorGUILayout.Popup(current, names, EditorStyles.toolbarPopup, GUILayout.MaxWidth(180));
                if (picked != current)
                {
                    string id = pages[picked].Id;
                    selectedId = null;
                    SwitchPage(id);
                }
            }

            GUILayout.Space(6);
            if (GUILayout.Button(new GUIContent("Fit", "Zoom to fit the page (Shift+1)"), EditorStyles.toolbarButton))
            {
                viewNeedsFit = true;
            }

            using (new EditorGUI.DisabledScope(boardSelection.Count == 0))
            {
                if (GUILayout.Button(new GUIContent("Selection", "Zoom to the selection (Shift+2 or F)"), EditorStyles.toolbarButton))
                {
                    ZoomToSelection();
                }
            }

            if (GUILayout.Button(new GUIContent((viewScale * 100f).ToString("0", CultureInfo.InvariantCulture) + "%", "Zoom to 100% (Shift+0)"), EditorStyles.toolbarButton, GUILayout.Width(52)))
            {
                ZoomAt(canvasSize * 0.5f, 1f / viewScale);
            }

            GUILayout.Space(6);
            snapToPixel = GUILayout.Toggle(snapToPixel, new GUIContent("Snap px", "Round positions and sizes to whole pixels"), EditorStyles.toolbarButton);
            smartGuides = GUILayout.Toggle(smartGuides, new GUIContent("Guides", "Snap to sibling and parent edges while moving"), EditorStyles.toolbarButton);

            GUILayout.Space(6);
            using (new EditorGUI.DisabledScope(!undo.CanUndo))
            {
                if (GUILayout.Button(new GUIContent("Undo", undo.CanUndo ? "Undo " + undo.UndoLabel : "Nothing to undo"), EditorStyles.toolbarButton, GUILayout.Width(40)))
                {
                    UndoStep(false);
                }
            }

            using (new EditorGUI.DisabledScope(!undo.CanRedo))
            {
                if (GUILayout.Button(new GUIContent("Redo", undo.CanRedo ? "Redo " + undo.RedoLabel : "Nothing to redo"), EditorStyles.toolbarButton, GUILayout.Width(40)))
                {
                    UndoStep(true);
                }
            }

            GUILayout.FlexibleSpace();
            if (scene != null)
            {
                string stats = $"{boardRenderer.DrawnItems}/{scene.Items.Count} drawn · {boardRenderer.RasterCount} cached vectors";
                GUILayout.Label(new GUIContent(stats, $"Raster cache: {boardRenderer.RasterMegapixels} MP"), EditorStyles.miniLabel);
                if (scene.Truncated)
                {
                    GUILayout.Label($"(first {FigBoardScene.MaxItems})", EditorStyles.miniLabel);
                }
            }

            boardSidebar = GUILayout.Toggle(boardSidebar, new GUIContent("Properties", "Show the layer properties beside the board"), EditorStyles.toolbarButton);
            GUILayout.EndHorizontal();
        }

        // ─── View ────────────────────────────────────────────────────────────────

        private Vector2 ToCanvas(Vector2 world) => world * viewScale + viewOffset;

        private Vector2 ToWorld(Vector2 canvas) => (canvas - viewOffset) / viewScale;

        private Rect ToCanvas(Rect world) => new Rect(world.x * viewScale + viewOffset.x, world.y * viewScale + viewOffset.y, world.width * viewScale, world.height * viewScale);

        private void ZoomAt(Vector2 canvasPoint, float factor)
        {
            Vector2 world = ToWorld(canvasPoint);
            viewScale = Mathf.Clamp(viewScale * factor, MinScale, MaxScale);
            viewOffset = canvasPoint - world * viewScale;
            Repaint();
        }

        private void ZoomToRect(Rect world, float maxScale)
        {
            if (canvasSize.x <= 0 || canvasSize.y <= 0)
            {
                return;
            }

            const float pad = 48f;
            float w = Mathf.Max(1f, world.width);
            float h = Mathf.Max(1f, world.height);
            viewScale = Mathf.Clamp(Mathf.Min((canvasSize.x - 2 * pad) / w, (canvasSize.y - 2 * pad) / h), MinScale, maxScale);
            viewOffset = canvasSize * 0.5f - world.center * viewScale;
            Repaint();
        }

        private void ZoomToSelection()
        {
            Rect? bounds = SelectionBounds();
            if (bounds.HasValue)
            {
                ZoomToRect(bounds.Value, 8f);
            }
        }

        private Rect? SelectionBounds()
        {
            Rect? bounds = null;
            foreach (string id in boardSelection)
            {
                FigBoardItem item = scene?.Find(id);
                if (item != null)
                {
                    bounds = bounds.HasValue ? FigGeometry.Union(bounds.Value, item.Bounds) : item.Bounds;
                }
            }

            return bounds;
        }

        // ─── Canvas ──────────────────────────────────────────────────────────────

        private void DrawBoardCanvas(Rect canvasRect)
        {
            boardControlId = GUIUtility.GetControlID(FocusType.Keyboard);
            canvasSize = canvasRect.size;
            SyncSelectionFromTree();

            if (viewNeedsFit && Event.current.type == EventType.Layout)
            {
                viewNeedsFit = false;
                ZoomToRect(scene.ContentBounds, 1f);
            }

            if (pendingZoomTo != null && Event.current.type == EventType.Layout)
            {
                FigBoardItem target = scene.Find(pendingZoomTo);
                pendingZoomTo = null;
                if (target != null)
                {
                    ZoomToRect(target.Bounds, Mathf.Max(viewScale, 1f));
                }
            }

            GUI.BeginClip(canvasRect);
            var local = new Rect(0, 0, canvasRect.width, canvasRect.height);
            HandleBoardInput(local);
            if (Event.current.type == EventType.Repaint)
            {
                boardRenderer.Draw(scene, canvasSize, viewScale, viewOffset);
                DrawBoardOverlays(local);
            }

            if (spaceHeld || drag == BoardDrag.Pan)
            {
                EditorGUIUtility.AddCursorRect(local, MouseCursor.Pan);
            }

            GUI.EndClip();
        }

        /// <summary>A selection made in the tree (or the inspector) becomes the board selection.</summary>
        private void SyncSelectionFromTree()
        {
            if (selectedId == null)
            {
                if (boardSelection.Count > 0 && drag == BoardDrag.None)
                {
                    boardSelection.Clear();
                }

                return;
            }

            if (!boardSelection.Contains(selectedId))
            {
                boardSelection.Clear();
                if (scene.Find(selectedId) != null)
                {
                    boardSelection.Add(selectedId);
                }
            }
        }

        private void SetBoardSelection(string id, bool additive)
        {
            if (!additive)
            {
                boardSelection.Clear();
            }

            if (id == null)
            {
                selectedId = null;
                return;
            }

            if (additive && boardSelection.Contains(id))
            {
                boardSelection.Remove(id);
                selectedId = boardSelection.Count > 0 ? First(boardSelection) : null;
                return;
            }

            boardSelection.Add(id);
            Select(id);
        }

        private static string First(HashSet<string> set)
        {
            foreach (string s in set)
            {
                return s;
            }

            return null;
        }

        // ─── Selection rules ─────────────────────────────────────────────────────

        /// <summary>
        /// Which layer a click on <paramref name="deep"/> (the top-most layer under the cursor) selects:
        /// the top-level layer, or a top-level frame's direct child; inside or beside the current
        /// selection, the same level; one level deeper on double-click; the deepest with Ctrl/Cmd.
        /// </summary>
        private int ResolveTarget(int deep, bool doubleClick, bool deepest)
        {
            if (deep < 0)
            {
                return -1;
            }

            if (deepest)
            {
                return deep;
            }

            List<int> chain = scene.Chain(deep);
            FigBoardItem top = scene.Items[chain[0]];
            int level = (top.Type == "FRAME" || top.Type == "SECTION") && chain.Count > 1 ? 1 : 0;

            foreach (string id in boardSelection)
            {
                FigBoardItem selected = scene.Find(id);
                if (selected == null)
                {
                    continue;
                }

                int inChain = chain.IndexOf(selected.Index);
                if (inChain >= 0)
                {
                    return chain[doubleClick ? Mathf.Min(inChain + 1, chain.Count - 1) : inChain];
                }

                int parentInChain = chain.IndexOf(selected.ParentIndex);
                if (parentInChain >= 0)
                {
                    level = Mathf.Max(level, Mathf.Min(parentInChain + 1, chain.Count - 1));
                }
            }

            return chain[Mathf.Min(level, chain.Count - 1)];
        }

        // ─── Input ───────────────────────────────────────────────────────────────

        private void HandleBoardInput(Rect local)
        {
            Event e = Event.current;
            int id = boardControlId;
            bool inside = local.Contains(e.mousePosition);

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (!inside)
                    {
                        return;
                    }

                    GUIUtility.keyboardControl = id;
                    GUI.FocusControl(null);
                    GUIUtility.keyboardControl = id;
                    OnBoardMouseDown(e, id);
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        OnBoardMouseDrag(e);
                        e.Use();
                    }

                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        OnBoardMouseUp(e);
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }

                    break;
                case EventType.MouseMove:
                    if (inside)
                    {
                        UpdateHover(e.mousePosition, e.control || e.command);
                    }

                    break;
                case EventType.ScrollWheel:
                    if (!inside)
                    {
                        return;
                    }

                    if (e.control || e.command || e.alt)
                    {
                        ZoomAt(e.mousePosition, Mathf.Pow(1.0015f, -e.delta.y * 40f));
                    }
                    else
                    {
                        Vector2 delta = e.shift ? new Vector2(e.delta.y, e.delta.x) : e.delta;
                        viewOffset -= delta * 14f;
                    }

                    e.Use();
                    Repaint();
                    break;
                case EventType.KeyDown:
                    if (GUIUtility.keyboardControl == id || (inside && GUIUtility.keyboardControl == 0))
                    {
                        OnBoardKey(e);
                    }

                    break;
                case EventType.KeyUp:
                    if (e.keyCode == KeyCode.Space)
                    {
                        spaceHeld = false;
                        Repaint();
                    }

                    break;
                case EventType.ValidateCommand:
                    if (GUIUtility.keyboardControl == id && BoardCommand(e.commandName, false))
                    {
                        e.Use();
                    }

                    break;
                case EventType.ExecuteCommand:
                    if (GUIUtility.keyboardControl == id && BoardCommand(e.commandName, true))
                    {
                        e.Use();
                    }

                    break;
            }
        }

        private void UpdateHover(Vector2 mouse, bool deepest)
        {
            if (drag != BoardDrag.None)
            {
                return;
            }

            int deep = scene.HitTest(ToWorld(mouse));
            int target = ResolveTarget(deep, false, deepest);
            string id = target >= 0 ? scene.Items[target].Id : null;
            if (id != hoverId)
            {
                hoverId = id;
                Repaint();
            }
        }

        private void OnBoardMouseDown(Event e, int id)
        {
            dragStartMouse = e.mousePosition;
            dragStartWorld = ToWorld(e.mousePosition);
            activeGuides.Clear();

            if (e.button == 2 || (e.button == 0 && spaceHeld))
            {
                drag = BoardDrag.Pan;
                GUIUtility.hotControl = id;
                e.Use();
                return;
            }

            if (e.button == 1)
            {
                int hit = ResolveTarget(scene.HitTest(dragStartWorld), false, e.control || e.command);
                if (hit >= 0 && !boardSelection.Contains(scene.Items[hit].Id))
                {
                    SetBoardSelection(scene.Items[hit].Id, false);
                }

                ShowBoardMenu();
                e.Use();
                return;
            }

            if (e.button != 0)
            {
                return;
            }

            GUIUtility.hotControl = id;
            e.Use();

            int handle = HitHandle(e.mousePosition);
            if (handle >= 0)
            {
                BeginResize(handle);
                return;
            }

            string titleHit = HitFrameTitle(e.mousePosition);
            int deep = titleHit != null ? scene.Find(titleHit)?.Index ?? -1 : scene.HitTest(dragStartWorld);
            int target = titleHit != null ? deep : ResolveTarget(deep, e.clickCount == 2, e.control || e.command);

            if (target < 0)
            {
                // Empty canvas: a marquee (adding to the selection with Shift).
                marqueeBase.Clear();
                if (e.shift)
                {
                    marqueeBase.UnionWith(boardSelection);
                }
                else
                {
                    SetBoardSelection(null, false);
                }

                marqueeContainer = -1;
                pendingMarquee = true;
                drag = BoardDrag.Pending;
                return;
            }

            FigBoardItem item = scene.Items[target];
            if (e.clickCount == 2 && item.Type == "TEXT" && boardSelection.Contains(item.Id))
            {
                // Double-clicking selected text edits it in the properties panel.
                boardSidebar = true;
                focusTextField = true;
            }

            bool wasSelected = boardSelection.Contains(item.Id);
            pendingMarquee = false;
            if (titleHit == null && item.TopLevel && deep == target && !wasSelected && item.Node.Children.Count > 0 &&
                (item.Type == "FRAME" || item.Type == "SECTION"))
            {
                // An unselected top-level frame's background: click selects it, drag draws a marquee inside it.
                pendingMarquee = true;
                marqueeContainer = target;
                marqueeBase.Clear();
            }

            if (e.shift)
            {
                SetBoardSelection(item.Id, true);
            }
            else if (!wasSelected)
            {
                SetBoardSelection(item.Id, false);
            }
            else
            {
                Select(item.Id);
            }

            drag = BoardDrag.Pending;
        }

        private void OnBoardMouseDrag(Event e)
        {
            switch (drag)
            {
                case BoardDrag.Pan:
                    viewOffset += e.delta;
                    Repaint();
                    return;
                case BoardDrag.Pending:
                    if ((e.mousePosition - dragStartMouse).magnitude < DragThreshold)
                    {
                        return;
                    }

                    if (pendingMarquee)
                    {
                        drag = BoardDrag.Marquee;
                        if (marqueeContainer >= 0)
                        {
                            SetBoardSelection(null, false);
                        }
                    }
                    else if (boardSelection.Count > 0)
                    {
                        BeginMove();
                    }
                    else
                    {
                        drag = BoardDrag.None;
                        return;
                    }

                    break;
            }

            Vector2 world = ToWorld(e.mousePosition);
            switch (drag)
            {
                case BoardDrag.Move:
                    UpdateMove(world, e.shift, !(e.control || e.command) && smartGuides);
                    break;
                case BoardDrag.Resize:
                    UpdateResize(world, e.shift, e.alt);
                    break;
                case BoardDrag.Marquee:
                    UpdateMarquee(world);
                    break;
            }

            Repaint();
        }

        private void OnBoardMouseUp(Event e)
        {
            switch (drag)
            {
                case BoardDrag.Pending:
                    // A plain click on one of several selected layers keeps just that one.
                    if (!pendingMarquee && !e.shift && boardSelection.Count > 1 && selectedId != null)
                    {
                        SetBoardSelection(selectedId, false);
                    }

                    break;
                case BoardDrag.Move:
                    scene.UpdateContentBounds();
                    break;
                case BoardDrag.Resize:
                    EndResize();
                    break;
            }

            drag = BoardDrag.None;
            activeGuides.Clear();
            Repaint();
        }

        // ─── Move ────────────────────────────────────────────────────────────────

        private void BeginMove()
        {
            moveOrigins.Clear();
            bool fresh = BeginUndo("Move", null);
            Rect? bounds = null;
            foreach (string id in TopMostSelection())
            {
                FigBoardItem item = scene.Find(id);
                KiwiValue transform = item?.Node.Change.Get("transform");
                if (transform == null)
                {
                    continue;
                }

                if (fresh)
                {
                    undo.Capture(item.Node.Change, "transform");
                }

                moveOrigins.Add(new MoveOrigin
                {
                    Id = id,
                    Transform = transform,
                    X = transform.Get("m02")?.AsFloat ?? 0,
                    Y = transform.Get("m12")?.AsFloat ?? 0,
                    ParentInverse = item.ParentWorld.Inverse(),
                });
                bounds = bounds.HasValue ? FigGeometry.Union(bounds.Value, item.Bounds) : item.Bounds;
            }

            if (fresh)
            {
                CommitUndo("Move");
            }

            if (moveOrigins.Count == 0)
            {
                drag = BoardDrag.None;
                return;
            }

            drag = BoardDrag.Move;
            moveStartBounds = bounds ?? default;
            CollectGuideCandidates();
        }

        /// <summary>The selection without layers whose ancestor is also selected (they move with it).</summary>
        private List<string> TopMostSelection()
        {
            var result = new List<string>();
            foreach (string id in boardSelection)
            {
                FigBoardItem item = scene.Find(id);
                if (item == null)
                {
                    continue;
                }

                bool covered = false;
                for (int p = item.ParentIndex; p >= 0; p = scene.Items[p].ParentIndex)
                {
                    if (boardSelection.Contains(scene.Items[p].Id))
                    {
                        covered = true;
                        break;
                    }
                }

                if (!covered)
                {
                    result.Add(id);
                }
            }

            return result;
        }

        private void CollectGuideCandidates()
        {
            guideXs.Clear();
            guideYs.Clear();
            guideSources.Clear();
            FigBoardItem primary = scene.Find(moveOrigins[0].Id);
            int parent = primary.ParentIndex;
            var moving = new HashSet<int>();
            foreach (MoveOrigin o in moveOrigins)
            {
                moving.Add(scene.Find(o.Id).Index);
            }

            if (parent >= 0)
            {
                AddGuideSource(scene.Items[parent].Bounds);
            }

            int start = parent >= 0 ? parent + 1 : 0;
            int end = parent >= 0 ? scene.Items[parent].SubtreeEnd : scene.Items.Count;
            for (int i = start; i < end; i = scene.Items[i].SubtreeEnd)
            {
                if (!moving.Contains(i))
                {
                    AddGuideSource(scene.Items[i].Bounds);
                }
            }
        }

        private void AddGuideSource(Rect r)
        {
            guideSources.Add(r);
            guideXs.Add(r.xMin);
            guideXs.Add(r.center.x);
            guideXs.Add(r.xMax);
            guideYs.Add(r.yMin);
            guideYs.Add(r.center.y);
            guideYs.Add(r.yMax);
        }

        private void UpdateMove(Vector2 world, bool axisLock, bool guides)
        {
            Vector2 delta = world - dragStartWorld;
            if (axisLock)
            {
                if (Mathf.Abs(delta.x) > Mathf.Abs(delta.y))
                {
                    delta.y = 0;
                }
                else
                {
                    delta.x = 0;
                }
            }

            activeGuides.Clear();
            if (guides)
            {
                delta = SnapToGuides(delta);
            }

            foreach (MoveOrigin o in moveOrigins)
            {
                Vector2 local = o.ParentInverse.ApplyVector(delta);
                float x = o.X + local.x;
                float y = o.Y + local.y;
                if (snapToPixel)
                {
                    x = Mathf.Round(x);
                    y = Mathf.Round(y);
                }

                SetFloat(o.Transform, "m02", x);
                SetFloat(o.Transform, "m12", y);
                FigBoardItem item = scene.Find(o.Id);
                if (item != null)
                {
                    scene.RefreshPlacement(item.Index);
                }
            }

            MarkDirty();
        }

        private Vector2 SnapToGuides(Vector2 delta)
        {
            float threshold = GuideSnapPixels / viewScale;
            Rect moved = new Rect(moveStartBounds.position + delta, moveStartBounds.size);
            float[] xs = { moved.xMin, moved.center.x, moved.xMax };
            float[] ys = { moved.yMin, moved.center.y, moved.yMax };

            float bestX = threshold;
            float snapX = 0;
            float guideX = float.NaN;
            foreach (float x in xs)
            {
                foreach (float c in guideXs)
                {
                    float d = c - x;
                    if (Mathf.Abs(d) < bestX)
                    {
                        bestX = Mathf.Abs(d);
                        snapX = d;
                        guideX = c;
                    }
                }
            }

            float bestY = threshold;
            float snapY = 0;
            float guideY = float.NaN;
            foreach (float y in ys)
            {
                foreach (float c in guideYs)
                {
                    float d = c - y;
                    if (Mathf.Abs(d) < bestY)
                    {
                        bestY = Mathf.Abs(d);
                        snapY = d;
                        guideY = c;
                    }
                }
            }

            delta += new Vector2(snapX, snapY);
            moved = new Rect(moveStartBounds.position + delta, moveStartBounds.size);
            if (!float.IsNaN(guideX))
            {
                float y0 = moved.yMin, y1 = moved.yMax;
                foreach (Rect r in guideSources)
                {
                    if (Mathf.Abs(r.xMin - guideX) < 0.01f || Mathf.Abs(r.center.x - guideX) < 0.01f || Mathf.Abs(r.xMax - guideX) < 0.01f)
                    {
                        y0 = Mathf.Min(y0, r.yMin);
                        y1 = Mathf.Max(y1, r.yMax);
                    }
                }

                activeGuides.Add(new Guide { A = new Vector2(guideX, y0), B = new Vector2(guideX, y1) });
            }

            if (!float.IsNaN(guideY))
            {
                float x0 = moved.xMin, x1 = moved.xMax;
                foreach (Rect r in guideSources)
                {
                    if (Mathf.Abs(r.yMin - guideY) < 0.01f || Mathf.Abs(r.center.y - guideY) < 0.01f || Mathf.Abs(r.yMax - guideY) < 0.01f)
                    {
                        x0 = Mathf.Min(x0, r.xMin);
                        x1 = Mathf.Max(x1, r.xMax);
                    }
                }

                activeGuides.Add(new Guide { A = new Vector2(x0, guideY), B = new Vector2(x1, guideY) });
            }

            return delta;
        }

        // ─── Resize ──────────────────────────────────────────────────────────────

        /// <summary>Local-space positions of the 8 handles (0 TL, 1 T, 2 TR, 3 R, 4 BR, 5 B, 6 BL, 7 L).</summary>
        private static Vector2 HandleLocal(int handle, float w, float h)
        {
            switch (handle)
            {
                case 0: return new Vector2(0, 0);
                case 1: return new Vector2(w * 0.5f, 0);
                case 2: return new Vector2(w, 0);
                case 3: return new Vector2(w, h * 0.5f);
                case 4: return new Vector2(w, h);
                case 5: return new Vector2(w * 0.5f, h);
                case 6: return new Vector2(0, h);
                default: return new Vector2(0, h * 0.5f);
            }
        }

        private FigBoardItem ResizeTarget()
        {
            if (boardSelection.Count != 1)
            {
                return null;
            }

            FigBoardItem item = scene.Find(First(boardSelection));
            return item != null && item.Resizable && !item.Locked && item.Node.Change.Get("size") != null && item.Node.Change.Get("transform") != null ? item : null;
        }

        private int HitHandle(Vector2 mouse)
        {
            FigBoardItem item = ResizeTarget();
            if (item == null)
            {
                return -1;
            }

            for (int i = 0; i < 8; i++)
            {
                // Edge handles only when the box is big enough on screen to tell them apart.
                if (i % 2 == 1 && Mathf.Min(item.Width, item.Height) * item.World.Scale * viewScale < 24f)
                {
                    continue;
                }

                Vector2 p = ToCanvas(item.World.Apply(HandleLocal(i, item.Width, item.Height)));
                if (Mathf.Abs(mouse.x - p.x) <= HandleSize && Mathf.Abs(mouse.y - p.y) <= HandleSize)
                {
                    return i;
                }
            }

            return -1;
        }

        private void BeginResize(int handle)
        {
            FigBoardItem item = ResizeTarget();
            if (item == null)
            {
                return;
            }

            KiwiValue change = item.Node.Change;
            if (BeginUndo("Resize", null))
            {
                foreach (string f in new[] { "size", "transform", "fillGeometry", "strokeGeometry", "derivedTextData", "textAutoResize" })
                {
                    undo.Capture(change, f);
                }

                CommitUndo("Resize");
            }

            KiwiValue transform = change.Get("transform");
            resizeHandle = handle;
            resizeOrigin = new ResizeOrigin
            {
                Id = item.Id,
                Width = item.Width,
                Height = item.Height,
                X = transform.Get("m02")?.AsFloat ?? 0,
                Y = transform.Get("m12")?.AsFloat ?? 0,
                Local = FigAffine.FromMatrix(transform),
                WorldInverse = item.World.Inverse(),
            };
            drag = BoardDrag.Resize;
        }

        private void UpdateResize(Vector2 world, bool keepRatio, bool fromCenter)
        {
            FigBoardItem item = scene.Find(resizeOrigin.Id);
            if (item == null)
            {
                return;
            }

            ResizeOrigin o = resizeOrigin;
            Vector2 p = o.WorldInverse.Apply(world);
            int h = resizeHandle;
            bool left = h == 0 || h == 6 || h == 7;
            bool right = h == 2 || h == 3 || h == 4;
            bool top = h == 0 || h == 1 || h == 2;
            bool bottom = h == 4 || h == 5 || h == 6;
            bool horizontal = left || right;
            bool vertical = top || bottom;

            // The new size along each axis the handle moves (the other keeps its size).
            float w = o.Width;
            float hgt = o.Height;
            if (horizontal)
            {
                w = left ? o.Width - p.x : p.x;
                if (fromCenter)
                {
                    w = Mathf.Abs(p.x - o.Width * 0.5f) * 2f;
                }
            }

            if (vertical)
            {
                hgt = top ? o.Height - p.y : p.y;
                if (fromCenter)
                {
                    hgt = Mathf.Abs(p.y - o.Height * 0.5f) * 2f;
                }
            }

            w = Mathf.Max(0.01f, w);
            hgt = Mathf.Max(0.01f, hgt);
            if (keepRatio && o.Width > 0 && o.Height > 0)
            {
                float ratio = o.Width / o.Height;
                if (horizontal && vertical)
                {
                    if (w / ratio >= hgt)
                    {
                        hgt = w / ratio;
                    }
                    else
                    {
                        w = hgt * ratio;
                    }
                }
                else if (horizontal)
                {
                    hgt = w / ratio;
                }
                else
                {
                    w = hgt * ratio;
                }
            }

            if (snapToPixel)
            {
                w = Mathf.Max(1f, Mathf.Round(w));
                hgt = Mathf.Max(1f, Mathf.Round(hgt));
            }

            // Where the new box starts in the old local space: the opposite edge stays put, or the
            // centre does (Alt, or the axis an edge handle scales for the ratio).
            float x0 = fromCenter || (!horizontal && keepRatio) ? (o.Width - w) * 0.5f : left ? o.Width - w : 0f;
            float y0 = fromCenter || (!vertical && keepRatio) ? (o.Height - hgt) * 0.5f : top ? o.Height - hgt : 0f;

            // Moving the local origin by (x0, y0) moves the layer by its own transform's linear part.
            Vector2 shift = o.Local.ApplyVector(new Vector2(x0, y0));
            float tx = o.X + shift.x;
            float ty = o.Y + shift.y;
            if (snapToPixel && item.World.AxisAligned)
            {
                tx = Mathf.Round(tx);
                ty = Mathf.Round(ty);
            }

            KiwiValue change = item.Node.Change;
            KiwiValue size = change.Get("size");
            SetFloat(size, "x", w);
            SetFloat(size, "y", hgt);
            KiwiValue transform = change.Get("transform");
            SetFloat(transform, "m02", tx);
            SetFloat(transform, "m12", ty);
            scene.RefreshPlacement(item.Index);
            MarkDirty();
        }

        /// <summary>
        /// After a resize, cached geometry no longer matches the new size: drop it so Figma rebuilds
        /// it from the size, and stop text from snapping back to its old auto size.
        /// </summary>
        private void EndResize()
        {
            FigBoardItem item = scene.Find(resizeOrigin.Id);
            if (item == null)
            {
                return;
            }

            KiwiValue change = item.Node.Change;
            change.Remove("fillGeometry");
            change.Remove("strokeGeometry");
            if (item.Type == "TEXT")
            {
                change.Remove("derivedTextData");
                bool heightChanged = !Mathf.Approximately(item.Height, resizeOrigin.Height);
                KiwiValue autoResize = change.Get("textAutoResize");
                if (autoResize != null && autoResize.Def != null)
                {
                    string mode = heightChanged ? "NONE" : "HEIGHT";
                    KiwiField value = autoResize.Def.Fields.Find(f => f.Name == mode);
                    if (value != null)
                    {
                        autoResize.Number = value.Value;
                    }
                }
            }

            scene.Refresh(item.Index, true);
            scene.UpdateContentBounds();
            resizeHandle = -1;
        }

        // ─── Marquee ─────────────────────────────────────────────────────────────

        private void UpdateMarquee(Vector2 world)
        {
            marqueeWorld = Rect.MinMaxRect(Mathf.Min(world.x, dragStartWorld.x), Mathf.Min(world.y, dragStartWorld.y),
                Mathf.Max(world.x, dragStartWorld.x), Mathf.Max(world.y, dragStartWorld.y));

            boardSelection.Clear();
            boardSelection.UnionWith(marqueeBase);
            int start = marqueeContainer >= 0 ? marqueeContainer + 1 : 0;
            int end = marqueeContainer >= 0 ? scene.Items[marqueeContainer].SubtreeEnd : scene.Items.Count;
            string last = null;
            for (int i = start; i < end; i = scene.Items[i].SubtreeEnd)
            {
                FigBoardItem item = scene.Items[i];
                if (!item.Locked && item.Bounds.Overlaps(marqueeWorld))
                {
                    boardSelection.Add(item.Id);
                    last = item.Id;
                }
            }

            selectedId = last ?? (boardSelection.Count > 0 ? First(boardSelection) : null);
        }

        // ─── Keyboard and commands ───────────────────────────────────────────────

        private void OnBoardKey(Event e)
        {
            if (!HandleBoardShortcut(e) && !HandleBoardPlainKey(e))
            {
                return;
            }

            e.Use();
            Repaint();
        }

        /// <summary>
        /// Ctrl/Cmd and Shift combinations. Returns true when the key was used. Ctrl/Cmd+Z and Y are
        /// left to Unity's Undo, which drives this history through <see cref="FigUndoProxy"/>.
        /// </summary>
        private bool HandleBoardShortcut(Event e)
        {
            bool mod = e.control || e.command;
            if (mod)
            {
                switch (e.keyCode)
                {
                    case KeyCode.D:
                        RequestBoardAction(DuplicateBoardSelection);
                        return true;
                    case KeyCode.A:
                        SelectAllSiblings();
                        return true;
                    case KeyCode.H:
                        if (!e.shift)
                        {
                            return false;
                        }

                        ToggleBoardVisibility();
                        return true;
                    default:
                        return false;
                }
            }

            if (e.shift)
            {
                switch (e.keyCode)
                {
                    case KeyCode.Alpha1:
                        viewNeedsFit = true;
                        return true;
                    case KeyCode.Alpha2:
                        ZoomToSelection();
                        return true;
                    case KeyCode.Alpha0:
                        ZoomAt(canvasSize * 0.5f, 1f / viewScale);
                        return true;
                }
            }

            return false;
        }

        private bool HandleBoardPlainKey(Event e)
        {
            switch (e.keyCode)
            {
                case KeyCode.Space:
                    spaceHeld = true;
                    return true;
                case KeyCode.Delete:
                case KeyCode.Backspace:
                    RequestBoardAction(DeleteBoardSelection);
                    return true;
                case KeyCode.F:
                    ZoomToSelection();
                    return true;
                case KeyCode.Escape:
                {
                    FigBoardItem item = scene.Find(selectedId);
                    if (item != null && item.ParentIndex >= 0)
                    {
                        SetBoardSelection(scene.Items[item.ParentIndex].Id, false);
                    }
                    else
                    {
                        SetBoardSelection(null, false);
                    }

                    return true;
                }
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                {
                    FigBoardItem item = scene.Find(selectedId);
                    if (item != null && item.Index + 1 < item.SubtreeEnd)
                    {
                        SetBoardSelection(scene.Items[item.Index + 1].Id, false);
                    }

                    return true;
                }
                case KeyCode.LeftArrow:
                case KeyCode.RightArrow:
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                {
                    float step = e.shift ? 10f : 1f;
                    Vector2 d = e.keyCode == KeyCode.LeftArrow ? new Vector2(-step, 0) :
                        e.keyCode == KeyCode.RightArrow ? new Vector2(step, 0) :
                        e.keyCode == KeyCode.UpArrow ? new Vector2(0, -step) : new Vector2(0, step);
                    Nudge(d);
                    return true;
                }
                default:
                    return false;
            }
        }

        /// <summary>Unity's own commands (Edit menu, F, Delete…) routed to the board while it has focus.</summary>
        private bool BoardCommand(string name, bool execute)
        {
            switch (name)
            {
                case "Delete":
                case "SoftDelete":
                    if (execute)
                    {
                        RequestBoardAction(DeleteBoardSelection);
                    }

                    return boardSelection.Count > 0;
                case "Duplicate":
                    if (execute)
                    {
                        RequestBoardAction(DuplicateBoardSelection);
                    }

                    return boardSelection.Count > 0;
                case "FrameSelected":
                    if (execute)
                    {
                        ZoomToSelection();
                    }

                    return true;
                case "SelectAll":
                    if (execute)
                    {
                        SelectAllSiblings();
                    }

                    return true;
                default:
                    return false;
            }
        }

        private Action pendingBoardAction;

        /// <summary>
        /// Run a destructive action once, after this event. Unity can deliver the same key both as a
        /// KeyDown and as an Edit command (Delete / SoftDelete, Duplicate); without this a single
        /// press would delete the selection and then its parent.
        /// </summary>
        private void RequestBoardAction(Action action)
        {
            if (pendingBoardAction != null)
            {
                return;
            }

            pendingBoardAction = action;
            Defer(() =>
            {
                Action run = pendingBoardAction;
                pendingBoardAction = null;
                run?.Invoke();
            });
        }

        private void Nudge(Vector2 worldDelta)
        {
            List<string> ids = TopMostSelection();
            if (ids.Count == 0)
            {
                return;
            }

            bool fresh = BeginUndo("Nudge", "nudge:" + string.Join(",", ids));
            foreach (string id in ids)
            {
                FigBoardItem item = scene.Find(id);
                KiwiValue transform = item?.Node.Change.Get("transform");
                if (transform == null || item.Locked)
                {
                    continue;
                }

                if (fresh)
                {
                    undo.Capture(item.Node.Change, "transform");
                }

                Vector2 local = item.ParentWorld.Inverse().ApplyVector(worldDelta);
                SetFloat(transform, "m02", (transform.Get("m02")?.AsFloat ?? 0) + local.x);
                SetFloat(transform, "m12", (transform.Get("m12")?.AsFloat ?? 0) + local.y);
                scene.RefreshPlacement(item.Index);
            }

            if (fresh)
            {
                CommitUndo("Nudge");
            }

            scene.UpdateContentBounds();
            MarkDirty();
        }

        private void SelectAllSiblings()
        {
            FigBoardItem item = scene.Find(selectedId);
            int parent = item?.ParentIndex ?? -1;
            int start = parent >= 0 ? parent + 1 : 0;
            int end = parent >= 0 ? scene.Items[parent].SubtreeEnd : scene.Items.Count;
            boardSelection.Clear();
            for (int i = start; i < end; i = scene.Items[i].SubtreeEnd)
            {
                if (!scene.Items[i].Locked)
                {
                    boardSelection.Add(scene.Items[i].Id);
                }
            }

            if (selectedId == null || !boardSelection.Contains(selectedId))
            {
                selectedId = First(boardSelection);
            }
        }

        private void ToggleBoardVisibility()
        {
            if (boardSelection.Count == 0 || !BeginUndo("Toggle Visibility", null))
            {
                return;
            }

            var ids = new List<string>(boardSelection);
            foreach (string id in ids)
            {
                undo.Capture(file.Find(id)?.Change, "visible");
            }

            CommitUndo("Toggle Visibility");
            foreach (string id in ids)
            {
                KiwiValue change = file.Find(id)?.Change;
                if (change == null)
                {
                    continue;
                }

                bool visible = change.Get("visible")?.AsBool ?? true;
                change.Set("visible", KiwiValue.FromBool(!visible));
            }

            MarkDirty();
            OnStructureChanged();
        }

        private void DeleteBoardSelection()
        {
            var ids = new List<string>(TopMostSelection());
            ids.RemoveAll(id => file.Find(id)?.Parent == null || file.Find(id).Type == "CANVAS");
            if (ids.Count == 0)
            {
                return;
            }

            if (ids.Exists(id => file.Find(id)?.Type == "SYMBOL") &&
                !EditorUtility.DisplayDialog("Fig Editor", "The selection includes a component: its instances will lose their main component.", "Delete", "Cancel"))
            {
                return;
            }

            RecordStructure(ids.Count == 1 ? "Delete " + file.Find(ids[0]).Label : $"Delete {ids.Count} layers");
            string parent = file.Find(ids[0]).Parent?.Id;
            foreach (string id in ids)
            {
                FigNode node = file.Find(id);
                if (node != null)
                {
                    file.Delete(node);
                }
            }

            MarkDirty();
            OnStructureChanged();
            boardSelection.Clear();
            FigNode parentNode = file.Find(parent);
            selectedId = parentNode != null && parentNode.Type != "CANVAS" ? parent : null;
        }

        private void DuplicateBoardSelection()
        {
            var ids = new List<string>(TopMostSelection());
            ids.RemoveAll(id => file.Find(id)?.Parent == null || file.Find(id).Type == "CANVAS");
            if (ids.Count == 0)
            {
                return;
            }

            RecordStructure(ids.Count == 1 ? "Duplicate " + file.Find(ids[0]).Label : $"Duplicate {ids.Count} layers");
            var copies = new List<string>();
            foreach (string id in ids)
            {
                FigNode node = file.Find(id);
                FigNode copy = node != null ? file.Duplicate(node) : null;
                if (copy != null)
                {
                    copies.Add(copy.Id);
                }
            }

            MarkDirty();
            OnStructureChanged();
            boardSelection.Clear();
            boardSelection.UnionWith(copies);
            selectedId = copies.Count > 0 ? copies[copies.Count - 1] : null;
        }

        private void ShowBoardMenu()
        {
            var menu = new GenericMenu();
            bool any = boardSelection.Count > 0;
            AddMenuItem(menu, "Duplicate _%d", any, () => RequestBoardAction(DuplicateBoardSelection));
            AddMenuItem(menu, "Delete", any, () => RequestBoardAction(DeleteBoardSelection));
            menu.AddSeparator("");
            AddMenuItem(menu, "Bring Forward", boardSelection.Count == 1, () => Defer(() => MoveSelectedInOrder(1)));
            AddMenuItem(menu, "Send Backward", boardSelection.Count == 1, () => Defer(() => MoveSelectedInOrder(-1)));
            AddMenuItem(menu, "Show/Hide", any, ToggleBoardVisibility);
            menu.AddSeparator("");
            AddMenuItem(menu, "Select Parent", scene.Find(selectedId)?.ParentIndex >= 0, () =>
            {
                FigBoardItem item = scene.Find(selectedId);
                SetBoardSelection(scene.Items[item.ParentIndex].Id, false);
            });
            AddMenuItem(menu, "Zoom to Selection", any, ZoomToSelection);
            AddMenuItem(menu, "Zoom to Fit", true, () => viewNeedsFit = true);
            AddMenuItem(menu, "Copy GUID", selectedId != null, () => EditorGUIUtility.systemCopyBuffer = selectedId);
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

        /// <summary>Paint order: later siblings draw on top, so "forward" is the next sibling.</summary>
        private void MoveSelectedInOrder(int delta)
        {
            FigNode node = file.Find(selectedId);
            if (node != null)
            {
                MoveNode(node, delta);
            }
        }

        // ─── Overlays ────────────────────────────────────────────────────────────

        private void DrawBoardOverlays(Rect local)
        {
            frameTitles.Clear();
            DrawFrameTitles(local);

            // Hover outline (not for what's already selected).
            FigBoardItem hover = drag == BoardDrag.None && hoverId != null && !boardSelection.Contains(hoverId) ? scene.Find(hoverId) : null;
            if (hover != null)
            {
                DrawItemOutline(hover, 1.5f, AccentColor, local);
            }

            foreach (string id in boardSelection)
            {
                FigBoardItem item = scene.Find(id);
                if (item != null)
                {
                    DrawItemOutline(item, 1.5f, AccentColor, local);
                }
            }

            Rect? bounds = SelectionBounds();
            if (boardSelection.Count > 1 && bounds.HasValue)
            {
                Rect r = ToCanvas(bounds.Value);
                DrawRectOutline(r, 1f, AccentColor, local);
            }

            foreach (Guide g in activeGuides)
            {
                boardRenderer.OverlayLine(new[] { ToCanvas(g.A), ToCanvas(g.B) }, 2, false, 1f, GuideColor, local);
            }

            if (drag == BoardDrag.Marquee)
            {
                Rect r = ToCanvas(marqueeWorld);
                boardRenderer.OverlayRect(r, AccentFill, local);
                DrawRectOutline(r, 1f, AccentColor, local);
            }

            FigBoardItem resizable = drag == BoardDrag.Move ? null : ResizeTarget();
            if (resizable != null)
            {
                for (int i = 0; i < 8; i++)
                {
                    if (i % 2 == 1 && Mathf.Min(resizable.Width, resizable.Height) * resizable.World.Scale * viewScale < 24f)
                    {
                        continue;
                    }

                    Vector2 p = ToCanvas(resizable.World.Apply(HandleLocal(i, resizable.Width, resizable.Height)));
                    var handle = new Rect(p.x - HandleSize * 0.5f, p.y - HandleSize * 0.5f, HandleSize, HandleSize);
                    boardRenderer.OverlayRect(handle, AccentColor, local);
                    boardRenderer.OverlayRect(new Rect(handle.x + 1, handle.y + 1, handle.width - 2, handle.height - 2), HandleFill, local);
                }
            }

            boardRenderer.FlushOverlay();

            if (bounds.HasValue)
            {
                DrawSizeBadge(ToCanvas(bounds.Value));
            }
        }

        private void DrawItemOutline(FigBoardItem item, float width, Color color, Rect clip)
        {
            var pts = new Vector2[item.Outline.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                pts[i] = ToCanvas(item.Outline[i]);
            }

            bool line = item.Type == "LINE";
            boardRenderer.OverlayLine(pts, line ? 2 : pts.Length, !line, width, color, clip);
        }

        private void DrawRectOutline(Rect r, float width, Color color, Rect clip)
        {
            var pts = new[] { new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax) };
            boardRenderer.OverlayLine(pts, 4, true, width, color, clip);
        }

        private GUIStyle titleStyle;
        private GUIStyle badgeStyle;

        private void DrawFrameTitles(Rect local)
        {
            titleStyle ??= new GUIStyle(EditorStyles.miniLabel) { clipping = TextClipping.Clip, alignment = TextAnchor.LowerLeft, padding = new RectOffset(0, 0, 0, 1) };
            Color text = scene.Background.grayscale > 0.5f ? new Color(0, 0, 0, 0.6f) : new Color(1, 1, 1, 0.6f);
            foreach (FigBoardItem item in scene.Items)
            {
                if (!item.TopLevel || !(item.IsContainer && item.Type != "GROUP" && item.Type != "BOOLEAN_OPERATION"))
                {
                    continue;
                }

                Rect r = ToCanvas(item.Bounds);
                if (r.width < 24f || r.xMax < 0 || r.xMin > local.width || r.yMin - 18 > local.height || r.yMin < -200)
                {
                    continue;
                }

                var title = new Rect(r.x, r.y - 17, Mathf.Max(24f, r.width), 16);
                frameTitles.Add(new FrameTitle { Rect = title, Id = item.Id });
                titleStyle.normal.textColor = boardSelection.Contains(item.Id) ? AccentColor : text;
                GUI.Label(title, item.Node.Label, titleStyle);
            }
        }

        private string HitFrameTitle(Vector2 mouse)
        {
            for (int i = frameTitles.Count - 1; i >= 0; i--)
            {
                // Only the text part counts, not the whole frame width.
                Rect r = frameTitles[i].Rect;
                float textWidth = titleStyle != null ? titleStyle.CalcSize(new GUIContent(scene.Find(frameTitles[i].Id)?.Node.Label ?? "")).x : r.width;
                if (new Rect(r.x, r.y, Mathf.Min(r.width, textWidth + 4), r.height).Contains(mouse))
                {
                    return frameTitles[i].Id;
                }
            }

            return null;
        }

        private void DrawSizeBadge(Rect selection)
        {
            badgeStyle ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white }, padding = new RectOffset(4, 4, 0, 0) };
            string text;
            FigBoardItem single = boardSelection.Count == 1 ? scene.Find(First(boardSelection)) : null;
            Rect? world = SelectionBounds();
            if (single != null)
            {
                text = FormatNumber(single.Width) + " × " + FormatNumber(single.Height);
            }
            else if (world.HasValue)
            {
                text = FormatNumber(world.Value.width) + " × " + FormatNumber(world.Value.height);
            }
            else
            {
                return;
            }

            Vector2 size = badgeStyle.CalcSize(new GUIContent(text));
            var badge = new Rect(selection.center.x - size.x * 0.5f, selection.yMax + 6, size.x, 16);
            EditorGUI.DrawRect(badge, AccentColor);
            GUI.Label(badge, text, badgeStyle);
        }

        private static string FormatNumber(float v)
        {
            return Mathf.Abs(v - Mathf.Round(v)) < 0.005f ? Mathf.Round(v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        // ─── Images for the renderer ─────────────────────────────────────────────

        /// <summary>The texture of image <paramref name="hex"/>; decodes it only when <paramref name="allowDecode"/>.</summary>
        private Texture2D BoardImage(string hex, bool allowDecode)
        {
            if (textures.TryGetValue(hex, out Texture2D cached))
            {
                return cached;
            }

            return allowDecode ? ImageTexture(hex) : null;
        }
    }
}
