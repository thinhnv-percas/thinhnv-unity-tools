using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MergeMeshUnity
{
    public class MergeMeshWindow : EditorWindow
    {
        private const string LastSaveFolderKey = "MergeMeshWindow_Settings";
        private const string LegacySaveFolderKey = "MergeMeshWindow_LastSaveFolder";
        private enum PivotMode
        {
            Origin,
            FirstMesh,
            SelectionCenter,
            MeshBoundsCenter
        }

        [System.Serializable]
        private class MergeMeshSettings
        {
            public string saveFolder = "Assets";
            public string outputAssetName = "merged_mesh";
            public bool overwriteExisting = true;
            public bool applyScale = false;
            public Vector3 scale = Vector3.one;
            public int pivotMode = (int)PivotMode.Origin;
            public bool createCollider = true;
            public bool reprojectNormals = true;
            public bool recalculateTangents = false;
        }

        public List<MeshFilter> meshFilters = new List<MeshFilter>();
        private SerializedObject serializedObject;
        private Vector2 listScroll;
        private MergeMeshSettings settings = new MergeMeshSettings();

        public void OnEnable()
        {
            this.serializedObject = new SerializedObject(this);
            this.LoadSettings();
        }

        public void OnGUI()
        {
            this.listField();
            this.addSelectedToList();
            this.clearList();
            this.drawMergeSettings();
            this.SaveSettings();
            this.mergeMesh();
            this.exportToFile();
        }

        private void listField()
        {
            this.serializedObject.Update();
            var listPorp = this.serializedObject.FindProperty("meshFilters");

            this.listScroll = EditorGUILayout.BeginScrollView(this.listScroll);
            EditorGUILayout.PropertyField(listPorp, true);
            EditorGUILayout.EndScrollView();

            this.serializedObject.ApplyModifiedProperties();
            this.nullCheck();
        }

        private void nullCheck()
        {
            this.meshFilters.RemoveAll(t => t == null);
        }

        private void addSelectedToList()
        {
            if (GUILayout.Button("Add to list"))
            {
                var gos = Selection.gameObjects;
                foreach (var go in gos)
                {
                    var filters = go.GetComponentsInChildren<MeshFilter>();
                    foreach (var filter in filters)
                    {
                        if (!this.meshFilters.Contains(filter))
                        {
                            this.meshFilters.Add(filter);
                        }
                    }
                }
            }
        }

        private void clearList()
        {
            if (GUILayout.Button("Clear list"))
            {
                this.meshFilters.Clear();
            }
        }

        private void drawMergeSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Merge Settings", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            this.settings.saveFolder = EditorGUILayout.TextField("Save Folder", this.settings.saveFolder);
            if (GUILayout.Button("Browse", GUILayout.Width(80)))
            {
                var absolute = EditorUtility.OpenFolderPanel("Choose Target Folder", Application.dataPath, "");
                if (!string.IsNullOrEmpty(absolute) && absolute.StartsWith(Application.dataPath))
                {
                    this.settings.saveFolder = "Assets" + absolute.Substring(Application.dataPath.Length).Replace('\\', '/');
                }
                else if (!string.IsNullOrEmpty(absolute))
                {
                    EditorUtility.DisplayDialog("Invalid Folder", "Please select a folder inside this Unity project.", "OK");
                }
            }
            EditorGUILayout.EndHorizontal();

            this.settings.outputAssetName = EditorGUILayout.TextField("Output Name", this.settings.outputAssetName);
            this.settings.overwriteExisting = EditorGUILayout.Toggle("Overwrite Existing Asset", this.settings.overwriteExisting);
            this.settings.applyScale = EditorGUILayout.Toggle("Apply Scale", this.settings.applyScale);
            if (this.settings.applyScale)
            {
                this.settings.scale = EditorGUILayout.Vector3Field("Scale", this.settings.scale);
            }

            this.settings.pivotMode = (int)(PivotMode)EditorGUILayout.EnumPopup("Pivot", (PivotMode)this.settings.pivotMode);
            this.settings.createCollider = EditorGUILayout.Toggle("Create MeshCollider", this.settings.createCollider);
            this.settings.reprojectNormals = EditorGUILayout.Toggle("Recalculate Normals", this.settings.reprojectNormals);
            this.settings.recalculateTangents = EditorGUILayout.Toggle("Recalculate Tangents", this.settings.recalculateTangents);

            if (this.meshFilters.Count == 0)
            {
                EditorGUILayout.HelpBox("Add meshes to the list before merging.", MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }

        private void exportToFile()
        {
            if (GUILayout.Button("Export OBJ"))
            {
                ObjExporterScript.DoExport(true);
                AssetDatabase.Refresh();
            }
        }

        private void mergeMesh()
        {
            if (GUILayout.Button("Merge Mesh"))
            {
                if (this.meshFilters.Count == 0)
                {
                    EditorUtility.DisplayDialog("Merge Mesh", "Please add at least one mesh to the list.", "OK");
                    return;
                }

                var validFilters = new List<MeshFilter>();
                foreach (var filter in this.meshFilters)
                {
                    if (filter != null && filter.sharedMesh != null)
                    {
                        validFilters.Add(filter);
                    }
                }

                if (validFilters.Count == 0)
                {
                    EditorUtility.DisplayDialog("Merge Mesh", "The selected meshes are invalid.", "OK");
                    return;
                }

                if (string.IsNullOrEmpty(this.settings.outputAssetName))
                {
                    this.settings.outputAssetName = "merged_mesh";
                }

                if (string.IsNullOrEmpty(this.settings.saveFolder) || !AssetDatabase.IsValidFolder(this.settings.saveFolder))
                {
                    EditorUtility.DisplayDialog("Merge Mesh", "Please choose a valid save folder.", "OK");
                    return;
                }

                var targetName = this.settings.outputAssetName.EndsWith(".asset") ? this.settings.outputAssetName : this.settings.outputAssetName + ".asset";
                var targetPath = Path.Combine(this.settings.saveFolder, targetName).Replace("\\", "/");
                targetPath = this.settings.overwriteExisting ? targetPath : AssetDatabase.GenerateUniqueAssetPath(targetPath);

                if (AssetDatabase.LoadAssetAtPath<Mesh>(targetPath) != null && this.settings.overwriteExisting)
                {
                    AssetDatabase.DeleteAsset(targetPath);
                }

                var pivotPoint = this.GetPivotPoint(validFilters);
                var combine = new List<CombineInstance>();
                bool hasCollider = false;
                for (int i = 0; i < validFilters.Count; i++)
                {
                    var filter = validFilters[i];
                    if (filter == null || filter.sharedMesh == null)
                    {
                        continue;
                    }

                    var instance = new CombineInstance
                    {
                        mesh = filter.sharedMesh,
                        transform = Matrix4x4.Translate(-pivotPoint) * filter.transform.localToWorldMatrix
                    };
                    combine.Add(instance);

                    filter.gameObject.SetActive(false);
                    if (filter.GetComponent<MeshCollider>())
                    {
                        hasCollider = true;
                    }
                }

                var meshName = string.IsNullOrWhiteSpace(this.settings.outputAssetName) ? "merged_mesh" : this.settings.outputAssetName;
                var go = new GameObject(meshName)
                {
                    layer = validFilters[0].gameObject.layer
                };

                var filterComponent = go.AddComponent<MeshFilter>();
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = validFilters[0].GetComponent<MeshRenderer>()?.sharedMaterial;

                var mesh = new Mesh { name = meshName };
                filterComponent.sharedMesh = mesh;
                mesh.CombineMeshes(combine.ToArray(), true, true);

                if (this.settings.applyScale)
                {
                    this.ApplyScale(mesh, pivotPoint);
                }

                if (this.settings.reprojectNormals)
                {
                    mesh.RecalculateNormals();
                }

                if (this.settings.recalculateTangents && mesh.HasVertexAttribute(VertexAttribute.Tangent))
                {
                    mesh.RecalculateTangents();
                }

                if (this.settings.createCollider || hasCollider)
                {
                    var collider = go.AddComponent<MeshCollider>();
                    collider.sharedMesh = mesh;
                }

                AssetDatabase.CreateAsset(mesh, targetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                this.SaveSettings();

                Selection.activeObject = AssetDatabase.LoadAssetAtPath<Mesh>(targetPath);
                Selection.activeGameObject = go;
            }
        }

        private Vector3 GetPivotPoint(List<MeshFilter> filters)
        {
            if (filters == null || filters.Count == 0)
            {
                return Vector3.zero;
            }

            switch ((PivotMode)this.settings.pivotMode)
            {
                case PivotMode.FirstMesh:
                    return filters[0].transform.position;
                case PivotMode.SelectionCenter:
                    var center = Vector3.zero;
                    foreach (var filter in filters)
                    {
                        center += filter.transform.position;
                    }
                    return center / filters.Count;
                case PivotMode.MeshBoundsCenter:
                    var bounds = new Bounds();
                    var hasBounds = false;
                    foreach (var filter in filters)
                    {
                        if (filter == null || filter.sharedMesh == null)
                        {
                            continue;
                        }

                        var meshBounds = filter.sharedMesh.bounds;
                        var worldCenter = filter.transform.TransformPoint(meshBounds.center);
                        var worldExtents = filter.transform.TransformVector(meshBounds.extents);
                        var worldBounds = new Bounds(worldCenter, worldExtents * 2f);
                        if (!hasBounds)
                        {
                            bounds = worldBounds;
                            hasBounds = true;
                        }
                        else
                        {
                            bounds.Encapsulate(worldBounds);
                        }
                    }
                    return hasBounds ? bounds.center : Vector3.zero;
                case PivotMode.Origin:
                default:
                    return Vector3.zero;
            }
        }

        private void ApplyScale(Mesh mesh, Vector3 pivotPoint)
        {
            var vertices = mesh.vertices;
            for (var i = 0; i < vertices.Length; i++)
            {
                vertices[i] = Vector3.Scale(vertices[i] - pivotPoint, this.settings.scale) + pivotPoint;
            }

            mesh.vertices = vertices;
            mesh.RecalculateBounds();
        }

        private void LoadSettings()
        {
            var json = EditorPrefs.GetString(LastSaveFolderKey, string.Empty);
            if (string.IsNullOrEmpty(json))
            {
                var legacyFolder = EditorPrefs.GetString(LegacySaveFolderKey, "Assets");
                this.settings.saveFolder = legacyFolder;
                return;
            }

            try
            {
                this.settings = JsonUtility.FromJson<MergeMeshSettings>(json);
            }
            catch
            {
                this.settings = new MergeMeshSettings();
            }
        }

        private void SaveSettings()
        {
            var json = JsonUtility.ToJson(this.settings, true);
            EditorPrefs.SetString(LastSaveFolderKey, json);
        }

        [MenuItem("Tools/Thinhnv/Merge and Export Mesh")]
        public static void ShowWindow()
        {
            var window = (MergeMeshWindow)GetWindow(typeof(MergeMeshWindow));
            window.minSize = new Vector2(50, 250);
            window.Show();
        }
    }
}