using System.IO;
using UnityEditor;
using UnityEngine;

public static class UITextureExporterUtility
{
    public static string AssetPathToAbsolutePath(string assetPath)
    {
        string projectPath =
            Directory.GetParent(Application.dataPath).FullName;

        return Path.GetFullPath(
            Path.Combine(projectPath, assetPath));
    }

    public static string ConvertAbsolutePathToAssetPath(
        string absolutePath)
    {
        absolutePath =
            absolutePath.Replace("\\", "/");

        string dataPath =
            Application.dataPath.Replace("\\", "/");

        if (absolutePath.StartsWith(dataPath))
        {
            return "Assets" +
                   absolutePath.Substring(dataPath.Length);
        }

        return absolutePath;
    }

    public static void EnsureFolderExists(string assetFolder)
    {
        string absolute =
            AssetPathToAbsolutePath(assetFolder);

        if (!Directory.Exists(absolute))
            Directory.CreateDirectory(absolute);

        AssetDatabase.Refresh();
    }

    public static string SanitizeFileName(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "UI_Texture";

        foreach (char c in Path.GetInvalidFileNameChars())
            value = value.Replace(c.ToString(), "_");

        return value;
    }

    public static void ShowTexturePreview(
        Texture2D texture,
        string title)
    {
        if (texture == null)
            return;

        UITextureExporterPreviewWindow window =
            EditorWindow.GetWindow<UITextureExporterPreviewWindow>(
                false,
                "UI Texture Preview",
                true);

        window.titleContent =
            new GUIContent(
                string.IsNullOrEmpty(title)
                    ? "UI Texture Preview"
                    : title);

        window.SetTexture(texture);
        window.Show();
    }
}

public sealed class UITextureExporterPreviewWindow : EditorWindow
{
    private Texture2D _texture;
    private float _zoom = 1f;
    private bool _checkerboard = true;

    public void SetTexture(Texture2D texture)
    {
        // Release the previous temporary preview texture when replacing it.
        if (_texture != null && _texture != texture)
        {
            UnityEngine.Object.DestroyImmediate(_texture);
        }

        _texture = texture;
        _zoom = 1f;
        Repaint();
    }

    private void OnEnable()
    {
        minSize = new Vector2(360f, 260f);
    }

    private void OnGUI()
    {
        DrawToolbar();

        if (_texture == null)
        {
            EditorGUILayout.HelpBox(
                "Texture was destroyed.",
                MessageType.Warning);
            return;
        }

        EditorGUILayout.LabelField(
            $"{_texture.width} x {_texture.height}",
            EditorStyles.miniLabel);

        Rect viewport =
            GUILayoutUtility.GetRect(
                GUIContent.none,
                GUIStyle.none,
                GUILayout.ExpandWidth(true),
                GUILayout.ExpandHeight(true));

        if (Event.current.type == EventType.Repaint)
        {
            EditorGUI.DrawRect(
                viewport,
                new Color(0.16f, 0.16f, 0.16f, 1f));

            if (_checkerboard)
                DrawCheckerboard(viewport);

            float drawWidth = _texture.width * _zoom;
            float drawHeight = _texture.height * _zoom;

            Rect imageRect = new Rect(
                viewport.center.x - drawWidth * 0.5f,
                viewport.center.y - drawHeight * 0.5f,
                drawWidth,
                drawHeight);

            EditorGUI.DrawTextureTransparent(
                imageRect,
                _texture,
                ScaleMode.StretchToFill);
        }

        HandleZoom(viewport);
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            _checkerboard =
                GUILayout.Toggle(
                    _checkerboard,
                    "Checker",
                    EditorStyles.toolbarButton,
                    GUILayout.Width(70));

            GUILayout.Label(
                "Zoom",
                GUILayout.Width(38));

            _zoom =
                GUILayout.HorizontalSlider(
                    _zoom,
                    0.1f,
                    8f,
                    GUILayout.Width(140));

            _zoom =
                EditorGUILayout.FloatField(
                    _zoom,
                    GUILayout.Width(55));

            _zoom = Mathf.Clamp(_zoom, 0.1f, 8f);

            if (GUILayout.Button("Fit", EditorStyles.toolbarButton, GUILayout.Width(45)))
            {
                _zoom = CalculateFitZoom();
            }

            if (GUILayout.Button("1:1", EditorStyles.toolbarButton, GUILayout.Width(45)))
            {
                _zoom = 1f;
            }
        }
    }

    private float CalculateFitZoom()
    {
        if (_texture == null)
            return 1f;

        float width = Mathf.Max(1f, position.width - 20f);
        float height = Mathf.Max(1f, position.height - 70f);

        return Mathf.Clamp(
            Mathf.Min(
                width / _texture.width,
                height / _texture.height),
            0.1f,
            8f);
    }

    private static void DrawCheckerboard(Rect rect)
    {
        const int size = 12;
        Color a = new Color(0.29f, 0.29f, 0.29f, 1f);
        Color b = new Color(0.23f, 0.23f, 0.23f, 1f);

        int columns = Mathf.CeilToInt(rect.width / size);
        int rows = Mathf.CeilToInt(rect.height / size);

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                EditorGUI.DrawRect(
                    new Rect(
                        rect.x + x * size,
                        rect.y + y * size,
                        size,
                        size),
                    ((x + y) & 1) == 0 ? a : b);
            }
        }
    }

    private void HandleZoom(Rect viewport)
    {
        Event e = Event.current;

        if (!viewport.Contains(e.mousePosition))
            return;

        if (e.type == EventType.ScrollWheel)
        {
            float oldZoom = _zoom;
            float direction = e.delta.y < 0f ? 1.15f : 1f / 1.15f;
            _zoom = Mathf.Clamp(_zoom * direction, 0.1f, 8f);

            if (!Mathf.Approximately(oldZoom, _zoom))
            {
                e.Use();
                Repaint();
            }
        }
    }

    private void OnDestroy()
    {
        if (_texture != null)
        {
            UnityEngine.Object.DestroyImmediate(_texture);
            _texture = null;
        }
    }
}
