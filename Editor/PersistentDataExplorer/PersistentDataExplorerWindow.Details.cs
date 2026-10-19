using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.PersistentDataExplorer
{
    public partial class PersistentDataExplorerWindow : ISerializationCallbackReceiver
    {
        private const long MaxPreviewBytes = 2 * 1024 * 1024;

        // Legacy IMGUI text rendering cuts off strings past ~16k characters, so longer text is
        // shown read-only (first chunk) instead of in an editable field that can't display it all.
        private const int MaxEditableChars = 15000;

        private const string RenameControlName = "PersistentDataExplorer.Rename";

        [SerializeField] private string previewPath;
        [SerializeField] private bool previewDirty;
        [SerializeField] private bool previewHasBom;
        [SerializeField] private long previewLastWriteTicks;
        [SerializeField] private string unsavedText;
        [SerializeField] private string renameBuffer = "";

        private string previewText;
        private string previewDisplayText;
        private bool previewIsText;
        private string previewNote;
        private Vector2 previewScroll;

        private string selectionInfo = "";
        private bool selectedExists;
        private bool selectedIsDirectory;
        private bool focusRename;
        private GUIStyle codeStyle;

        private bool PreviewEditable => previewText != null && previewText.Length <= MaxEditableChars;

        // Only unsaved edits need to survive a domain reload; a clean preview is just re-read from disk.
        public void OnBeforeSerialize()
        {
            unsavedText = previewDirty ? previewText : null;
        }

        public void OnAfterDeserialize()
        {
        }

        private void RestorePreviewAfterReload()
        {
            if (previewDirty && unsavedText != null && previewPath != null)
            {
                previewText = unsavedText;
                previewIsText = true;
                previewNote = null;
                UpdateDisplayText();
                hasUnsavedChanges = true;
            }
            else
            {
                LoadPreview(selectedPath);
            }

            if (string.IsNullOrEmpty(renameBuffer) && !string.IsNullOrEmpty(selectedPath))
            {
                renameBuffer = Path.GetFileName(selectedPath);
            }
        }

        private void DrawDetails()
        {
            if (string.IsNullOrEmpty(selectedPath))
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(
                    "Select a file to preview or edit it.\nDouble-click a folder to open it, or a file to open it externally.",
                    EditorStyles.centeredGreyMiniLabel);
                GUILayout.FlexibleSpace();
                return;
            }

            DrawRenameRow();
            EditorGUILayout.LabelField("persistentDataPath/" + RelativePath(selectedPath), EditorStyles.miniLabel);
            EditorGUILayout.LabelField(selectionInfo, EditorStyles.miniLabel);
            DrawActionButtons();

            if (selectedIsDirectory)
            {
                GUILayout.FlexibleSpace();
                return;
            }

            EditorGUILayout.Space(4);
            if (previewNote != null)
            {
                EditorGUILayout.HelpBox(previewNote, MessageType.Info);
            }

            if (previewIsText && previewPath == selectedPath)
            {
                DrawTextPreview();
            }
            else
            {
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawRenameRow()
        {
            Event e = Event.current;
            bool enterPressed = e.type == EventType.KeyDown
                                && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                                && GUI.GetNameOfFocusedControl() == RenameControlName;

            string currentName = Path.GetFileName(selectedPath);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(IconForPath(selectedPath, selectedIsDirectory), GUILayout.Width(18), GUILayout.Height(18));

                GUI.SetNextControlName(RenameControlName);
                renameBuffer = EditorGUILayout.TextField(renameBuffer);

                bool canRename = selectedExists && !string.IsNullOrWhiteSpace(renameBuffer) && renameBuffer != currentName;
                using (new EditorGUI.DisabledScope(!canRename))
                {
                    if (GUILayout.Button("Rename", GUILayout.Width(64)))
                    {
                        RequestRename();
                    }
                }

                if (enterPressed && canRename)
                {
                    RequestRename();
                    e.Use();
                }
            }

            if (focusRename && e.type == EventType.Repaint)
            {
                focusRename = false;
                EditorGUI.FocusTextInControl(RenameControlName);
                Repaint();
            }
        }

        private void RequestRename()
        {
            string path = selectedPath;
            string newName = renameBuffer;
            Defer(() => Rename(path, newName));
        }

        private void DrawActionButtons()
        {
            string path = selectedPath;
            bool isDir = selectedIsDirectory;

            using (new EditorGUI.DisabledScope(!selectedExists))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(isDir ? "Open Folder" : "Open", EditorStyles.miniButtonLeft))
                {
                    Defer(() => OpenEntry(path, isDir));
                }

                if (GUILayout.Button(RevealLabel, EditorStyles.miniButtonMid))
                {
                    EditorUtility.RevealInFinder(path);
                }

                if (GUILayout.Button("Copy Path", EditorStyles.miniButtonMid))
                {
                    EditorGUIUtility.systemCopyBuffer = ToNativePath(path);
                }

                if (GUILayout.Button("Duplicate", EditorStyles.miniButtonMid))
                {
                    Defer(() => Duplicate(path));
                }

                if (GUILayout.Button("Export...", EditorStyles.miniButtonMid))
                {
                    Defer(() => Export(path));
                }

                if (GUILayout.Button("Delete", EditorStyles.miniButtonRight))
                {
                    Defer(() => Delete(path));
                }
            }
        }

        private void DrawTextPreview()
        {
            codeStyle ??= new GUIStyle(EditorStyles.textArea) { wordWrap = false };
            bool editable = PreviewEditable;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(previewDirty ? "Content *" : "Content", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();

                if (LooksLikeJson(previewPath, previewText)
                    && GUILayout.Button("Format JSON", EditorStyles.miniButton, GUILayout.Width(84)))
                {
                    string formatted = FormatJson(previewText);
                    if (formatted != previewText)
                    {
                        ReplacePreviewText(formatted);
                    }
                }

                using (new EditorGUI.DisabledScope(!previewDirty))
                {
                    if (GUILayout.Button("Revert", EditorStyles.miniButtonLeft, GUILayout.Width(56)))
                    {
                        string path = previewPath;
                        Defer(() => LoadPreview(path));
                    }

                    if (GUILayout.Button("Save", EditorStyles.miniButtonRight, GUILayout.Width(56)))
                    {
                        Defer(() => SavePreview());
                    }
                }
            }

            if (!editable)
            {
                EditorGUILayout.HelpBox(
                    $"Showing the first {MaxEditableChars:N0} of {previewText.Length:N0} characters, read-only — too long " +
                    "to edit inline. Use Open to edit the whole file in an external editor (Save still writes the full text).",
                    MessageType.None);
            }

            previewScroll = EditorGUILayout.BeginScrollView(previewScroll);
            if (editable)
            {
                EditorGUI.BeginChangeCheck();
                string edited = EditorGUILayout.TextArea(previewText, codeStyle, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
                if (EditorGUI.EndChangeCheck())
                {
                    previewText = edited;
                    UpdateDisplayText();
                    SetPreviewDirty(true);
                }
            }
            else
            {
                // Return value deliberately ignored: selectable/copyable, but not editable.
                EditorGUILayout.TextArea(previewDisplayText, codeStyle, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            }

            EditorGUILayout.EndScrollView();
        }

        private void LoadPreview(string path)
        {
            previewPath = path != null && File.Exists(path) ? path : null;
            previewText = null;
            previewDisplayText = null;
            previewIsText = false;
            previewNote = null;
            previewHasBom = false;
            previewScroll = Vector2.zero;
            SetPreviewDirty(false);

            if (previewPath == null)
            {
                return;
            }

            try
            {
                var info = new FileInfo(previewPath);
                previewLastWriteTicks = info.LastWriteTimeUtc.Ticks;
                if (info.Length > MaxPreviewBytes)
                {
                    previewNote = $"{FormatBytes(info.Length)} — too large to preview here. Use Open to view it in an external app.";
                    return;
                }

                byte[] bytes = File.ReadAllBytes(previewPath);
                if (LooksBinary(bytes))
                {
                    previewNote = "Binary file — no text preview. Use Open or Export to inspect it.";
                    return;
                }

                previewHasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                int offset = previewHasBom ? 3 : 0;
                previewText = new UTF8Encoding(false).GetString(bytes, offset, bytes.Length - offset);
                previewIsText = true;
                UpdateDisplayText();
            }
            catch (Exception e) when (IsIoException(e))
            {
                previewNote = $"Could not read file: {e.Message}";
            }
        }

        private void RefreshPreviewFromDisk()
        {
            if (previewPath == null || previewDirty)
            {
                return;
            }

            if (!File.Exists(previewPath))
            {
                LoadPreview(null);
                return;
            }

            if (File.GetLastWriteTimeUtc(previewPath).Ticks != previewLastWriteTicks)
            {
                LoadPreview(previewPath);
            }
        }

        private bool SavePreview()
        {
            if (!previewDirty)
            {
                return true;
            }

            if (previewPath == null || previewText == null)
            {
                return false;
            }

            string path = previewPath;
            string text = previewText;
            bool bom = previewHasBom;
            bool saved = TryFileOp($"save '{Path.GetFileName(path)}'", () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text, new UTF8Encoding(bom));
            });

            if (!saved)
            {
                return false;
            }

            previewLastWriteTicks = File.GetLastWriteTimeUtc(path).Ticks;
            SetPreviewDirty(false);
            RebuildEntries();
            UpdateSelectionInfo();
            UpdateRootSummary();
            return true;
        }

        private bool ConfirmLeavePreview()
        {
            if (!previewDirty)
            {
                return true;
            }

            int choice = EditorUtility.DisplayDialogComplex("Unsaved Changes",
                $"'{Path.GetFileName(previewPath)}' has unsaved edits.", "Save", "Cancel", "Discard");

            switch (choice)
            {
                case 0:
                    return SavePreview();
                case 2:
                    SetPreviewDirty(false);
                    return true;
                default:
                    return false;
            }
        }

        private void SetPreviewDirty(bool dirty)
        {
            previewDirty = dirty;
            hasUnsavedChanges = dirty;
        }

        private void ReplacePreviewText(string text)
        {
            previewText = text;
            UpdateDisplayText();
            SetPreviewDirty(true);

            // Drop focus so a focused text area re-reads the new value instead of its edit buffer.
            GUIUtility.keyboardControl = 0;
        }

        private void UpdateDisplayText()
        {
            previewDisplayText = previewText != null && previewText.Length > MaxEditableChars
                ? previewText.Substring(0, MaxEditableChars)
                : previewText;
        }

        private void UpdateSelectionInfo()
        {
            selectionInfo = "";
            selectedExists = false;
            selectedIsDirectory = false;
            if (string.IsNullOrEmpty(selectedPath))
            {
                return;
            }

            try
            {
                if (Directory.Exists(selectedPath))
                {
                    selectedExists = true;
                    selectedIsDirectory = true;
                    selectionInfo = $"Folder · {CountChildren(selectedPath)} item(s) · {FormatBytes(DirectorySize(selectedPath))} · " +
                                    $"modified {Directory.GetLastWriteTime(selectedPath):yyyy-MM-dd HH:mm:ss}";
                }
                else if (File.Exists(selectedPath))
                {
                    selectedExists = true;
                    var info = new FileInfo(selectedPath);
                    selectionInfo = $"{FormatBytes(info.Length)} · modified {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
                }
                else
                {
                    selectionInfo = "No longer exists on disk — Save will recreate it.";
                }
            }
            catch (Exception e) when (IsIoException(e))
            {
                selectionInfo = e.Message;
            }
        }

        private static bool LooksLikeJson(string path, string text)
        {
            if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (char c in text)
            {
                if (!char.IsWhiteSpace(c))
                {
                    return c == '{' || c == '[';
                }
            }

            return false;
        }

        private static bool LooksBinary(byte[] bytes)
        {
            int length = Math.Min(bytes.Length, 8000);
            for (int i = 0; i < length; i++)
            {
                if (bytes[i] == 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Re-indents JSON (4 spaces, like JsonUtility's prettyPrint); string contents are left untouched.</summary>
        private static string FormatJson(string json)
        {
            var sb = new StringBuilder(json.Length * 2);
            int indent = 0;
            bool inString = false;
            bool escaped = false;
            bool afterOpen = false;

            foreach (char c in json)
            {
                if (inString)
                {
                    sb.Append(c);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                if (afterOpen)
                {
                    afterOpen = false;
                    if (c == '}' || c == ']')
                    {
                        indent = Math.Max(0, indent - 1);
                        sb.Append(c);
                        continue;
                    }

                    AppendNewLine(sb, indent);
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        sb.Append(c);
                        break;
                    case '{':
                    case '[':
                        sb.Append(c);
                        indent++;
                        afterOpen = true;
                        break;
                    case '}':
                    case ']':
                        indent = Math.Max(0, indent - 1);
                        AppendNewLine(sb, indent);
                        sb.Append(c);
                        break;
                    case ',':
                        sb.Append(c);
                        AppendNewLine(sb, indent);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }

        private static void AppendNewLine(StringBuilder sb, int indent)
        {
            sb.Append('\n');
            sb.Append(' ', indent * 4);
        }
    }
}
