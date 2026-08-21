using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class BulkRenameWindow : EditorWindow
{
    [Serializable]
    private class RenameSection
    {
        public string Title;

        public string FindText = "";
        public string ReplaceText = "";
        public bool IgnoreCase = true;

        public Vector2 Scroll;

        public readonly List<RenameInfo> PreviewList = new();

        public RenameSection(string title)
        {
            Title = title;
        }
    }

    private class RenameInfo
    {
        public UnityEngine.Object Asset;
        public string OldName;
        public string NewName;
    }

    private readonly RenameSection renameSection1 = new("Bulk Rename Assets");
    private readonly RenameSection renameSection2 = new("Bulk Rename Assets");

    [MenuItem("Tools/Thinhnv/Bulk Rename")]
    private static void Open()
    {
        GetWindow<BulkRenameWindow>("Bulk Rename");
    }

    private void OnEnable()
    {
        RefreshPreview(renameSection1);
        RefreshPreview(renameSection2);
    }

    private void OnSelectionChange()
    {
        RefreshPreview(renameSection1);
        RefreshPreview(renameSection2);
        Repaint();
    }

    private void OnGUI()
    {
        DrawRenameSection(renameSection1);
        DrawRenameSection(renameSection2);
    }

    private void DrawRenameSection(RenameSection section)
    {
        EditorGUILayout.Space();

        EditorGUILayout.LabelField(section.Title, EditorStyles.boldLabel);

        EditorGUILayout.HelpBox(
            $"Selected Assets : {Selection.objects.Length}",
            MessageType.Info);

        EditorGUI.BeginChangeCheck();

        section.FindText = EditorGUILayout.TextField("Find", section.FindText);
        section.ReplaceText = EditorGUILayout.TextField("Replace", section.ReplaceText);
        section.IgnoreCase = EditorGUILayout.Toggle("Ignore Case", section.IgnoreCase);

        if (EditorGUI.EndChangeCheck())
        {
            RefreshPreview(section);
        }

        GUILayout.Space(10);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Refresh"))
            {
                RefreshPreview(section);
            }

            GUI.enabled = section.PreviewList.Count > 0;

            if (GUILayout.Button($"Rename ({section.PreviewList.Count})"))
            {
                RenameAssets(section);
            }

            GUI.enabled = true;
        }

        GUILayout.Space(10);

        EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

        section.Scroll = EditorGUILayout.BeginScrollView(section.Scroll, GUILayout.Height(200));

        foreach (var item in section.PreviewList)
        {
            EditorGUILayout.BeginHorizontal();

            GUILayout.Label(item.OldName, GUILayout.Width(position.width * 0.4f));

            GUILayout.Label("→", GUILayout.Width(20));

            GUI.color = Color.green;
            GUILayout.Label(item.NewName);
            GUI.color = Color.white;

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }

    private void RefreshPreview(RenameSection section)
    {
        section.PreviewList.Clear();

        foreach (var obj in Selection.objects)
        {
            if (obj == null)
                continue;

            string oldName = obj.name;
            string newName = Replace(oldName, section);

            if (oldName == newName)
                continue;

            section.PreviewList.Add(new RenameInfo
            {
                Asset = obj,
                OldName = oldName,
                NewName = newName
            });
        }
    }

    private string Replace(string input, RenameSection section)
    {
        if (string.IsNullOrEmpty(section.FindText))
            return input;

        if (section.IgnoreCase)
        {
            return input.Replace(
                section.FindText,
                section.ReplaceText,
                StringComparison.OrdinalIgnoreCase);
        }

        return input.Replace(section.FindText, section.ReplaceText);
    }

    private void RenameAssets(RenameSection section)
    {
        if (section.PreviewList.Count == 0)
            return;

        if (!EditorUtility.DisplayDialog(
                section.Title,
                $"Rename {section.PreviewList.Count} assets?",
                "Rename",
                "Cancel"))
        {
            return;
        }

        AssetDatabase.StartAssetEditing();

        try
        {
            foreach (var item in section.PreviewList)
            {
                string path = AssetDatabase.GetAssetPath(item.Asset);

                if (string.IsNullOrEmpty(path))
                    continue;

                string error = AssetDatabase.RenameAsset(path, item.NewName);

                if (!string.IsNullOrEmpty(error))
                {
                    Debug.LogError($"Rename failed: {item.OldName}\n{error}");
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        RefreshPreview(renameSection1);
        RefreshPreview(renameSection2);
    }
}