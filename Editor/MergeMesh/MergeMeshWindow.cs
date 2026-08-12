using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            public bool removeHiddenFaces = false;
            public float hiddenFaceDistance = 0.01f;
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

            EditorGUILayout.Space(4);
            this.settings.removeHiddenFaces = EditorGUILayout.Toggle("Remove Hidden Faces", this.settings.removeHiddenFaces);
            if (this.settings.removeHiddenFaces)
            {
                EditorGUI.indentLevel++;
                this.settings.hiddenFaceDistance = EditorGUILayout.Slider("Distance Threshold", this.settings.hiddenFaceDistance, 0.001f, 0.1f);
                EditorGUI.indentLevel--;
            }

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

                if (this.settings.removeHiddenFaces)
                {
                    RemoveHiddenFaces(mesh, this.settings.hiddenFaceDistance);
                }

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

        private static void RemoveHiddenFaces(Mesh mesh, float distanceThreshold)
        {
            var verts = mesh.vertices;
            var tris = mesh.triangles;
            int triCount = tris.Length / 3;
            if (triCount == 0) return;

            var centers = new Vector3[triCount];
            var faceNormals = new Vector3[triCount];

            for (int i = 0; i < triCount; i++)
            {
                var v0 = verts[tris[i * 3]];
                var v1 = verts[tris[i * 3 + 1]];
                var v2 = verts[tris[i * 3 + 2]];
                centers[i] = (v0 + v1 + v2) / 3f;
                var cross = Vector3.Cross(v1 - v0, v2 - v0);
                faceNormals[i] = cross.sqrMagnitude > 1e-12f ? cross.normalized : Vector3.zero;
            }

            float cellSize = Mathf.Max(distanceThreshold * 2f, 0.001f);
            var grid = new Dictionary<Vector3Int, List<int>>();

            for (int i = 0; i < triCount; i++)
            {
                var cell = new Vector3Int(
                    Mathf.FloorToInt(centers[i].x / cellSize),
                    Mathf.FloorToInt(centers[i].y / cellSize),
                    Mathf.FloorToInt(centers[i].z / cellSize));

                if (!grid.TryGetValue(cell, out var bucket))
                {
                    bucket = new List<int>();
                    grid[cell] = bucket;
                }
                bucket.Add(i);
            }

            var hidden = new bool[triCount];
            float distSq = distanceThreshold * distanceThreshold;

            for (int i = 0; i < triCount; i++)
            {
                if (hidden[i] || faceNormals[i] == Vector3.zero) continue;

                int cx = Mathf.FloorToInt(centers[i].x / cellSize);
                int cy = Mathf.FloorToInt(centers[i].y / cellSize);
                int cz = Mathf.FloorToInt(centers[i].z / cellSize);
                bool found = false;

                for (int dx = -1; dx <= 1 && !found; dx++)
                for (int dy = -1; dy <= 1 && !found; dy++)
                for (int dz = -1; dz <= 1 && !found; dz++)
                {
                    var neighborCell = new Vector3Int(cx + dx, cy + dy, cz + dz);
                    if (!grid.TryGetValue(neighborCell, out var bucket)) continue;

                    foreach (int j in bucket)
                    {
                        if (j <= i || hidden[j] || faceNormals[j] == Vector3.zero) continue;

                        if ((centers[i] - centers[j]).sqrMagnitude > distSq) continue;

                        float dot = Vector3.Dot(faceNormals[i], faceNormals[j]);
                        if (dot < -0.7f)
                        {
                            hidden[i] = true;
                            hidden[j] = true;
                            found = true;
                            break;
                        }
                        if (dot > 0.95f)
                        {
                            hidden[j] = true;
                        }
                    }
                }
            }

            for (int i = 0; i < triCount; i++)
            {
                if (faceNormals[i] == Vector3.zero) hidden[i] = true;
            }

            int removedCount = hidden.Count(h => h);
            if (removedCount == 0) return;

            var norms = mesh.normals;
            var tangents = mesh.tangents;
            var uv0 = mesh.uv;
            var uv1 = mesh.uv2;
            var colors = mesh.colors;
            bool hasNorms = norms.Length == verts.Length;
            bool hasTangents = tangents.Length == verts.Length;
            bool hasUV0 = uv0.Length == verts.Length;
            bool hasUV1 = uv1.Length == verts.Length;
            bool hasColors = colors.Length == verts.Length;

            var newTris = new List<int>(tris.Length);
            for (int i = 0; i < triCount; i++)
            {
                if (hidden[i]) continue;
                newTris.Add(tris[i * 3]);
                newTris.Add(tris[i * 3 + 1]);
                newTris.Add(tris[i * 3 + 2]);
            }

            var usedVerts = new bool[verts.Length];
            foreach (int idx in newTris) usedVerts[idx] = true;

            var remap = new int[verts.Length];
            int newCount = 0;
            for (int i = 0; i < verts.Length; i++)
                remap[i] = usedVerts[i] ? newCount++ : -1;

            var compactVerts = new Vector3[newCount];
            var compactNorms = hasNorms ? new Vector3[newCount] : null;
            var compactTangents = hasTangents ? new Vector4[newCount] : null;
            var compactUV0 = hasUV0 ? new Vector2[newCount] : null;
            var compactUV1 = hasUV1 ? new Vector2[newCount] : null;
            var compactColors = hasColors ? new Color[newCount] : null;

            for (int i = 0; i < verts.Length; i++)
            {
                if (!usedVerts[i]) continue;
                int ni = remap[i];
                compactVerts[ni] = verts[i];
                if (compactNorms != null) compactNorms[ni] = norms[i];
                if (compactTangents != null) compactTangents[ni] = tangents[i];
                if (compactUV0 != null) compactUV0[ni] = uv0[i];
                if (compactUV1 != null) compactUV1[ni] = uv1[i];
                if (compactColors != null) compactColors[ni] = colors[i];
            }

            for (int i = 0; i < newTris.Count; i++)
                newTris[i] = remap[newTris[i]];

            mesh.Clear();
            mesh.indexFormat = newCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = compactVerts;
            if (compactNorms != null) mesh.normals = compactNorms;
            if (compactTangents != null) mesh.tangents = compactTangents;
            if (compactUV0 != null) mesh.uv = compactUV0;
            if (compactUV1 != null) mesh.uv2 = compactUV1;
            if (compactColors != null) mesh.colors = compactColors;
            mesh.SetTriangles(newTris, 0);
            mesh.RecalculateBounds();

            int removedVerts = verts.Length - newCount;
            Debug.Log($"[MergeMesh] Removed {removedCount} hidden triangles, {removedVerts} unused vertices " +
                      $"({triCount} → {triCount - removedCount} tris, {verts.Length} → {newCount} verts)");
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