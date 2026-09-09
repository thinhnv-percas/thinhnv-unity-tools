using System;
using System.Collections.Generic;
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
    /// UnityEditor.LogEntries/LogEntry via reflection — undocumented, internal-only classes whose
    /// exact shape (field/method names, even which assembly they live in) has shifted between
    /// Unity versions. Every lookup here is resolved by name at runtime and null/try-checked, so a
    /// mismatch on some future Editor version degrades to a clear error message (with the actual
    /// exception, to help pin down what changed) instead of an unhandled exception.
    /// </summary>
    public static class CopyConsoleLogs
    {
        // Bit flags from Unity's internal (undocumented) LogMessageFlags enum. These have been
        // stable from Unity 2018 through Unity 6, but aren't a public contract.
        private const int ModeFatal = 1 << 4;
        private const int ModeScriptingError = 1 << 9;
        private const int ModeScriptingWarning = 1 << 10;
        private const int ModeScriptingLog = 1 << 11;
        private const int ModeScriptCompileError = 1 << 12;
        private const int ModeScriptCompileWarning = 1 << 13;

        [MenuItem("Tools/Thinhnv/Copy Console Logs %#l")]
        private static void CopyToClipboard()
        {
            try
            {
                CopyToClipboardInternal();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Copy Console Logs",
                    "Could not read the Console through Unity's internal API on this Editor version.\n\n" +
                    $"{e.GetType().Name}: {e.Message}\n\n" +
                    "The full exception was logged to the Console — please share it so the reflection " +
                    "lookup can be updated for this Unity version.",
                    "OK");
            }
        }

        private static void CopyToClipboardInternal()
        {
            // Resolve LogEntries/LogEntry through the assembly a known public Editor type lives
            // in, rather than guessing the assembly's display name (e.g. "UnityEditor" vs
            // "UnityEditor.CoreModule") with Type.GetType("Name,Assembly") — that name has
            // differed across Unity versions and is a common cause of the lookup silently
            // returning null.
            Assembly editorAssembly = typeof(EditorApplication).Assembly;
            Type logEntriesType = editorAssembly.GetType("UnityEditor.LogEntries");
            Type logEntryType = editorAssembly.GetType("UnityEditor.LogEntry");
            if (logEntriesType == null || logEntryType == null)
            {
                EditorUtility.DisplayDialog("Copy Console Logs",
                    "Could not find Unity's internal Console API (UnityEditor.LogEntries) on this Editor version.",
                    "OK");
                return;
            }

            // Names below try the current (Unity 2018 through Unity 6) spelling first, with an
            // older/alternate spelling as fallback in case a given Editor version differs.
            MethodInfo getCount = FindMethod(logEntriesType, "GetCount");
            MethodInfo startGetEntries = FindMethod(logEntriesType, "StartGettingEntries", "StartGetEntries");
            MethodInfo endGetEntries = FindMethod(logEntriesType, "EndGettingEntries", "EndGetEntries");
            MethodInfo getEntryInternal = FindMethod(logEntriesType, "GetEntryInternal");

            var missingMethods = new List<string>();
            if (getCount == null) missingMethods.Add("GetCount");
            if (startGetEntries == null) missingMethods.Add("StartGettingEntries");
            if (endGetEntries == null) missingMethods.Add("EndGettingEntries");
            if (getEntryInternal == null) missingMethods.Add("GetEntryInternal");

            if (missingMethods.Count > 0)
            {
                EditorUtility.DisplayDialog("Copy Console Logs",
                    "Unity's internal Console API is missing expected method(s) on this Editor version: " +
                    string.Join(", ", missingMethods) + ".", "OK");
                return;
            }

            ParameterInfo[] getEntryParams = getEntryInternal.GetParameters();
            if (getEntryParams.Length != 2)
            {
                EditorUtility.DisplayDialog("Copy Console Logs",
                    $"Unity's internal GetEntryInternal has an unexpected signature on this Editor version " +
                    $"({getEntryParams.Length} parameter(s) instead of 2).", "OK");
                return;
            }

            int count = Convert.ToInt32(getCount.Invoke(null, null));
            if (count == 0)
            {
                EditorUtility.DisplayDialog("Copy Console Logs", "The Console is empty.", "OK");
                return;
            }

            FieldInfo messageField = FindField(logEntryType, "message", "condition");
            FieldInfo modeField = FindField(logEntryType, "mode", "flags");
            object entry = Activator.CreateInstance(logEntryType);
            var getEntryArgs = new object[2];

            var sb = new StringBuilder();
            startGetEntries.Invoke(null, null);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    getEntryArgs[0] = i;
                    getEntryArgs[1] = entry;
                    getEntryInternal.Invoke(null, getEntryArgs);

                    string message = messageField?.GetValue(entry) as string ?? "";
                    string prefix = modeField != null ? TypePrefix(Convert.ToInt32(modeField.GetValue(entry))) : "";

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

        private static FieldInfo FindField(Type type, params string[] candidateNames)
        {
            foreach (string name in candidateNames)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    return field;
                }
            }

            return null;
        }

        private static MethodInfo FindMethod(Type type, params string[] candidateNames)
        {
            foreach (string name in candidateNames)
            {
                MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
                if (method != null)
                {
                    return method;
                }
            }

            return null;
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
