using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>Preferences › Thinhnv › Fig Editor: the Figma access token and the import folder.</summary>
    public static class FigmaSettings
    {
        public const string Path = "Preferences/Thinhnv/Fig Editor";

        private static string typed;
        private static bool reveal;
        private static Task<string> test;
        private static string testResult;
        private static bool testFailed;

        public static void Open()
        {
            SettingsService.OpenUserPreferences(Path);
        }

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(Path, SettingsScope.User)
            {
                label = "Fig Editor",
                guiHandler = _ => Draw(),
                keywords = new HashSet<string> { "Figma", "token", "fig", "import" },
            };
        }

        private static void Draw()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Figma access token", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Importing and updating from a Figma link uses Figma's REST API with a personal access token.\n" +
                "In Figma: Settings › Security › Personal access tokens › Generate new token, with the scope " +
                "\"File content: read-only\". The token is kept in this computer's Unity preferences, not in the project.",
                MessageType.None);

            if (typed == null)
            {
                typed = FigmaApi.TokenFromEnvironment ? "" : FigmaApi.Token;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                typed = reveal ? EditorGUILayout.TextField("Token", typed) : EditorGUILayout.PasswordField("Token", typed);
                reveal = GUILayout.Toggle(reveal, "Show", EditorStyles.miniButton, GUILayout.Width(44));
            }

            if (FigmaApi.TokenFromEnvironment)
            {
                EditorGUILayout.LabelField(" ", "Using FIGMA_TOKEN from the environment (" + FigmaApi.MaskedToken + ").", EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUIUtility.labelWidth);
                bool changed = typed.Trim() != (FigmaApi.TokenFromEnvironment ? "" : FigmaApi.Token);
                using (new EditorGUI.DisabledScope(!changed))
                {
                    if (GUILayout.Button("Save", GUILayout.Width(70)))
                    {
                        FigmaApi.Token = typed;
                        testResult = null;
                        GUI.FocusControl(null);
                    }
                }

                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(FigmaApi.Token) || test != null))
                {
                    if (GUILayout.Button(test != null ? "Testing…" : "Test", GUILayout.Width(70)))
                    {
                        StartTest();
                    }
                }

                if (GUILayout.Button("Clear", GUILayout.Width(70)))
                {
                    FigmaApi.Token = "";
                    typed = "";
                    testResult = null;
                    GUI.FocusControl(null);
                }

                GUILayout.FlexibleSpace();
                if (GUILayout.Button("How to get a token", EditorStyles.linkLabel))
                {
                    Application.OpenURL(FigmaApi.TokenHelpUrl);
                }
            }

            if (testResult != null)
            {
                EditorGUILayout.HelpBox(testResult, testFailed ? MessageType.Error : MessageType.Info);
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Import", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                string folder = EditorGUILayout.DelayedTextField(new GUIContent("Folder", "Where imported .fig files are written (project-relative or absolute)."), FigmaApi.ImportFolder);
                if (folder != FigmaApi.ImportFolder)
                {
                    FigmaApi.ImportFolder = folder;
                }

                if (GUILayout.Button("…", GUILayout.Width(24)))
                {
                    string picked = EditorUtility.OpenFolderPanel("Import folder", FigmaApi.ImportFolder, "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        FigmaApi.ImportFolder = FigImportWindow.ProjectRelative(picked);
                    }
                }
            }
        }

        private static void StartTest()
        {
            testResult = null;
            test = FigmaApi.Me(FigmaApi.Token, CancellationToken.None);
            EditorApplication.update += PollTest;
        }

        private static void PollTest()
        {
            if (test == null || !test.IsCompleted)
            {
                return;
            }

            EditorApplication.update -= PollTest;
            if (test.Status == TaskStatus.RanToCompletion)
            {
                testFailed = false;
                testResult = "The token works: signed in as " + test.Result + ".";
            }
            else
            {
                testFailed = true;
                Exception e = test.Exception?.GetBaseException();
                testResult = e is FigmaException ? e.Message : "The test failed: " + e?.Message;
            }

            test = null;
            SettingsService.RepaintAllSettingsWindow();
        }
    }
}
