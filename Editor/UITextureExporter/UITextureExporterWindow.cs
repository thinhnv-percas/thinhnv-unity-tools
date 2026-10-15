using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class UITextureExporterWindow : EditorWindow
{
    private GameObject _source;
    private bool _includeInactive = true;
    private bool _transparentBackground = true;
    private int _scale = 1;
    private int _maxSize = 4096;
    private string _outputFolder = "Assets/_Export/UI";

    private UITextureExporterScanner.ScanResult _scanResult;
    private readonly Dictionary<int, bool> _rendererSelection = new Dictionary<int, bool>();
    private Vector2 _scroll;

    [MenuItem("Tools/UI Texture Exporter")]
    public static void Open()
    {
        var window = GetWindow<UITextureExporterWindow>();
        window.titleContent = new GUIContent("UI Texture Exporter");
        window.minSize = new Vector2(500, 620);
        window.Show();
    }

    private void OnEnable()
    {
        if (_source == null)
            _source = Selection.activeGameObject;

        if (_source != null)
            Scan();
    }

    private void OnGUI()
    {
        DrawSource();
        EditorGUILayout.Space(8);
        DrawSettings();
        EditorGUILayout.Space(8);
        DrawScan();
        EditorGUILayout.Space(8);
        DrawResults();
        EditorGUILayout.Space(8);
        DrawExport();
    }

    private void DrawSource()
    {
        EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);

        GameObject selected =
            (GameObject)EditorGUILayout.ObjectField(
                "GameObject",
                _source,
                typeof(GameObject),
                true);

        if (selected != _source)
        {
            _source = selected;
            Scan();
        }

        if (_source == null)
        {
            EditorGUILayout.HelpBox(
                "Select a GameObject under a Canvas.",
                MessageType.Info);
        }
    }

    private void DrawSettings()
    {
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);

        _includeInactive = EditorGUILayout.Toggle(
            "Include Inactive",
            _includeInactive);

        _transparentBackground = EditorGUILayout.Toggle(
            "Transparent Background",
            _transparentBackground);

        _scale = EditorGUILayout.IntSlider(
            "Scale",
            _scale,
            1,
            4);

        _maxSize = Mathf.Max(
            64,
            EditorGUILayout.IntField(
                "Max Size",
                _maxSize));

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PrefixLabel("Output Folder");
        _outputFolder = EditorGUILayout.TextField(_outputFolder);

        if (GUILayout.Button("...", GUILayout.Width(28)))
        {
            string absolute =
                EditorUtility.OpenFolderPanel(
                    "Select Output Folder",
                    Application.dataPath,
                    "");

            if (!string.IsNullOrEmpty(absolute))
                _outputFolder =
                    UITextureExporterUtility.ConvertAbsolutePathToAssetPath(
                        absolute);
        }

        EditorGUILayout.EndHorizontal();
    }

    private void DrawScan()
    {
        EditorGUILayout.LabelField("Scan CanvasRenderer", EditorStyles.boldLabel);

        if (GUILayout.Button("Scan / Refresh", GUILayout.Height(30)))
            Scan();
    }

    private void DrawResults()
    {
        int count =
            _scanResult != null
                ? _scanResult.renderers.Count
                : 0;

        int selectedCount = GetSelectedCount();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(
            $"CanvasRenderer: {selectedCount}/{count} selected",
            EditorStyles.boldLabel);

        if (count > 0)
        {
            if (GUILayout.Button("All", GUILayout.Width(52)))
                SetAllRendererSelection(true);

            if (GUILayout.Button("None", GUILayout.Width(52)))
                SetAllRendererSelection(false);
        }

        EditorGUILayout.EndHorizontal();

        if (_scanResult == null || count == 0)
        {
            EditorGUILayout.HelpBox(
                "No CanvasRenderer found.",
                MessageType.Warning);

            return;
        }

        _scroll = EditorGUILayout.BeginScrollView(
            _scroll,
            GUILayout.MinHeight(260));

        for (int i = 0; i < count; i++)
        {
            var e = _scanResult.renderers[i];

            using (new EditorGUILayout.HorizontalScope())
            {
                bool newSelected = EditorGUILayout.Toggle(
                    e.selected,
                    GUILayout.Width(20));

                if (newSelected != e.selected)
                {
                    e.selected = newSelected;
                    _rendererSelection[e.InstanceId] = newSelected;
                    Repaint();
                }

                string kind = e.IsTextMeshPro ? "TMP" : "UI";

                EditorGUILayout.LabelField(
                    $"{i + 1}. {e.Name}",
                    GUILayout.Width(190));

                EditorGUILayout.LabelField(
                    kind,
                    GUILayout.Width(42));

                EditorGUILayout.LabelField(
                    $"Depth {e.depth}",
                    GUILayout.Width(70));

                EditorGUILayout.LabelField(
                    e.culled ? "CULL" : "VISIBLE",
                    GUILayout.Width(70));

                EditorGUILayout.LabelField(
                    $"Alpha {e.inheritedAlpha:0.###}",
                    GUILayout.Width(88));

                EditorGUILayout.LabelField(
                    e.mesh != null
                        ? $"{e.mesh.vertexCount} verts"
                        : "No Mesh",
                    GUILayout.Width(78));

                string materialName =
                    e.material != null
                        ? e.material.name
                        : "<None>";

                EditorGUILayout.LabelField(
                    materialName,
                    EditorStyles.miniLabel);

                if (e.material != null &&
                    GUILayout.Button("Ping", GUILayout.Width(42)))
                {
                    EditorGUIUtility.PingObject(e.material);
                    Selection.activeObject = e.material;
                }
            }
        }

        EditorGUILayout.EndScrollView();
    }

    private int GetSelectedCount()
    {
        if (_scanResult == null)
            return 0;

        int count = 0;

        for (int i = 0; i < _scanResult.renderers.Count; i++)
        {
            if (_scanResult.renderers[i] != null &&
                _scanResult.renderers[i].selected)
            {
                count++;
            }
        }

        return count;
    }

    private void SetAllRendererSelection(bool selected)
    {
        if (_scanResult == null)
            return;

        for (int i = 0; i < _scanResult.renderers.Count; i++)
        {
            if (_scanResult.renderers[i] != null)
            {
                _scanResult.renderers[i].selected = selected;
                _rendererSelection[_scanResult.renderers[i].InstanceId] = selected;
            }
        }

        Repaint();
    }

    private HashSet<int> GetSelectedRendererIds()
    {
        var ids = new HashSet<int>();

        if (_scanResult == null)
            return ids;

        for (int i = 0; i < _scanResult.renderers.Count; i++)
        {
            var entry = _scanResult.renderers[i];

            if (entry == null ||
                !entry.selected ||
                entry.canvasRenderer == null)
            {
                continue;
            }

            ids.Add(entry.canvasRenderer.GetInstanceID());
        }

        return ids;
    }

    private void DrawExport()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.enabled =
                _source != null &&
                _scanResult != null &&
                _scanResult.renderers.Count > 0 &&
                GetSelectedCount() > 0;

            if (GUILayout.Button(
                    "Preview",
                    GUILayout.Height(38)))
            {
                Preview();
            }

            if (GUILayout.Button(
                    "Export PNG",
                    GUILayout.Height(38)))
            {
                Export();
            }

            GUI.enabled = true;
        }
    }

    private void Scan()
    {
        _scanResult = null;

        if (_source == null)
            return;

        // Generate TMP geometry/sub-renderers before building the selectable list.
        UITextureExporterRenderer.PrepareSource(_source);

        _scanResult =
            UITextureExporterScanner.Scan(
                _source,
                _includeInactive);

        for (int i = 0; i < _scanResult.renderers.Count; i++)
        {
            var entry = _scanResult.renderers[i];

            if (entry == null)
                continue;

            int id = entry.InstanceId;

            bool selected;
            if (_rendererSelection.TryGetValue(id, out selected))
                entry.selected = selected;
            else
                entry.selected = true;

            _rendererSelection[id] = entry.selected;
        }

        Repaint();
    }

    private void Preview()
    {
        Texture2D texture =
            UITextureExporterRenderer.Render(
                _source,
                GetSelectedRendererIds(),
                _scale,
                _maxSize,
                _transparentBackground);

        if (texture == null)
            return;

        UITextureExporterUtility.ShowTexturePreview(
            texture,
            _source.name);
    }

    private void Export()
    {
        if (_source == null)
            return;

        if (string.IsNullOrEmpty(_outputFolder))
        {
            EditorUtility.DisplayDialog(
                "UI Texture Exporter",
                "Output folder is empty.",
                "OK");

            return;
        }

        UITextureExporterUtility.EnsureFolderExists(
            _outputFolder);

        Texture2D texture =
            UITextureExporterRenderer.Render(
                _source,
                GetSelectedRendererIds(),
                _scale,
                _maxSize,
                _transparentBackground);

        if (texture == null)
            return;

        string fileName =
            UITextureExporterUtility.SanitizeFileName(
                _source.name);

        string assetPath =
            $"{_outputFolder}/{fileName}.png";

        string absolutePath =
            UITextureExporterUtility.AssetPathToAbsolutePath(
                assetPath);

        System.IO.File.WriteAllBytes(
            absolutePath,
            texture.EncodeToPNG());

        DestroyImmediate(texture);

        AssetDatabase.ImportAsset(
            assetPath,
            ImportAssetOptions.ForceUpdate);

        TextureImporter importer =
            AssetImporter.GetAtPath(assetPath)
                as TextureImporter;

        if (importer != null)
        {
            importer.textureType =
                TextureImporterType.Sprite;

            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;

            importer.SaveAndReimport();
        }

        AssetDatabase.Refresh();

        Object exported =
            AssetDatabase.LoadAssetAtPath<Texture2D>(
                assetPath);

        EditorGUIUtility.PingObject(exported);

        EditorUtility.DisplayDialog(
            "UI Texture Exporter",
            $"Exported:\n{assetPath}",
            "OK");
    }

    private void OnSelectionChange()
    {
        if (Selection.activeGameObject == null)
            return;

        _source = Selection.activeGameObject;
        Scan();
        Repaint();
    }
}
