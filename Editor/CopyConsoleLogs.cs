using System;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ThinhnvTools
{
    /// <summary>
    /// Copies every entry currently held in the Console window — logs, warnings and errors from
    /// this whole session, not just ones printed since the Editor last reloaded — to the
    /// clipboard, one per line, prefixed with [Log]/[Warning]/[Error] where the type could be read.
    ///
    /// The Console's contents aren't exposed by any public API, so this reads them through
    /// UnityEditor.LogEntries/LogEntry via reflection — undocumented, internal-only classes that
    /// Unity could change in a future version. If a piece of that internal API is missing, this
    /// degrades gracefully (skips the type prefix, or reports that it couldn't reach the Console
    /// at all) instead of throwing.
    /// </summary>
    public static class CopyConsoleLogs
    {
        // Bit flags from Unity's internal (undocumented) LogMessageFlags enum.
        private const int ModeFatal = 1 << 4;
        private const int ModeScriptingError = 1 << 9;
        private const int ModeScriptingWarning = 1 << 10;
        private const int ModeScriptingLog = 1 << 11;
        private const int ModeScriptCompileError = 1 << 12;
        private const int ModeScriptCompileWarning = 1 << 13;

        [MenuItem("Tools/Thinhnv/Copy Console Logs %#l")]
        private static void CopyToClipboard()
        {
            Type logEntriesType = Type.GetType("UnityEditor.LogEntries,UnityEditor");
            Type logEntryType = Type.GetType("UnityEditor.LogEntry,UnityEditor");
            if (logEntriesType == null || logEntryType == null)
            {
                EditorUtility.DisplayDialog("Copy Console Logs",
                    "Could not access Unity's internal Console API (UnityEditor.LogEntries). " +
                    "This Editor version may have changed its internal layout.", "OK");
                return;
            }

            MethodInfo getCount = logEntriesType.GetMethod("GetCount", BindingFlags.Public | BindingFlags.Static);
            MethodInfo startGetEntries = logEntriesType.GetMethod("StartGetEntries", BindingFlags.Public | BindingFlags.Static);
            MethodInfo endGetEntries = logEntriesType.GetMethod("EndGetEntries", BindingFlags.Public | BindingFlags.Static);
            MethodInfo getEntryInternal = logEntriesType.GetMethod("GetEntryInternal", BindingFlags.Public | BindingFlags.Static);
            if (getCount == null || startGetEntries == null || endGetEntries == null || getEntryInternal == null)
            {
                EditorUtility.DisplayDialog("Copy Console Logs",
                    "Unity's internal Console API is missing an expected method on this Editor version.", "OK");
                return;
            }

            int count = (int)getCount.Invoke(null, null);
            if (count == 0)
            {
                EditorUtility.DisplayDialog("Copy Console Logs", "The Console is empty.", "OK");
                return;
            }

            FieldInfo messageField = logEntryType.GetField("message", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo modeField = logEntryType.GetField("mode", BindingFlags.Public | BindingFlags.Instance);
            object entry = Activator.CreateInstance(logEntryType);

            var sb = new StringBuilder();
            startGetEntries.Invoke(null, null);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    getEntryInternal.Invoke(null, new object[] { i, entry });

                    string message = messageField?.GetValue(entry) as string ?? "";
                    string prefix = modeField != null ? TypePrefix((int)modeField.GetValue(entry)) : "";

                    if (i > 0)
                    {
                        sb.Append('\n');
                    }

                    sb.Append(prefix).Append(message);
                }
            }
            finally
            {
                endGetEntries.Invoke(null, null);
            }

            EditorGUIUtility.systemCopyBuffer = sb.ToString();
            Debug.Log($"Copied {count} console entrie(s) to clipboard.");
        }

        private static string TypePrefix(int mode)
        {
            if ((mode & (ModeScriptingError | ModeScriptCompileError | ModeFatal)) != 0)
            {
                return "[Error] ";
            }

            if ((mode & (ModeScriptingWarning | ModeScriptCompileWarning)) != 0)
            {
                return "[Warning] ";
            }

            if ((mode & ModeScriptingLog) != 0)
            {
                return "[Log] ";
            }

            return "";
        }
    }
}
