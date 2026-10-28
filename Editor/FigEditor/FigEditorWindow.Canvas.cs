using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// The canvas: the current page rendered by <see cref="FigTileCache"/>, operated like Figma.
    ///
    /// Navigation: wheel scrolls (Shift: sideways), Ctrl/Cmd/Alt + wheel zooms at the cursor,
    /// Space-drag, middle-drag or the Hand tool (H) pans; Shift+1 fits the page, Shift+2 / F the
    /// selection, Shift+0 is 100%.
    ///
    /// Selection: a click selects at the current level (a top-level layer, or a top-level frame's
    /// direct child; next to the selection, its siblings); double-click goes a level deeper (on
    /// text: edits it); Ctrl/Cmd-click selects the deepest layer; Shift-click adds; dragging on empty
    /// canvas (or an unselected top-level frame's background) draws a marquee; Esc / Enter go up / down.
    ///
    /// Editing: drag to move (Shift locks the axis; smart guides snap to siblings and the parent,
    /// Ctrl/Cmd disables them), handles resize (Shift keeps the ratio, Alt from the centre), arrows
    /// nudge (Shift: 10), Delete, Ctrl/Cmd+D duplicate, Ctrl/Cmd+Shift+H show / hide. Tools: Move (V),
    /// Hand (H), Frame (F), Rectangle (R), Ellipse (O), Line (L), Text (T) — drag to draw.
    /// </summary>
    public partial class FigEditorWindow
    {
        private const float HandleSize = 8f;
        private const float DragThreshold = 3f;
        private const float GuideSnapPixels = 6f;
        private const float MinScale = 0.01f;
        private const float MaxScale = 256f;

        private static readonly Color GuideColor = new Color(0.95f, 0.28f, 0.13f, 1f);

        [SerializeField] private string boardPageId;
        [SerializeField] private bool snapToPixel = true;
        [SerializeField] private bool smartGuides = true;

        private FigScene scene;
        private bool boardDirty = true;
        private readonly HashSet<string> boardChangedIds = new();
        private FigTileCache tileCache;
        private float viewScale = 1f;
        private Vector2 viewOffset;
        private bool viewNeedsFit = true;
        private string pendingZoomTo;
        private Vector2 canvasSize;

        private readonly HashSet<string> boardSelection = new();
        private string hoverId;
        private bool spaceHeld;
        private int canvasControlId;

        private enum Tool
        {
            Move,
            Hand,
            Frame,
            Rectangle,
            Ellipse,
            Line,
            Text,
        }

        private Tool tool = Tool.Move;

        private enum Drag
        {
            None,
            Pending,
            Move,
            Resize,
            Marquee,
            Pan,
            Create,
        }

        private Drag drag;
        private Vector2 dragStartMouse;
        private Vector2 dragStartWorld;
        private Vector2 dragWorld;
        private bool pendingMarquee;
        private int marqueeContainer = -1;
        private Rect marqueeWorld;
        private readonly HashSet<string> marqueeBase = new();
        private int resizeHandle = -1;

        private struct MoveOrigin
        {
            public string Id;
            public int Index;
            public KiwiValue Transform;
            public float X;
            public float Y;
            public FigAffine ParentInverse;
        }

        private readonly List<MoveOrigin> moveOrigins = new();
        private Rect moveStartBounds;
        private Vector2 moveDelta;
        private readonly HashSet<int> liftExclude = new();
        private Rect liftSettleRegion;
        private bool liftSettling;
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
        private string editingTextId;
        private string editingText;
        private readonly Dictionary<string, Font> osFonts = new();
        private GUIStyle overlayText;

        // ─── Lifecycle ───────────────────────────────────────────────────────────

        private void EnableCanvas()
        {
            wantsMouseMove = true;
            // Text without Figma's glyph outlines is laid out with the installed fonts, the
            // project's own font files included; the font index is cached in Library.
            string assets = Application.dataPath;
            if (!FigFonts.ExtraFolders.Contains(assets))
            {
                FigFonts.ExtraFolders.Add(assets);
            }

            FigFonts.CacheFile = Path.Combine(Path.GetDirectoryName(assets) ?? ".", "Library", "FigEditorFonts.tsv");
            tileCache ??= new FigTileCache();
            tileCache.ImageBytes = hash => file?.Member(FigFile.ImagePrefix + hash)?.Data;
            if (undoProxy == null)
            {
                undoProxy = CreateInstance<FigUndoProxy>();
                undoProxy.hideFlags = HideFlags.HideAndDontSave;
            }

            Undo.undoRedoPerformed += OnUnityUndoRedo;
            EditorApplication.modifierKeysChanged += Repaint;
        }

        private void DisableCanvas()
        {
            Undo.undoRedoPerformed -= OnUnityUndoRedo;
            EditorApplication.modifierKeysChanged -= Repaint;
            tileCache?.Dispose();
            tileCache = null;
            if (undoProxy != null)
            {
                Undo.ClearUndo(undoProxy);
                DestroyImmediate(undoProxy);
                undoProxy = null;
            }
        }

        /// <summary>A freshly loaded file: no history, no caches, fit the view.</summary>
        private void ResetCanvas()
        {
            undo.Clear();
            if (undoProxy != null)
            {
                Undo.ClearUndo(undoProxy);
                undoProxy.Revision = 0;
            }

            tileCache?.ClearImages();
            tileCache?.SetScene(null, false);
            scene = null;
            boardDirty = true;
            boardSelection.Clear();
            viewNeedsFit = true;
            editingTextId = null;
        }

        /// <summary>The scene must be rebuilt (structure, visibility or many layers changed).</summary>
        private void InvalidateBoard()
        {
            boardDirty = true;
        }

        /// <summary>Only <paramref name="id"/> changed (placement or paint): patch it in the scene.</summary>
        private void InvalidateBoardNode(string id)
        {
            if (id != null)
            {
                boardChangedIds.Add(id);
            }
        }

        // ─── Pages and scene ─────────────────────────────────────────────────────

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

        private FigNode ResolvePage()
        {
            FigNode selectedPage = PageOf(Selected);
            if (selectedPage != null && selectedPage.Id != boardPageId)
            {
                string keep = selectedId;
                SwitchPage(selectedPage.Id);
                selectedId = keep;
                if (Selected != null && Selected != selectedPage)
                {
                    pendingZoomTo = selectedId;
                }
            }

            FigNode page = file.Find(boardPageId);
            if (page == null || page.Type != "CANVAS")
            {
                List<FigNode> pages = Pages();
                page = pages.Count > 0 ? pages[0] : null;
                boardPageId = page?.Id;
                viewNeedsFit = true;
                rowsDirty = true;
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
            rowsDirty = true;
            editingTextId = null;
        }

        private void EnsureScene(FigNode page)
        {
            bool samePage = scene != null && scene.Page?.Id == page.Id;
            if (scene == null || boardDirty || !samePage || scene.Page != page)
            {
                scene = FigScene.Build(file, page);
                tileCache.SetScene(scene, samePage);
                boardDirty = false;
                boardChangedIds.Clear();
                rowsDirty = true;
                return;
            }

            if (boardChangedIds.Count > 0)
            {
                foreach (string id in boardChangedIds)
                {
                    FigSceneNode n = scene.Find(id);
                    if (n != null)
                    {
                        Rect dirty = scene.Refresh(n.Index, true);
                        tileCache.UpdateScene(scene, dirty);
                    }
                    else
                    {
                        // An instance internal (or a layer the scene doesn't hold) changed: rebuild.
                        scene = FigScene.Build(file, page);
                        tileCache.SetScene(scene, true);
                        break;
                    }
                }

                boardChangedIds.Clear();
            }
        }

        /// <summary>Re-read layer <paramref name="index"/> into the scene and re-render where it was and is.</summary>
        private void RefreshNode(int index, bool paint)
        {
            Rect dirty = scene.Refresh(index, paint);
            tileCache.UpdateScene(scene, dirty);
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

        private void ZoomTo(float scale)
        {
            ZoomAt(canvasSize * 0.5f, scale / viewScale);
        }

        private void ZoomToRect(Rect world, float maxScale)
        {
            if (canvasSize.x <= 0 || canvasSize.y <= 0)
            {
                return;
            }

            const float pad = 64f;
            float w = Mathf.Max(1f, world.width);
            float h = Mathf.Max(1f, world.height);
            viewScale = Mathf.Clamp(Mathf.Min((canvasSize.x - 2 * pad) / w, (canvasSize.y - 2 * pad - 60) / h), MinScale, maxScale);
            viewOffset = new Vector2(canvasSize.x * 0.5f, (canvasSize.y - 60) * 0.5f) - world.center * viewScale;
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

        private void ShowZoomMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Zoom to Fit   Shift+1"), false, () => viewNeedsFit = true);
            menu.AddItem(new GUIContent("Zoom to Selection   Shift+2"), false, ZoomToSelection);
            menu.AddSeparator("");
            foreach (float z in new[] { 0.25f, 0.5f, 1f, 2f, 4f, 8f })
            {
                float zoom = z;
                menu.AddItem(new GUIContent(Mathf.RoundToInt(z * 100) + "%"), Mathf.Approximately(viewScale, z), () => ZoomTo(zoom));
            }

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Snap to Pixel Grid"), snapToPixel, () => snapToPixel = !snapToPixel);
            menu.AddItem(new GUIContent("Smart Guides"), smartGuides, () => smartGuides = !smartGuides);
            menu.ShowAsContext();
        }

        private Rect? SelectionBounds(bool withMove = false)
        {
            Rect? bounds = null;
            foreach (string id in boardSelection)
            {
                FigSceneNode n = scene?.Find(id);
                if (n != null)
                {
                    Rect b = n.Bounds;
                    if (withMove && drag == Drag.Move && IsMoving(n.Index))
                    {
                        b.position += moveDelta;
                    }

                    bounds = bounds.HasValue ? FigGeometry.Union(bounds.Value, b) : b;
                }
            }

            return bounds;
        }

        private bool IsMoving(int index)
        {
            foreach (MoveOrigin o in moveOrigins)
            {
                if (scene.IsAncestor(o.Index, index))
                {
                    return true;
                }
            }

            return false;
        }

        // ─── Canvas ──────────────────────────────────────────────────────────────

        private void DrawCanvas(Rect canvasRect)
        {
            FigNode page = ResolvePage();
            if (page == null)
            {
                GUI.Label(canvasRect, "This file has no pages.", FigTheme.Centered);
                return;
            }

            EnsureScene(page);
            canvasControlId = GUIUtility.GetControlID(FocusType.Keyboard);
            canvasSize = canvasRect.size;
            SyncSelectionFromTree();

            if (viewNeedsFit && Event.current.type == EventType.Layout)
            {
                viewNeedsFit = false;
                ZoomToRect(scene.ContentBounds, 1f);
            }

            if (pendingZoomTo != null && Event.current.type == EventType.Layout)
            {
                FigSceneNode target = scene.Find(pendingZoomTo);
                pendingZoomTo = null;
                if (target != null)
                {
                    ZoomToRect(target.Bounds, Mathf.Max(viewScale, 1f));
                }
            }

            GUI.BeginClip(canvasRect);
            var local = new Rect(0, 0, canvasRect.width, canvasRect.height);
            var toolbar = ToolbarRect(local);
            HandleCanvasInput(local, toolbar);

            bool pending = false;
            if (Event.current.type == EventType.Repaint)
            {
                bool lifted = liftExclude.Count > 0 && (drag == Drag.Move || liftSettling);
                pending = tileCache.Draw(canvasSize, viewScale, viewOffset, lifted ? liftExclude : null);
                if (lifted)
                {
                    tileCache.DrawLift(viewScale, viewOffset, moveDelta);
                    if (liftSettling && drag != Drag.Move && tileCache.RegionReady(liftSettleRegion, viewScale))
                    {
                        // The moved layers are rendered in place: drop the lifted copy.
                        liftSettling = false;
                        liftExclude.Clear();
                        tileCache.ClearLift();
                        tileCache.DropLayer1();
                        moveDelta = Vector2.zero;
                    }

                    pending = true;
                }

                DrawFallbackText(local);
                DrawCanvasOverlays(local);
            }

            DrawTextEditor();
            DrawToolbar(toolbar);
            if (spaceHeld || drag == Drag.Pan || tool == Tool.Hand)
            {
                EditorGUIUtility.AddCursorRect(local, MouseCursor.Pan);
            }
            else if (tool != Tool.Move)
            {
                EditorGUIUtility.AddCursorRect(local, MouseCursor.ArrowPlus);
            }

            GUI.EndClip();
            if (pending)
            {
                Repaint();
            }
        }

        /// <summary>A selection made in the layers panel becomes the canvas selection.</summary>
        private void SyncSelectionFromTree()
        {
            if (selectedId == null)
            {
                if (boardSelection.Count > 0 && drag == Drag.None)
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

        private FigSceneNode SelectedSceneNode => scene?.Find(selectedId);

        // ─── Selection rules ─────────────────────────────────────────────────────

        /// <summary>
        /// Which layer a click on <paramref name="deep"/> selects: a top-level layer, or a top-level
        /// frame's direct child; inside or beside the selection, the same level; a level deeper on
        /// double-click; the deepest with Ctrl/Cmd.
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
            FigSceneNode top = scene.Nodes[chain[0]];
            int level = (top.Type == "FRAME" || top.Type == "SECTION") && chain.Count > 1 ? 1 : 0;
            foreach (string id in boardSelection)
            {
                FigSceneNode selected = scene.Find(id);
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

        private void HandleCanvasInput(Rect local, Rect toolbar)
        {
            Event e = Event.current;
            int id = canvasControlId;
            bool inside = local.Contains(e.mousePosition) && !toolbar.Contains(e.mousePosition);
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (!inside)
                    {
                        return;
                    }

                    if (editingTextId != null)
                    {
                        CommitTextEdit();
                    }

                    GUI.FocusControl(null);
                    GUIUtility.keyboardControl = id;
                    OnCanvasMouseDown(e, id);
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        OnCanvasMouseDrag(e);
                        e.Use();
                    }

                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        OnCanvasMouseUp(e);
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
                    if (editingTextId == null && (GUIUtility.keyboardControl == id || (inside && GUIUtility.keyboardControl == 0)))
                    {
                        OnCanvasKey(e);
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
                    if (GUIUtility.keyboardControl == id && CanvasCommand(e.commandName, false))
                    {
                        e.Use();
                    }

                    break;
                case EventType.ExecuteCommand:
                    if (GUIUtility.keyboardControl == id && CanvasCommand(e.commandName, true))
                    {
                        e.Use();
                    }

                    break;
            }
        }

        private void UpdateHover(Vector2 mouse, bool deepest)
        {
            if (drag != Drag.None)
            {
                return;
            }

            int target = ResolveTarget(scene.HitTest(ToWorld(mouse)), false, deepest);
            string id = target >= 0 ? scene.Nodes[target].Id : null;
            if (id != hoverId)
            {
                hoverId = id;
                Repaint();
            }
        }

        private void OnCanvasMouseDown(Event e, int id)
        {
            dragStartMouse = e.mousePosition;
            dragStartWorld = ToWorld(e.mousePosition);
            dragWorld = dragStartWorld;
            activeGuides.Clear();

            if (e.button == 2 || (e.button == 0 && (spaceHeld || tool == Tool.Hand)))
            {
                drag = Drag.Pan;
                GUIUtility.hotControl = id;
                e.Use();
                return;
            }

            if (e.button == 1)
            {
                int hit = ResolveTarget(scene.HitTest(dragStartWorld), false, e.control || e.command);
                if (hit >= 0 && !boardSelection.Contains(scene.Nodes[hit].Id))
                {
                    SetBoardSelection(scene.Nodes[hit].Id, false);
                }

                ShowCanvasMenu();
                e.Use();
                return;
            }

            if (e.button != 0)
            {
                return;
            }

            GUIUtility.hotControl = id;
            e.Use();

            if (tool != Tool.Move)
            {
                drag = Drag.Create;
                return;
            }

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
                drag = Drag.Pending;
                return;
            }

            FigSceneNode node = scene.Nodes[target];
            if (e.clickCount == 2 && node.Type == "TEXT" && boardSelection.Contains(node.Id) && !node.Expanded)
            {
                BeginTextEdit(node);
                drag = Drag.None;
                GUIUtility.hotControl = 0;
                return;
            }

            bool wasSelected = boardSelection.Contains(node.Id);
            pendingMarquee = false;
            if (titleHit == null && node.ParentIndex < 0 && deep == target && !wasSelected && node.SubtreeEnd > node.Index + 1 &&
                (node.Type == "FRAME" || node.Type == "SECTION"))
            {
                pendingMarquee = true;
                marqueeContainer = target;
                marqueeBase.Clear();
            }

            if (e.shift)
            {
                SetBoardSelection(node.Id, true);
            }
            else if (!wasSelected)
            {
                SetBoardSelection(node.Id, false);
            }
            else
            {
                Select(node.Id);
            }

            drag = Drag.Pending;
        }

        private void OnCanvasMouseDrag(Event e)
        {
            Vector2 world = ToWorld(e.mousePosition);
            dragWorld = world;
            switch (drag)
            {
                case Drag.Pan:
                    viewOffset += e.delta;
                    Repaint();
                    return;
                case Drag.Create:
                    Repaint();
                    return;
                case Drag.Pending:
                    if ((e.mousePosition - dragStartMouse).magnitude < DragThreshold)
                    {
                        return;
                    }

                    if (pendingMarquee)
                    {
                        drag = Drag.Marquee;
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
                        drag = Drag.None;
                        return;
                    }

                    break;
            }

            switch (drag)
            {
                case Drag.Move:
                    UpdateMove(world, e.shift, !(e.control || e.command) && smartGuides);
                    break;
                case Drag.Resize:
                    UpdateResize(world, e.shift, e.alt);
                    break;
                case Drag.Marquee:
                    UpdateMarquee(world);
                    break;
            }

            Repaint();
        }

        private void OnCanvasMouseUp(Event e)
        {
            switch (drag)
            {
                case Drag.Pending:
                    if (!pendingMarquee && !e.shift && boardSelection.Count > 1 && selectedId != null)
                    {
                        SetBoardSelection(selectedId, false);
                    }

                    break;
                case Drag.Move:
                    EndMove();
                    break;
                case Drag.Resize:
                    EndResize();
                    break;
                case Drag.Create:
                    EndCreate(e);
                    break;
            }

            drag = Drag.None;
            activeGuides.Clear();
            Repaint();
        }

        // ─── Move ────────────────────────────────────────────────────────────────

        /// <summary>The selection without layers whose ancestor is also selected, mapped from instance internals to nothing.</summary>
        private List<string> TopMostSelection()
        {
            var result = new List<string>();
            foreach (string id in boardSelection)
            {
                FigSceneNode n = scene.Find(id);
                if (n == null || n.Expanded)
                {
                    continue;
                }

                bool covered = false;
                for (int p = n.ParentIndex; p >= 0; p = scene.Nodes[p].ParentIndex)
                {
                    if (boardSelection.Contains(scene.Nodes[p].Id))
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

        private void BeginMove()
        {
            moveOrigins.Clear();
            moveDelta = Vector2.zero;
            bool fresh = BeginUndo("Move", null);
            Rect? bounds = null;
            var roots = new List<int>();
            foreach (string id in TopMostSelection())
            {
                FigSceneNode n = scene.Find(id);
                KiwiValue transform = n?.Source.Change.Get("transform");
                if (transform == null || n.Locked)
                {
                    continue;
                }

                if (fresh)
                {
                    undo.Capture(n.Source.Change, "transform");
                }

                moveOrigins.Add(new MoveOrigin
                {
                    Id = id,
                    Index = n.Index,
                    Transform = transform,
                    X = transform.Get("m02")?.AsFloat ?? 0,
                    Y = transform.Get("m12")?.AsFloat ?? 0,
                    ParentInverse = n.ParentWorld.Inverse(),
                });
                roots.Add(n.Index);
                bounds = bounds.HasValue ? FigGeometry.Union(bounds.Value, n.Bounds) : n.Bounds;
            }

            if (fresh)
            {
                CommitUndo("Move");
            }

            if (moveOrigins.Count == 0)
            {
                drag = Drag.None;
                return;
            }

            drag = Drag.Move;
            moveStartBounds = bounds ?? default;
            CollectGuideCandidates();

            // Lift the selection: drag one texture over tiles rendered without it.
            liftExclude.Clear();
            liftExclude.UnionWith(roots);
            liftSettling = false;
            tileCache.Lift(scene, roots, viewScale, viewOffset, canvasSize);
        }

        private void CollectGuideCandidates()
        {
            guideXs.Clear();
            guideYs.Clear();
            guideSources.Clear();
            FigSceneNode primary = scene.Nodes[moveOrigins[0].Index];
            int parent = primary.ParentIndex;
            var moving = new HashSet<int>();
            foreach (MoveOrigin o in moveOrigins)
            {
                moving.Add(o.Index);
            }

            if (parent >= 0)
            {
                AddGuideSource(scene.Nodes[parent].Bounds);
            }

            int start = parent >= 0 ? parent + 1 : 0;
            int end = parent >= 0 ? scene.Nodes[parent].SubtreeEnd : scene.Nodes.Length;
            for (int i = start; i < end; i = scene.Nodes[i].SubtreeEnd)
            {
                if (!moving.Contains(i))
                {
                    AddGuideSource(scene.Nodes[i].Bounds);
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

            if (snapToPixel)
            {
                delta = new Vector2(Mathf.Round(delta.x), Mathf.Round(delta.y));
            }

            // The file follows live (the panels show it); the scene catches up on release.
            foreach (MoveOrigin o in moveOrigins)
            {
                Vector2 localDelta = o.ParentInverse.ApplyVector(delta);
                SetFloat(o.Transform, "m02", o.X + localDelta.x);
                SetFloat(o.Transform, "m12", o.Y + localDelta.y);
            }

            moveDelta = delta;
            MarkDirty();
        }

        private void EndMove()
        {
            if (moveDelta == Vector2.zero)
            {
                liftExclude.Clear();
                tileCache.ClearLift();
                tileCache.DropLayer1();
                return;
            }

            Rect? region = null;
            foreach (MoveOrigin o in moveOrigins)
            {
                Rect dirty = scene.Refresh(o.Index, false);
                tileCache.UpdateScene(scene, dirty);
                region = region.HasValue ? FigGeometry.Union(region.Value, dirty) : dirty;
            }

            // Keep showing the lifted copy until the tiles under it are rendered with the move.
            liftSettleRegion = region ?? default;
            liftSettling = true;
        }

        private Vector2 SnapToGuides(Vector2 delta)
        {
            float threshold = GuideSnapPixels / viewScale;
            Rect moved = new Rect(moveStartBounds.position + delta, moveStartBounds.size);
            float[] xs = { moved.xMin, moved.center.x, moved.xMax };
            float[] ys = { moved.yMin, moved.center.y, moved.yMax };
            float bestX = threshold, snapX = 0, guideX = float.NaN;
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

            float bestY = threshold, snapY = 0, guideY = float.NaN;
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

        /// <summary>Local positions of the 8 handles (0 TL, 1 T, 2 TR, 3 R, 4 BR, 5 B, 6 BL, 7 L).</summary>
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

        private FigSceneNode ResizeTarget()
        {
            if (boardSelection.Count != 1 || drag == Drag.Move)
            {
                return null;
            }

            FigSceneNode n = scene.Find(First(boardSelection));
            return n != null && n.Resizable && !n.Locked && n.Source.Change.Get("size") != null && n.Source.Change.Get("transform") != null ? n : null;
        }

        private int HitHandle(Vector2 mouse)
        {
            FigSceneNode n = ResizeTarget();
            if (n == null)
            {
                return -1;
            }

            for (int i = 0; i < 8; i++)
            {
                if (i % 2 == 1 && Mathf.Min(n.Width, n.Height) * n.World.Scale * viewScale < 24f)
                {
                    continue;
                }

                Vector2 p = ToCanvas(n.World.Apply(HandleLocal(i, n.Width, n.Height)));
                if (Mathf.Abs(mouse.x - p.x) <= HandleSize && Mathf.Abs(mouse.y - p.y) <= HandleSize)
                {
                    return i;
                }
            }

            return -1;
        }

        private void BeginResize(int handle)
        {
            FigSceneNode n = ResizeTarget();
            if (n == null)
            {
                return;
            }

            KiwiValue change = n.Source.Change;
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
                Id = n.Id,
                Width = n.Width,
                Height = n.Height,
                X = transform.Get("m02")?.AsFloat ?? 0,
                Y = transform.Get("m12")?.AsFloat ?? 0,
                Local = FigAffine.FromMatrix(transform),
                WorldInverse = n.World.Inverse(),
            };
            drag = Drag.Resize;
        }

        private void UpdateResize(Vector2 world, bool keepRatio, bool fromCenter)
        {
            FigSceneNode n = scene.Find(resizeOrigin.Id);
            if (n == null)
            {
                return;
            }

            ResizeOrigin o = resizeOrigin;
            Vector2 p = o.WorldInverse.Apply(world);
            int h = resizeHandle;
            bool left = h == 0 || h == 6 || h == 7, right = h == 2 || h == 3 || h == 4;
            bool top = h == 0 || h == 1 || h == 2, bottom = h == 4 || h == 5 || h == 6;
            bool horizontal = left || right, vertical = top || bottom;
            float w = o.Width, hgt = o.Height;
            if (horizontal)
            {
                w = fromCenter ? Mathf.Abs(p.x - o.Width * 0.5f) * 2f : left ? o.Width - p.x : p.x;
            }

            if (vertical)
            {
                hgt = fromCenter ? Mathf.Abs(p.y - o.Height * 0.5f) * 2f : top ? o.Height - p.y : p.y;
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

            float x0 = fromCenter || (!horizontal && keepRatio) ? (o.Width - w) * 0.5f : left ? o.Width - w : 0f;
            float y0 = fromCenter || (!vertical && keepRatio) ? (o.Height - hgt) * 0.5f : top ? o.Height - hgt : 0f;
            Vector2 shift = o.Local.ApplyVector(new Vector2(x0, y0));
            float tx = o.X + shift.x, ty = o.Y + shift.y;
            if (snapToPixel && n.World.AxisAligned)
            {
                tx = Mathf.Round(tx);
                ty = Mathf.Round(ty);
            }

            KiwiValue change = n.Source.Change;
            SetFloat(change.Get("size"), "x", w);
            SetFloat(change.Get("size"), "y", hgt);
            SetFloat(change.Get("transform"), "m02", tx);
            SetFloat(change.Get("transform"), "m12", ty);
            RefreshNode(n.Index, false);
            MarkDirty();
        }

        /// <summary>
        /// Cached geometry no longer matches the new size: drop it so Figma rebuilds it, and keep
        /// text from snapping back to its old auto size.
        /// </summary>
        private void EndResize()
        {
            FigSceneNode n = scene.Find(resizeOrigin.Id);
            if (n == null)
            {
                return;
            }

            KiwiValue change = n.Source.Change;
            change.Remove("fillGeometry");
            change.Remove("strokeGeometry");
            if (n.Type == "TEXT")
            {
                change.Remove("derivedTextData");
                bool heightChanged = !Mathf.Approximately(n.Height, resizeOrigin.Height);
                FigFile.SetEnum(change, "textAutoResize", heightChanged ? "NONE" : "HEIGHT");
            }

            RefreshNode(n.Index, true);
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
            int end = marqueeContainer >= 0 ? scene.Nodes[marqueeContainer].SubtreeEnd : scene.Nodes.Length;
            string last = null;
            for (int i = start; i < end; i = scene.Nodes[i].SubtreeEnd)
            {
                FigSceneNode n = scene.Nodes[i];
                if (!n.Locked && n.Bounds.Overlaps(marqueeWorld))
                {
                    boardSelection.Add(n.Id);
                    last = n.Id;
                }
            }

            selectedId = last ?? (boardSelection.Count > 0 ? First(boardSelection) : null);
            rowsDirty = true;
        }

        // ─── Creating layers ─────────────────────────────────────────────────────

        private void EndCreate(Event e)
        {
            Tool used = tool;
            tool = Tool.Move;
            Vector2 a = dragStartWorld, b = dragWorld;
            bool dragged = (e.mousePosition - dragStartMouse).magnitude >= DragThreshold;
            Rect r = dragged
                ? Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y))
                : new Rect(a.x, a.y, used == Tool.Text ? 120 : 100, used == Tool.Text ? 24 : 100);
            if (snapToPixel)
            {
                r = Rect.MinMaxRect(Mathf.Round(r.xMin), Mathf.Round(r.yMin), Mathf.Round(r.xMax), Mathf.Round(r.yMax));
            }

            if (used == Tool.Line)
            {
                // A line is a width along its own x axis, rotated to the drag direction.
                Vector2 d = dragged ? b - a : new Vector2(100, 0);
                Defer(() => CreateLayer(used, new Rect(a.x, a.y, d.magnitude, 0), Mathf.Atan2(d.y, d.x)));
                return;
            }

            Defer(() => CreateLayer(used, r, 0));
        }

        private void CreateLayer(Tool kind, Rect world, float angle)
        {
            // Inside the innermost frame under the start point (not an instance's internals), else the page.
            FigNode parent = file.Find(boardPageId);
            FigAffine parentWorld = FigAffine.Identity;
            int hit = scene.HitTest(world.position, true);
            for (int i = hit; i >= 0; i = scene.Nodes[i].ParentIndex)
            {
                FigSceneNode n = scene.Nodes[i];
                if (!n.Expanded && (n.Type == "FRAME" || n.Type == "SECTION" || n.Type == "SYMBOL"))
                {
                    parent = n.Source;
                    parentWorld = n.World;
                    break;
                }
            }

            Vector2 local = parentWorld.Inverse().Apply(world.position);
            string type = kind == Tool.Frame ? "FRAME" : kind == Tool.Ellipse ? "ELLIPSE" : kind == Tool.Line ? "LINE" : kind == Tool.Text ? "TEXT" : "RECTANGLE";
            string name = kind == Tool.Frame ? "Frame" : kind == Tool.Ellipse ? "Ellipse" : kind == Tool.Line ? "Line" : kind == Tool.Text ? "Text" : "Rectangle";
            RecordStructure("Create " + name);
            FigNode created = file.CreateNode(parent, type, name + " " + (parent.Children.Count + 1), local.x, local.y, Mathf.Max(1f, world.width), world.height, n =>
            {
                switch (kind)
                {
                    case Tool.Frame:
                        FigFile.SetSolidFill(n, "fillPaints", 1, 1, 1, 1);
                        break;
                    case Tool.Line:
                        FigFile.SetSolidFill(n, "strokePaints", 0, 0, 0, 1);
                        n.Set("strokeWeight", KiwiValue.FromFloat(1));
                        FigFile.SetEnum(n, "strokeAlign", "CENTER");
                        if (Mathf.Abs(angle) > 1e-4f && n.Get("transform") is KiwiValue t)
                        {
                            t.Set("m00", KiwiValue.FromFloat(Mathf.Cos(angle)));
                            t.Set("m01", KiwiValue.FromFloat(-Mathf.Sin(angle)));
                            t.Set("m10", KiwiValue.FromFloat(Mathf.Sin(angle)));
                            t.Set("m11", KiwiValue.FromFloat(Mathf.Cos(angle)));
                        }

                        break;
                    case Tool.Text:
                    {
                        FigFile.SetSolidFill(n, "fillPaints", 0, 0, 0, 1);
                        KiwiValue textData = FigFile.Default(n.Def, "textData");
                        if (textData != null)
                        {
                            textData.Set("characters", KiwiValue.FromString("Text"));
                            n.Set("textData", textData);
                        }

                        n.Set("fontSize", KiwiValue.FromFloat(16));
                        KiwiValue font = FigFile.Default(n.Def, "fontName");
                        if (font != null)
                        {
                            font.Set("family", KiwiValue.FromString("Inter"));
                            font.Set("style", KiwiValue.FromString("Regular"));
                            font.Set("postscript", KiwiValue.FromString("Inter-Regular"));
                            n.Set("fontName", font);
                        }

                        FigFile.SetEnum(n, "textAutoResize", "WIDTH_AND_HEIGHT");
                        FigFile.SetEnum(n, "textAlignHorizontal", "LEFT");
                        FigFile.SetEnum(n, "textAlignVertical", "TOP");
                        break;
                    }
                    default:
                        FigFile.SetSolidFill(n, "fillPaints", 0.85f, 0.85f, 0.85f, 1);
                        break;
                }
            });

            if (created == null)
            {
                EditorUtility.DisplayDialog("Fig Editor", "This file's schema can't describe a new " + name.ToLowerInvariant() + ".", "OK");
                return;
            }

            MarkDirty();
            OnStructureChanged();
            Select(created.Id);
            boardSelection.Clear();
            boardSelection.Add(created.Id);
            if (kind == Tool.Text)
            {
                pendingTextEdit = created.Id;
            }
        }

        private string pendingTextEdit;

        // ─── Text editing ────────────────────────────────────────────────────────

        private void BeginTextEdit(FigSceneNode n)
        {
            editingTextId = n.Id;
            editingText = n.Text ?? "";
            Repaint();
        }

        private void CommitTextEdit()
        {
            string id = editingTextId;
            editingTextId = null;
            FigNode node = file.Find(id);
            if (node == null || editingText == null || editingText == (node.Change.Path("textData.characters")?.String ?? ""))
            {
                return;
            }

            RecordFields("Edit Text", null, node.Change, "textData", "derivedTextData");
            FigFile.SetText(node, editingText);
            MarkDirty();
            InvalidateBoardNode(id);
        }

        private void DrawTextEditor()
        {
            if (pendingTextEdit != null && Event.current.type == EventType.Layout && scene?.Find(pendingTextEdit) is FigSceneNode created)
            {
                pendingTextEdit = null;
                BeginTextEdit(created);
            }

            FigSceneNode n = editingTextId != null ? scene.Find(editingTextId) : null;
            if (n == null)
            {
                editingTextId = null;
                return;
            }

            Rect r = ToCanvas(n.Bounds);
            r.width = Mathf.Max(r.width, 40);
            r.height = Mathf.Max(r.height, n.FontSize * viewScale * 1.4f);
            GUIStyle style = TextStyleFor(n, true);
            GUI.SetNextControlName("FigCanvasText");
            editingText = GUI.TextArea(r, editingText, style);
            EditorGUI.FocusTextInControl("FigCanvasText");
            Event e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                CommitTextEdit();
                e.Use();
            }
        }

        // ─── Keyboard and commands ───────────────────────────────────────────────

        private void OnCanvasKey(Event e)
        {
            if (!HandleCanvasShortcut(e) && !HandleCanvasPlainKey(e))
            {
                return;
            }

            e.Use();
            Repaint();
        }

        /// <summary>
        /// Ctrl/Cmd and Shift combinations; Ctrl/Cmd+Z and Y are left to Unity's Undo, which drives
        /// this history through <see cref="FigUndoProxy"/>.
        /// </summary>
        private bool HandleCanvasShortcut(Event e)
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
                        ZoomTo(1f);
                        return true;
                }
            }

            return false;
        }

        private bool HandleCanvasPlainKey(Event e)
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
                case KeyCode.V:
                    tool = Tool.Move;
                    return true;
                case KeyCode.H:
                    tool = Tool.Hand;
                    return true;
                case KeyCode.F:
                    tool = Tool.Frame;
                    return true;
                case KeyCode.R:
                    tool = Tool.Rectangle;
                    return true;
                case KeyCode.O:
                    tool = Tool.Ellipse;
                    return true;
                case KeyCode.L:
                    tool = Tool.Line;
                    return true;
                case KeyCode.T:
                    tool = Tool.Text;
                    return true;
                case KeyCode.Escape:
                {
                    if (tool != Tool.Move)
                    {
                        tool = Tool.Move;
                        return true;
                    }

                    FigSceneNode n = SelectedSceneNode;
                    SetBoardSelection(n != null && n.ParentIndex >= 0 ? scene.Nodes[n.ParentIndex].Id : null, false);
                    return true;
                }
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                {
                    FigSceneNode n = SelectedSceneNode;
                    if (n != null && n.Type == "TEXT" && !n.Expanded)
                    {
                        BeginTextEdit(n);
                    }
                    else if (n != null && n.Index + 1 < n.SubtreeEnd)
                    {
                        SetBoardSelection(scene.Nodes[n.Index + 1].Id, false);
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

        /// <summary>Unity's own commands (Edit menu, F, Delete…) routed to the canvas while it has focus.</summary>
        private bool CanvasCommand(string name, bool execute)
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
        /// KeyDown and as an Edit command; without this one press could delete twice.
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
                FigSceneNode n = scene.Find(id);
                KiwiValue transform = n?.Source.Change.Get("transform");
                if (transform == null || n.Locked)
                {
                    continue;
                }

                if (fresh)
                {
                    undo.Capture(n.Source.Change, "transform");
                }

                Vector2 local = n.ParentWorld.Inverse().ApplyVector(worldDelta);
                SetFloat(transform, "m02", (transform.Get("m02")?.AsFloat ?? 0) + local.x);
                SetFloat(transform, "m12", (transform.Get("m12")?.AsFloat ?? 0) + local.y);
                RefreshNode(n.Index, false);
            }

            if (fresh)
            {
                CommitUndo("Nudge");
            }

            MarkDirty();
        }

        private void SelectAllSiblings()
        {
            FigSceneNode n = SelectedSceneNode;
            int parent = n?.ParentIndex ?? -1;
            int start = parent >= 0 ? parent + 1 : 0;
            int end = parent >= 0 ? scene.Nodes[parent].SubtreeEnd : scene.Nodes.Length;
            boardSelection.Clear();
            for (int i = start; i < end; i = scene.Nodes[i].SubtreeEnd)
            {
                if (!scene.Nodes[i].Locked)
                {
                    boardSelection.Add(scene.Nodes[i].Id);
                }
            }

            if (selectedId == null || !boardSelection.Contains(selectedId))
            {
                selectedId = First(boardSelection);
            }

            rowsDirty = true;
        }

        private void ToggleBoardVisibility()
        {
            var ids = new List<string>(TopMostSelection());
            if (ids.Count == 0 || !BeginUndo("Show/Hide", null))
            {
                return;
            }

            foreach (string id in ids)
            {
                undo.Capture(file.Find(id)?.Change, "visible");
            }

            CommitUndo("Show/Hide");
            foreach (string id in ids)
            {
                KiwiValue change = file.Find(id)?.Change;
                if (change != null)
                {
                    change.Set("visible", KiwiValue.FromBool(!(change.Get("visible")?.AsBool ?? true)));
                }
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

        private void ShowCanvasMenu()
        {
            var menu = new GenericMenu();
            bool any = TopMostSelection().Count > 0;
            AddMenuItem(menu, "Duplicate", any, () => RequestBoardAction(DuplicateBoardSelection));
            AddMenuItem(menu, "Delete", any, () => RequestBoardAction(DeleteBoardSelection));
            menu.AddSeparator("");
            AddMenuItem(menu, "Bring Forward", boardSelection.Count == 1 && Selected != null, () => Defer(() => MoveNode(Selected, 1)));
            AddMenuItem(menu, "Send Backward", boardSelection.Count == 1 && Selected != null, () => Defer(() => MoveNode(Selected, -1)));
            AddMenuItem(menu, "Show/Hide", any, ToggleBoardVisibility);
            menu.AddSeparator("");
            AddMenuItem(menu, "Select Parent", SelectedSceneNode?.ParentIndex >= 0, () => SetBoardSelection(scene.Nodes[SelectedSceneNode.ParentIndex].Id, false));
            AddMenuItem(menu, "Zoom to Selection", boardSelection.Count > 0, ZoomToSelection);
            AddMenuItem(menu, "Zoom to Fit", true, () => viewNeedsFit = true);
            AddMenuItem(menu, "Copy GUID", selectedId != null, () => EditorGUIUtility.systemCopyBuffer = selectedId);
            menu.ShowAsContext();
        }

        /// <summary>Paint order: later siblings draw on top, so "forward" is the next sibling.</summary>
        private void MoveSelectedInOrder(int delta)
        {
            if (Selected != null)
            {
                MoveNode(Selected, delta);
            }
        }

        // ─── Overlays ────────────────────────────────────────────────────────────

        private void DrawCanvasOverlays(Rect local)
        {
            frameTitles.Clear();
            DrawFrameTitles(local);

            FigSceneNode hover = drag == Drag.None && hoverId != null && !boardSelection.Contains(hoverId) ? scene.Find(hoverId) : null;
            if (hover != null && !Measuring)
            {
                DrawNodeOutline(hover, 1f, hover.Expanded || hover.Type == "SYMBOL" || hover.Type == "INSTANCE" ? FigTheme.Component : FigTheme.Accent, Vector2.zero);
            }

            foreach (string id in boardSelection)
            {
                FigSceneNode n = scene.Find(id);
                if (n != null)
                {
                    // While dragging the scene still has the old place; once dropped it has the new one.
                    Vector2 offset = drag == Drag.Move && IsMoving(n.Index) ? moveDelta : Vector2.zero;
                    DrawNodeOutline(n, 1.5f, n.Expanded || n.Type == "SYMBOL" || n.Type == "INSTANCE" ? FigTheme.Component : FigTheme.Accent, offset);
                }
            }

            Rect? bounds = SelectionBounds(true);
            if (boardSelection.Count > 1 && bounds.HasValue)
            {
                DrawRectOutline(ToCanvas(bounds.Value), 1f, FigTheme.Accent);
            }

            foreach (Guide g in activeGuides)
            {
                Vector2 a = ToCanvas(g.A), b = ToCanvas(g.B);
                DrawLine(a, b, 1f, GuideColor);
            }

            if (drag == Drag.Marquee)
            {
                Rect r = ToCanvas(marqueeWorld);
                EditorGUI.DrawRect(r, new Color(FigTheme.Accent.r, FigTheme.Accent.g, FigTheme.Accent.b, 0.12f));
                DrawRectOutline(r, 1f, FigTheme.Accent);
            }

            if (drag == Drag.Create)
            {
                Rect r = ToCanvas(Rect.MinMaxRect(Mathf.Min(dragStartWorld.x, dragWorld.x), Mathf.Min(dragStartWorld.y, dragWorld.y),
                    Mathf.Max(dragStartWorld.x, dragWorld.x), Mathf.Max(dragStartWorld.y, dragWorld.y)));
                if (tool == Tool.Line)
                {
                    DrawLine(ToCanvas(dragStartWorld), ToCanvas(dragWorld), 1.5f, FigTheme.Accent);
                }
                else
                {
                    DrawRectOutline(r, 1f, FigTheme.Accent);
                }
            }

            FigSceneNode resizable = ResizeTarget();
            if (resizable != null && drag != Drag.Create)
            {
                for (int i = 0; i < 8; i++)
                {
                    if (i % 2 == 1 && Mathf.Min(resizable.Width, resizable.Height) * resizable.World.Scale * viewScale < 24f)
                    {
                        continue;
                    }

                    Vector2 p = ToCanvas(resizable.World.Apply(HandleLocal(i, resizable.Width, resizable.Height)));
                    var h = new Rect(Mathf.Round(p.x - HandleSize * 0.5f), Mathf.Round(p.y - HandleSize * 0.5f), HandleSize, HandleSize);
                    EditorGUI.DrawRect(h, FigTheme.Accent);
                    EditorGUI.DrawRect(new Rect(h.x + 1, h.y + 1, h.width - 2, h.height - 2), Color.white);
                }
            }

            DrawMeasurements();
            if (bounds.HasValue && drag != Drag.Marquee)
            {
                DrawSizeBadge(ToCanvas(bounds.Value));
            }
        }

        // ─── Measuring (hold Alt) ─────────────────────────────────────────────────

        private static readonly Color MeasureColor = new Color(0.95f, 0.28f, 0.13f, 1f);

        /// <summary>Alt held with a selection and nothing being dragged: distances are shown, like Figma.</summary>
        private bool Measuring => Event.current != null && Event.current.alt && drag == Drag.None && boardSelection.Count > 0 && editingTextId == null;

        /// <summary>
        /// Hold Alt: the distances from the selection to the hovered layer (to its edges when it
        /// contains the selection or the other way round, else the gaps between them), or to the
        /// selection's parent when nothing else is hovered.
        /// </summary>
        private void DrawMeasurements()
        {
            if (!Measuring)
            {
                return;
            }

            Rect? selected = SelectionBounds();
            if (!selected.HasValue)
            {
                return;
            }

            Rect a = selected.Value;
            FigSceneNode other = hoverId != null && !boardSelection.Contains(hoverId) ? scene.Find(hoverId) : null;
            if (other == null)
            {
                FigSceneNode first = scene.Find(First(boardSelection));
                other = first != null && first.ParentIndex >= 0 ? scene.Nodes[first.ParentIndex] : null;
            }

            if (other == null)
            {
                return;
            }

            Rect b = other.Bounds;
            DrawRectOutline(ToCanvas(b), 1f, MeasureColor);
            DrawRectOutline(ToCanvas(a), 1f, MeasureColor);
            if (Contains(b, a))
            {
                Inside(a, b);
            }
            else if (Contains(a, b))
            {
                Inside(b, a);
            }
            else
            {
                Gaps(a, b);
            }
        }

        private static bool Contains(Rect outer, Rect inner)
        {
            const float e = 0.01f;
            return inner.xMin >= outer.xMin - e && inner.xMax <= outer.xMax + e && inner.yMin >= outer.yMin - e && inner.yMax <= outer.yMax + e;
        }

        /// <summary>From each edge of <paramref name="inner"/> to the matching edge of <paramref name="outer"/>.</summary>
        private void Inside(Rect inner, Rect outer)
        {
            float cy = inner.center.y, cx = inner.center.x;
            MeasureLine(new Vector2(outer.xMin, cy), new Vector2(inner.xMin, cy));
            MeasureLine(new Vector2(inner.xMax, cy), new Vector2(outer.xMax, cy));
            MeasureLine(new Vector2(cx, outer.yMin), new Vector2(cx, inner.yMin));
            MeasureLine(new Vector2(cx, inner.yMax), new Vector2(cx, outer.yMax));
        }

        /// <summary>The horizontal and vertical gaps between two separate (or overlapping) boxes.</summary>
        private void Gaps(Rect a, Rect b)
        {
            // Horizontal: between facing sides, at the middle of the vertical overlap (else at the selection's centre).
            float y0 = Mathf.Max(a.yMin, b.yMin), y1 = Mathf.Min(a.yMax, b.yMax);
            float y = y1 >= y0 ? (y0 + y1) / 2f : a.center.y;
            float x0 = Mathf.Max(a.xMin, b.xMin), x1 = Mathf.Min(a.xMax, b.xMax);
            float x = x1 >= x0 ? (x0 + x1) / 2f : a.center.x;
            if (a.xMax <= b.xMin)
            {
                MeasureLine(new Vector2(a.xMax, y), new Vector2(b.xMin, y));
                Extension(new Vector2(b.xMin, y), b);
            }
            else if (b.xMax <= a.xMin)
            {
                MeasureLine(new Vector2(b.xMax, y), new Vector2(a.xMin, y));
                Extension(new Vector2(b.xMax, y), b);
            }
            else
            {
                // Overlapping horizontally: the offsets between the left and between the right edges.
                MeasureLine(new Vector2(Mathf.Min(a.xMin, b.xMin), y), new Vector2(Mathf.Max(a.xMin, b.xMin), y));
                MeasureLine(new Vector2(Mathf.Min(a.xMax, b.xMax), y), new Vector2(Mathf.Max(a.xMax, b.xMax), y));
            }

            if (a.yMax <= b.yMin)
            {
                MeasureLine(new Vector2(x, a.yMax), new Vector2(x, b.yMin));
                Extension(new Vector2(x, b.yMin), b);
            }
            else if (b.yMax <= a.yMin)
            {
                MeasureLine(new Vector2(x, b.yMax), new Vector2(x, a.yMin));
                Extension(new Vector2(x, b.yMax), b);
            }
            else if (!(a.xMax <= b.xMin || b.xMax <= a.xMin))
            {
                MeasureLine(new Vector2(x, Mathf.Min(a.yMin, b.yMin)), new Vector2(x, Mathf.Max(a.yMin, b.yMin)));
                MeasureLine(new Vector2(x, Mathf.Min(a.yMax, b.yMax)), new Vector2(x, Mathf.Max(a.yMax, b.yMax)));
            }
        }

        /// <summary>A faint guide from a line's end to the box it measures to, when the end lies outside it.</summary>
        private void Extension(Vector2 end, Rect box)
        {
            var faint = new Color(MeasureColor.r, MeasureColor.g, MeasureColor.b, 0.45f);
            Vector2 clamped = new Vector2(Mathf.Clamp(end.x, box.xMin, box.xMax), Mathf.Clamp(end.y, box.yMin, box.yMax));
            if ((clamped - end).sqrMagnitude > 1e-6f)
            {
                DrawLine(ToCanvas(end), ToCanvas(clamped), 1f, faint);
            }
        }

        /// <summary>A red distance line with its length (in layer units) in a pill at its middle.</summary>
        private void MeasureLine(Vector2 worldA, Vector2 worldB)
        {
            float length = (worldB - worldA).magnitude;
            if (length < 0.01f)
            {
                return;
            }

            Vector2 a = ToCanvas(worldA), b = ToCanvas(worldB);
            DrawLine(a, b, 1f, MeasureColor);
            bool horizontal = Mathf.Abs(a.y - b.y) < 0.5f;
            // End ticks.
            Vector2 tick = horizontal ? new Vector2(0, 4) : new Vector2(4, 0);
            DrawLine(a - tick, a + tick, 1f, MeasureColor);
            DrawLine(b - tick, b + tick, 1f, MeasureColor);

            string text = FormatNumber(length);
            Vector2 size = FigTheme.Badge.CalcSize(new GUIContent(text));
            Vector2 mid = (a + b) * 0.5f;
            var pill = horizontal
                ? new Rect(Mathf.Round(mid.x - size.x * 0.5f - 4), Mathf.Round(mid.y + 4), size.x + 8, 16)
                : new Rect(Mathf.Round(mid.x + 5), Mathf.Round(mid.y - 8), size.x + 8, 16);
            EditorGUI.DrawRect(pill, MeasureColor);
            GUI.Label(pill, text, FigTheme.Badge);
        }

        private void DrawNodeOutline(FigSceneNode n, float width, Color color, Vector2 worldOffset)
        {
            if (n.World.AxisAligned)
            {
                Rect r = ToCanvas(new Rect(n.Bounds.position + worldOffset, n.Bounds.size));
                DrawRectOutline(r, width, color);
                return;
            }

            for (int i = 0; i < 4; i++)
            {
                DrawLine(ToCanvas(n.Corners[i] + worldOffset), ToCanvas(n.Corners[(i + 1) % 4] + worldOffset), width, color);
            }
        }

        private static void DrawRectOutline(Rect r, float width, Color color)
        {
            float w = Mathf.Max(1f, Mathf.Round(width));
            r = new Rect(Mathf.Round(r.x), Mathf.Round(r.y), Mathf.Round(r.width), Mathf.Round(r.height));
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, w), color);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - w, r.width, w), color);
            EditorGUI.DrawRect(new Rect(r.x, r.y, w, r.height), color);
            EditorGUI.DrawRect(new Rect(r.xMax - w, r.y, w, r.height), color);
        }

        /// <summary>A line as a rotated thin rect (axis-aligned lines take the exact path).</summary>
        private static void DrawLine(Vector2 a, Vector2 b, float width, Color color)
        {
            if (Mathf.Abs(a.x - b.x) < 0.5f)
            {
                EditorGUI.DrawRect(new Rect(Mathf.Round(a.x) - width * 0.5f, Mathf.Min(a.y, b.y), width, Mathf.Abs(b.y - a.y)), color);
                return;
            }

            if (Mathf.Abs(a.y - b.y) < 0.5f)
            {
                EditorGUI.DrawRect(new Rect(Mathf.Min(a.x, b.x), Mathf.Round(a.y) - width * 0.5f, Mathf.Abs(b.x - a.x), width), color);
                return;
            }

            Vector2 d = b - a;
            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            EditorGUI.DrawRect(new Rect(a.x, a.y - width * 0.5f, d.magnitude, width), color);
            GUI.matrix = old;
        }

        private GUIStyle titleStyle;

        private void DrawFrameTitles(Rect local)
        {
            titleStyle ??= new GUIStyle(FigTheme.Small) { alignment = TextAnchor.LowerLeft };
            Color text = scene.Background.grayscale > 0.5f ? new Color(0, 0, 0, 0.55f) : new Color(1, 1, 1, 0.6f);
            foreach (FigSceneNode n in scene.Nodes)
            {
                if (n.ParentIndex >= 0 || !n.IsFrameLike)
                {
                    continue;
                }

                Vector2 delta = drag == Drag.Move && IsMoving(n.Index) ? moveDelta : Vector2.zero;
                Rect r = ToCanvas(new Rect(n.Bounds.position + delta, n.Bounds.size));
                if (r.width < 24f || r.xMax < 0 || r.xMin > local.width || r.yMin - 18 > local.height || r.yMin < -200)
                {
                    continue;
                }

                var title = new Rect(r.x, r.y - 18, Mathf.Max(24f, r.width), 16);
                frameTitles.Add(new FrameTitle { Rect = title, Id = n.Id });
                bool selected = boardSelection.Contains(n.Id);
                titleStyle.normal.textColor = selected ? (n.Type == "SYMBOL" || n.Type == "INSTANCE" ? FigTheme.Component : FigTheme.Accent) : text;
                string label = (n.Type == "SYMBOL" ? "❖ " : "") + (string.IsNullOrEmpty(n.Name) ? n.Type : n.Name);
                GUI.Label(title, label, titleStyle);
            }
        }

        private string HitFrameTitle(Vector2 mouse)
        {
            for (int i = frameTitles.Count - 1; i >= 0; i--)
            {
                Rect r = frameTitles[i].Rect;
                string label = scene.Find(frameTitles[i].Id)?.Name ?? "";
                float textWidth = titleStyle != null ? titleStyle.CalcSize(new GUIContent(label)).x + 16 : r.width;
                if (new Rect(r.x, r.y, Mathf.Min(r.width, textWidth), r.height).Contains(mouse))
                {
                    return frameTitles[i].Id;
                }
            }

            return null;
        }

        private void DrawSizeBadge(Rect selection)
        {
            string text;
            FigSceneNode single = boardSelection.Count == 1 ? scene.Find(First(boardSelection)) : null;
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

            Vector2 size = FigTheme.Badge.CalcSize(new GUIContent(text));
            var badge = new Rect(Mathf.Round(selection.center.x - size.x * 0.5f - 5), Mathf.Round(selection.yMax + 6), size.x + 10, 17);
            bool component = single != null && (single.Expanded || single.Type == "SYMBOL" || single.Type == "INSTANCE");
            EditorGUI.DrawRect(badge, component ? FigTheme.Component : FigTheme.Accent);
            GUI.Label(badge, text, FigTheme.Badge);
        }

        private static string FormatNumber(float v)
        {
            return Mathf.Abs(v - Mathf.Round(v)) < 0.005f ? Mathf.Round(v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        // ─── Text without glyph outlines ─────────────────────────────────────────

        /// <summary>
        /// The last resort for text that has neither Figma's glyph outlines nor an installed font to
        /// lay it out with (no font files found at all): Unity draws it over the rendered page.
        /// </summary>
        private void DrawFallbackText(Rect local)
        {
            foreach (FigSceneNode n in scene.Nodes)
            {
                if (!n.FallbackText || n.Id == editingTextId || !n.World.AxisAligned)
                {
                    continue;
                }

                if ((drag == Drag.Move || liftSettling) && IsMoving(n.Index))
                {
                    continue;
                }

                Rect r = ToCanvas(n.Bounds);
                if (r.xMax < 0 || r.yMax < 0 || r.xMin > local.width || r.yMin > local.height)
                {
                    continue;
                }

                float size = n.FontSize * n.World.Scale * viewScale;
                if (size < 4f)
                {
                    EditorGUI.DrawRect(new Rect(r.x, r.y + r.height * 0.35f, r.width, Mathf.Max(1, r.height * 0.3f)), new Color(n.TextColor.r, n.TextColor.g, n.TextColor.b, 0.3f));
                    continue;
                }

                // Clipped by its clipping ancestors.
                Rect clip = local;
                for (int p = n.ParentIndex; p >= 0; p = scene.Nodes[p].ParentIndex)
                {
                    if (scene.Nodes[p].ClipsContent)
                    {
                        clip = FigGeometry.Intersect(clip, ToCanvas(scene.Nodes[p].Bounds));
                    }
                }

                if (clip.width <= 0 || clip.height <= 0)
                {
                    continue;
                }

                GUI.BeginGroup(clip);
                GUI.Label(new Rect(r.x - clip.x, r.y - clip.y, r.width, r.height), n.Text, TextStyleFor(n, false));
                GUI.EndGroup();
            }
        }

        private GUIStyle TextStyleFor(FigSceneNode n, bool editing)
        {
            overlayText ??= new GUIStyle(EditorStyles.label) { richText = false, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
            var style = editing ? new GUIStyle(overlayText) : overlayText;
            style.font = OsFont(n.FontFamily);
            style.fontSize = Mathf.Clamp(Mathf.RoundToInt(n.FontSize * n.World.Scale * viewScale), 1, 300);
            string fs = n.FontStyle ?? "";
            bool bold = fs.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) >= 0 || fs.IndexOf("Black", StringComparison.OrdinalIgnoreCase) >= 0 || fs.IndexOf("Semi", StringComparison.OrdinalIgnoreCase) >= 0;
            bool italic = fs.IndexOf("Italic", StringComparison.OrdinalIgnoreCase) >= 0;
            style.fontStyle = bold ? italic ? FontStyle.BoldAndItalic : FontStyle.Bold : italic ? FontStyle.Italic : FontStyle.Normal;
            style.wordWrap = n.TextWraps;
            style.clipping = TextClipping.Overflow;
            int col = n.TextAlignH == "CENTER" ? 1 : n.TextAlignH == "RIGHT" ? 2 : 0;
            int row = n.TextAlignV == "CENTER" ? 1 : n.TextAlignV == "BOTTOM" ? 2 : 0;
            style.alignment = (TextAnchor)(row * 3 + col);
            Color c = n.TextColor;
            style.normal.textColor = c;
            style.focused.textColor = c;
            style.hover.textColor = c;
            style.active.textColor = c;
            if (editing)
            {
                style.normal.background = null;
                style.focused.background = null;
            }

            return style;
        }

        private Font OsFont(string family)
        {
            if (string.IsNullOrEmpty(family))
            {
                return null;
            }

            if (!osFonts.TryGetValue(family, out Font font))
            {
                try
                {
                    font = Font.CreateDynamicFontFromOSFont(new[] { family, "Inter", "Roboto", "Segoe UI", "Helvetica Neue", "Arial" }, 16);
                    if (font != null)
                    {
                        font.hideFlags = HideFlags.HideAndDontSave;
                    }
                }
                catch (Exception)
                {
                    font = null;
                }

                osFonts[family] = font;
            }

            return font;
        }

        // ─── Floating toolbar ────────────────────────────────────────────────────

        private static Rect ToolbarRect(Rect local)
        {
            const float w = 7 * 34 + 16, h = 44;
            return new Rect(Mathf.Round(local.center.x - w / 2), local.yMax - h - 16, w, h);
        }

        private void DrawToolbar(Rect r)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(new Rect(r.x + 2, r.y + 3, r.width, r.height), new Color(0, 0, 0, 0.35f));
                EditorGUI.DrawRect(r, FigTheme.Panel);
                DrawRectOutline(r, 1f, FigTheme.Border);
            }

            float x = r.x + 8;
            ToolButton(ref x, r, Tool.Move, "move", "Move (V)");
            ToolButton(ref x, r, Tool.Hand, "hand", "Hand (H)");
            ToolButton(ref x, r, Tool.Frame, "frame", "Frame (F)");
            ToolButton(ref x, r, Tool.Rectangle, "rect", "Rectangle (R)");
            ToolButton(ref x, r, Tool.Ellipse, "ellipse", "Ellipse (O)");
            ToolButton(ref x, r, Tool.Line, "line", "Line (L)");
            ToolButton(ref x, r, Tool.Text, "text", "Text (T)");
        }

        private void ToolButton(ref float x, Rect bar, Tool which, string icon, string tooltip)
        {
            var b = new Rect(x, bar.y + 6, 32, 32);
            if (FigTheme.IconButton(b, icon, tooltip, tool == which))
            {
                tool = which;
                GUIUtility.keyboardControl = canvasControlId;
            }

            x += 34;
        }
    }
}
