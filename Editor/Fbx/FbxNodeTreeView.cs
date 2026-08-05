using System;
using System.Collections.Generic;
using Autodesk.Fbx;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Percas.UnityTools.Fbx
{
    public class FbxNodeTreeViewItem : TreeViewItem
    {
        public FbxNode Node;
    }

    /// <summary>
    /// Displays the open FbxDocument's node hierarchy: select, rename inline,
    /// delete via context menu, reparent via drag-and-drop.
    /// </summary>
    public class FbxNodeTreeView : TreeView
    {
        private const string DragGenericDataKey = "Percas.FbxNodeTreeView.DraggedIds";

        private readonly FbxDocument _document;
        private readonly Dictionary<int, FbxNode> _nodesById = new Dictionary<int, FbxNode>();
        private int _nextId;

        public Action<FbxNode> OnNodeRenamed;
        public Action<FbxNode, FbxNode> OnNodeReparented;
        public Action<FbxNode> OnNodeDeleted;

        public FbxNodeTreeView(TreeViewState state, FbxDocument document) : base(state)
        {
            _document = document;
            Reload();
        }

        public FbxNode GetNode(int id) => _nodesById.TryGetValue(id, out var node) ? node : null;

        protected override TreeViewItem BuildRoot()
        {
            var root = new TreeViewItem { id = -1, depth = -1, displayName = "Root" };
            _nodesById.Clear();
            _nextId = 0;

            if (_document.IsOpen)
            {
                BuildChildren(root, _document.RootNode);
            }
            else
            {
                root.AddChild(new TreeViewItem { id = 0, displayName = "(no file open)" });
            }

            TreeViewUtility<int>.SetupDepthsFromParentsAndChildren(root);
            return root;
        }

        private void BuildChildren(TreeViewItem parentItem, FbxNode fbxNode)
        {
            for (var i = 0; i < fbxNode.GetChildCount(); i++)
            {
                var child = fbxNode.GetChild(i);
                var id = _nextId++;
                _nodesById[id] = child;

                var item = new FbxNodeTreeViewItem
                {
                    id = id,
                    displayName = child.GetName(),
                    Node = child
                };
                parentItem.AddChild(item);
                BuildChildren(item, child);
            }
        }

        protected override bool CanRename(TreeViewItem item) => item is FbxNodeTreeViewItem;

        protected override void RenameEnded(RenameEndedArgs args)
        {
            if (!args.acceptedRename || args.newName == args.originalName)
            {
                return;
            }

            var node = GetNode(args.itemID);
            if (node == null)
            {
                return;
            }

            var path = FbxNodeOperations.GetNodePath(node);
            FbxNodeOperations.Rename(node, args.newName);
            _document.RecordChange(FbxChangeKind.Renamed, path, args.originalName, args.newName);
            OnNodeRenamed?.Invoke(node);
            Reload();
        }

        protected override void ContextClickedItem(int id)
        {
            var node = GetNode(id);
            if (node == null)
            {
                return;
            }

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Delete"), false, () => DeleteNode(node));
            menu.ShowAsContext();
        }

        private void DeleteNode(FbxNode node)
        {
            var path = FbxNodeOperations.GetNodePath(node);

            if (FbxNodeOperations.IsBoneOrSkinned(node) &&
                !EditorUtility.DisplayDialog(
                    "Delete bone/skinned node",
                    $"'{path}' is a bone or is referenced by a skin deformer. Deleting it can break " +
                    "Unity-side Animator/Avatar bindings that reference it by name outside this file. " +
                    "Delete anyway?",
                    "Delete", "Cancel"))
            {
                return;
            }

            FbxNodeOperations.Delete(node);
            _document.RecordChange(FbxChangeKind.Deleted, path, path, "(deleted)");
            OnNodeDeleted?.Invoke(node);
            Reload();
        }

        protected override bool CanStartDrag(CanStartDragArgs args) => true;

        protected override void SetupDragAndDrop(SetupDragAndDropArgs args)
        {
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.SetGenericData(DragGenericDataKey, new List<int>(args.draggedItemIDs));
            DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
            DragAndDrop.StartDrag("Move FBX Node");
        }

        protected override DragAndDropVisualMode HandleDragAndDrop(DragAndDropArgs args)
        {
            if (!(DragAndDrop.GetGenericData(DragGenericDataKey) is List<int> draggedIds) || draggedIds.Count == 0)
            {
                return DragAndDropVisualMode.None;
            }

            if (!args.performDrop)
            {
                return DragAndDropVisualMode.Move;
            }

            var newParentNode = args.parentItem == null || args.parentItem.id == -1
                ? _document.RootNode
                : GetNode(args.parentItem.id);

            if (newParentNode == null)
            {
                return DragAndDropVisualMode.None;
            }

            foreach (var id in draggedIds)
            {
                var node = GetNode(id);
                if (node == null || node == newParentNode)
                {
                    continue;
                }

                var oldPath = FbxNodeOperations.GetNodePath(node);
                FbxNodeOperations.Reparent(_document.Scene, node, newParentNode, preserveWorldTransform: true);
                var newPath = FbxNodeOperations.GetNodePath(node);
                _document.RecordChange(FbxChangeKind.Reparented, oldPath, oldPath, newPath);
                OnNodeReparented?.Invoke(node, newParentNode);
            }

            Reload();
            return DragAndDropVisualMode.Move;
        }
    }
}
