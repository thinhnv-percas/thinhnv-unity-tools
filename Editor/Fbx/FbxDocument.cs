using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Fbx;
using UnityEditor;

namespace Percas.UnityTools.Fbx
{
    /// <summary>
    /// Owns the FbxManager/FbxScene pair for one open .fbx file. All FBX SDK
    /// access should go through this class rather than touching Autodesk.Fbx
    /// types directly, since that native memory is not tracked across Unity's
    /// domain reloads and has to be closed explicitly.
    /// </summary>
    public class FbxDocument : IDisposable
    {
        private static readonly List<FbxDocument> OpenDocuments = new List<FbxDocument>();

        static FbxDocument()
        {
            AssemblyReloadEvents.beforeAssemblyReload += CloseAllOpenDocuments;
            EditorApplication.quitting += CloseAllOpenDocuments;
        }

        private static void CloseAllOpenDocuments()
        {
            for (var i = OpenDocuments.Count - 1; i >= 0; i--)
            {
                OpenDocuments[i].Close();
            }
        }

        private FbxManager _manager;
        private FbxScene _scene;

        public string FilePath { get; private set; }
        public bool IsOpen => _scene != null;
        public bool IsDirty { get; private set; }
        public FbxChangeLog ChangeLog { get; } = new FbxChangeLog();

        public bool HasAnimation { get; private set; }
        public bool HasSkinning { get; private set; }
        public bool HasBlendShapes { get; private set; }

        /// <summary>
        /// Phase 1 does not understand animation/skin/blend-shape data well enough
        /// to guarantee it survives a round trip untouched, so saving is blocked
        /// outright rather than risking a silent partial write.
        /// </summary>
        public bool IsSaveBlocked => HasAnimation || HasSkinning || HasBlendShapes;

        public FbxScene Scene => _scene;
        public FbxNode RootNode => _scene?.GetRootNode();

        public void Open(string path)
        {
            Close();

            _manager = FbxManager.Create();
            var ioSettings = FbxIOSettings.Create(_manager, Globals.IOSROOT);
            _manager.SetIOSettings(ioSettings);

            var importer = FbxImporter.Create(_manager, "");
            try
            {
                if (!importer.Initialize(path, -1, _manager.GetIOSettings()))
                {
                    throw new InvalidOperationException(
                        $"Failed to initialize FBX importer for '{path}': {importer.GetStatus().GetErrorString()}");
                }

                _scene = FbxScene.Create(_manager, "scene");
                if (!importer.Import(_scene))
                {
                    throw new InvalidOperationException($"Failed to import FBX scene from '{path}'.");
                }
            }
            finally
            {
                importer.Destroy();
            }

            FilePath = path;
            IsDirty = false;
            ChangeLog.Clear();
            ScanRiskyContent();
            OpenDocuments.Add(this);
        }

        /// <summary>
        /// Saves to <paramref name="path"/> (defaults to the currently open file).
        /// Overwriting the original file is always preceded by a timestamped
        /// backup so a bad round trip cannot destroy the only copy.
        /// </summary>
        public string Save(string path = null)
        {
            if (!IsOpen)
            {
                throw new InvalidOperationException("No FBX document is open.");
            }

            if (IsSaveBlocked)
            {
                throw new InvalidOperationException(
                    "Save is blocked: this file contains animation, skinning, or blend shapes, " +
                    "which this tool cannot yet round-trip safely.");
            }

            var targetPath = path ?? FilePath;
            string backupPath = null;
            if (string.Equals(targetPath, FilePath, StringComparison.OrdinalIgnoreCase) && File.Exists(targetPath))
            {
                backupPath = FbxBackupService.CreateBackup(targetPath);
            }

            var exporter = FbxExporter.Create(_manager, "");
            try
            {
                if (!exporter.Initialize(targetPath, -1, _manager.GetIOSettings()))
                {
                    throw new InvalidOperationException(
                        $"Failed to initialize FBX exporter for '{targetPath}': {exporter.GetStatus().GetErrorString()}");
                }

                if (!exporter.Export(_scene))
                {
                    throw new InvalidOperationException($"Failed to export FBX scene to '{targetPath}'.");
                }
            }
            finally
            {
                exporter.Destroy();
            }

            IsDirty = false;
            ChangeLog.Clear();
            return backupPath;
        }

        public void RecordChange(FbxChangeKind kind, string nodePath, string oldValue, string newValue)
        {
            ChangeLog.Record(kind, nodePath, oldValue, newValue);
            IsDirty = true;
        }

        public void Close()
        {
            if (_manager != null)
            {
                _manager.Destroy();
                _manager = null;
            }

            _scene = null;
            FilePath = null;
            IsDirty = false;
            HasAnimation = HasSkinning = HasBlendShapes = false;
            OpenDocuments.Remove(this);
        }

        public void Dispose()
        {
            Close();
        }

        private void ScanRiskyContent()
        {
            HasAnimation = _scene.GetSrcObjectCount<FbxAnimStack>() > 0;
            HasSkinning = false;
            HasBlendShapes = false;

            ScanNodeForDeformers(_scene.GetRootNode());
        }

        private void ScanNodeForDeformers(FbxNode node)
        {
            if (node == null)
            {
                return;
            }

            var attribute = node.GetNodeAttribute();
            if (attribute != null && attribute.GetAttributeType() == FbxNodeAttribute.EType.eMesh)
            {
                var mesh = (FbxMesh)attribute;
                if (mesh.GetDeformerCount(FbxDeformer.EDeformerType.eSkin) > 0)
                {
                    HasSkinning = true;
                }
                if (mesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape) > 0)
                {
                    HasBlendShapes = true;
                }
            }

            for (var i = 0; i < node.GetChildCount(); i++)
            {
                ScanNodeForDeformers(node.GetChild(i));
            }
        }
    }
}
