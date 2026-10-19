using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.PersistentDataExplorer
{
    public partial class PersistentDataExplorerWindow
    {
        private void Select(string path)
        {
            if (path == selectedPath)
            {
                return;
            }

            if (path != previewPath && !ConfirmLeavePreview())
            {
                return;
            }

            selectedPath = path;
            renameBuffer = path != null ? Path.GetFileName(path) : "";
            GUIUtility.keyboardControl = 0;

            if (path != previewPath)
            {
                LoadPreview(path);
            }

            UpdateSelectionInfo();
        }

        private void NavigateTo(string dir)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            dir = NormalizePath(dir);
            if (!Directory.Exists(dir) || !IsSameOrInside(dir, rootPath))
            {
                return;
            }

            currentDir = dir;
            ClearSearch();
            listScroll = Vector2.zero;
            RebuildEntries();
        }

        private void OpenEntry(string path, bool isDirectory)
        {
            if (isDirectory)
            {
                NavigateTo(path);
            }
            else
            {
                EditorUtility.OpenWithDefaultApp(path);
            }
        }

        private void CreateFolder()
        {
            string path = UniquePath($"{currentDir}/New Folder");
            if (TryFileOp("create a folder", () => Directory.CreateDirectory(path)))
            {
                AfterCreate(path);
            }
        }

        private void CreateFile()
        {
            string path = UniquePath($"{currentDir}/New File.txt");
            if (TryFileOp("create a file", () => File.WriteAllText(path, "")))
            {
                AfterCreate(path);
            }
        }

        private void AfterCreate(string path)
        {
            ClearSearch();
            RebuildEntries();
            UpdateRootSummary();
            Select(path);
            if (selectedPath == path)
            {
                focusRename = true;
            }
        }

        private void Rename(string path, string newName)
        {
            newName = newName?.Trim();
            if (!IsValidFileName(newName))
            {
                EditorUtility.DisplayDialog("Rename", $"'{newName}' is not a valid name.", "OK");
                return;
            }

            string target = NormalizePath($"{Path.GetDirectoryName(path)}/{newName}");
            if (target == path)
            {
                return;
            }

            // Windows/macOS file systems are case-insensitive: "a.json" -> "A.json" targets the same
            // entry, so it's not a clash — but it needs a two-step move to actually change case.
            bool caseOnlyChange = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
            if (PathExists(target) && !caseOnlyChange)
            {
                EditorUtility.DisplayDialog("Rename", $"'{newName}' already exists in this folder.", "OK");
                return;
            }

            bool isDir = Directory.Exists(path);
            bool renamed = TryFileOp($"rename '{Path.GetFileName(path)}'", () =>
            {
                if (caseOnlyChange)
                {
                    string temp = path + ".renaming~";
                    Move(path, temp, isDir);
                    Move(temp, target, isDir);
                }
                else
                {
                    Move(path, target, isDir);
                }
            });

            if (!renamed)
            {
                return;
            }

            selectedPath = Remap(selectedPath, path, target);
            previewPath = Remap(previewPath, path, target);
            currentDir = Remap(currentDir, path, target);
            renameBuffer = selectedPath != null ? Path.GetFileName(selectedPath) : "";
            Refresh();
        }

        private void Duplicate(string path)
        {
            string target = UniquePath(path);
            bool isDir = Directory.Exists(path);
            bool copied = TryFileOp($"duplicate '{Path.GetFileName(path)}'", () =>
            {
                if (isDir)
                {
                    CopyDirectory(path, target);
                }
                else
                {
                    File.Copy(path, target);
                }
            });

            if (!copied)
            {
                return;
            }

            RebuildEntries();
            UpdateRootSummary();
            Select(target);
        }

        private void Delete(string path)
        {
            bool isDir = Directory.Exists(path);
            string name = Path.GetFileName(path);
            string message = isDir
                ? $"Delete folder '{name}' and everything inside it?\n\nThis cannot be undone."
                : $"Delete '{name}'?\n\nThis cannot be undone.";

            if (!EditorUtility.DisplayDialog("Delete", message, "Delete", "Cancel"))
            {
                return;
            }

            TryFileOp($"delete '{name}'", () => DeleteFileOrDirectory(path));
            DropPreviewIfDeleted();
            Refresh();
        }

        private void ClearAll()
        {
            if (!EditorUtility.DisplayDialog("Clear Persistent Data",
                    $"Delete EVERYTHING inside:\n{ToNativePath(rootPath)}\n\nThis cannot be undone.",
                    "Delete All", "Cancel"))
            {
                return;
            }

            string[] items = Array.Empty<string>();
            if (!TryFileOp("list persistentDataPath", () => items = Directory.GetFileSystemEntries(rootPath)))
            {
                return;
            }

            var failures = new List<string>();
            foreach (string item in items)
            {
                try
                {
                    DeleteFileOrDirectory(item);
                }
                catch (Exception e) when (IsIoException(e))
                {
                    failures.Add($"{Path.GetFileName(item)}: {e.Message}");
                }
            }

            DropPreviewIfDeleted();
            currentDir = rootPath;
            Refresh();

            if (failures.Count > 0)
            {
                EditorUtility.DisplayDialog("Clear Persistent Data",
                    "Some items could not be deleted (in use?):\n\n" + string.Join("\n", failures), "OK");
            }
        }

        private void DropPreviewIfDeleted()
        {
            // The user confirmed the delete, so unsaved edits to a file that's now gone are discarded.
            if (previewPath != null && !File.Exists(previewPath))
            {
                LoadPreview(null);
            }
        }

        private void ImportFromDialog()
        {
            string file = EditorUtility.OpenFilePanel("Import File into persistentDataPath", "", "");
            if (!string.IsNullOrEmpty(file))
            {
                ImportPaths(new[] { file });
            }
        }

        private void ImportPaths(IEnumerable<string> sources)
        {
            string destDir = currentDir;
            string lastImported = null;

            foreach (string raw in sources)
            {
                string source = NormalizePath(raw);
                bool isDir = Directory.Exists(source);
                if (!isDir && !File.Exists(source))
                {
                    continue;
                }

                string name = Path.GetFileName(source);
                string target = $"{destDir}/{name}";
                if (string.Equals(target, source, PathComparison))
                {
                    continue;
                }

                if (isDir && IsSameOrInside(destDir, source))
                {
                    EditorUtility.DisplayDialog("Import", $"Can't import folder '{name}' into itself.", "OK");
                    continue;
                }

                if (PathExists(target))
                {
                    int choice = EditorUtility.DisplayDialogComplex("Import",
                        $"'{name}' already exists in this folder.", "Replace", "Skip", "Keep Both");

                    if (choice == 1)
                    {
                        continue;
                    }

                    if (choice == 2)
                    {
                        target = UniquePath(target);
                    }
                    else
                    {
                        string existing = target;
                        if (!TryFileOp($"replace '{name}'", () => DeleteFileOrDirectory(existing)))
                        {
                            continue;
                        }
                    }
                }

                string finalTarget = target;
                bool imported = TryFileOp($"import '{name}'", () =>
                {
                    if (isDir)
                    {
                        CopyDirectory(source, finalTarget);
                    }
                    else
                    {
                        File.Copy(source, finalTarget);
                    }
                });

                if (imported)
                {
                    lastImported = finalTarget;
                }
            }

            if (lastImported == null)
            {
                return;
            }

            Refresh();
            Select(lastImported);
        }

        private void Export(string path)
        {
            string name = Path.GetFileName(path);

            if (Directory.Exists(path))
            {
                string destParent = EditorUtility.SaveFolderPanel($"Export '{name}' To", "", "");
                if (string.IsNullOrEmpty(destParent))
                {
                    return;
                }

                string target = NormalizePath($"{destParent}/{name}");
                if (IsSameOrInside(target, path))
                {
                    EditorUtility.DisplayDialog("Export", "Can't export a folder into itself.", "OK");
                    return;
                }

                if (Directory.Exists(target)
                    && !EditorUtility.DisplayDialog("Export",
                        $"'{ToNativePath(target)}' already exists.\n\nMerge into it, overwriting files with the same name?",
                        "Merge", "Cancel"))
                {
                    return;
                }

                if (TryFileOp($"export '{name}'", () => CopyDirectory(path, target)))
                {
                    EditorUtility.RevealInFinder(target);
                }
            }
            else
            {
                string extension = Path.GetExtension(path).TrimStart('.');
                string dest = EditorUtility.SaveFilePanel($"Export '{name}'", "", Path.GetFileNameWithoutExtension(path), extension);
                if (string.IsNullOrEmpty(dest))
                {
                    return;
                }

                if (TryFileOp($"export '{name}'", () => File.Copy(path, dest, true)))
                {
                    EditorUtility.RevealInFinder(dest);
                }
            }
        }

        private void ClearSearch()
        {
            if (!IsSearching && string.IsNullOrEmpty(searchInput))
            {
                return;
            }

            search = "";
            searchInput = "";
            GUIUtility.keyboardControl = 0;
        }

        private static void OpenFolderInOS(string dir)
        {
            string native = ToNativePath(dir);
            try
            {
                switch (Application.platform)
                {
                    case RuntimePlatform.WindowsEditor:
                        System.Diagnostics.Process.Start("explorer.exe", $"\"{native}\"")?.Dispose();
                        break;
                    case RuntimePlatform.OSXEditor:
                        System.Diagnostics.Process.Start("open", $"\"{native}\"")?.Dispose();
                        break;
                    default:
                        System.Diagnostics.Process.Start("xdg-open", $"\"{native}\"")?.Dispose();
                        break;
                }
            }
            catch (Exception)
            {
                EditorUtility.RevealInFinder(native);
            }
        }

        private static void Move(string from, string to, bool isDirectory)
        {
            if (isDirectory)
            {
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to);
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);

            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }

            foreach (string dir in Directory.GetDirectories(source))
            {
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
        }

        private static void DeleteFileOrDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            else if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }

        private static bool TryFileOp(string description, Action op)
        {
            try
            {
                op();
                return true;
            }
            catch (Exception e) when (IsIoException(e))
            {
                EditorUtility.DisplayDialog("Persistent Data Explorer", $"Could not {description}:\n\n{e.Message}", "OK");
                return false;
            }
        }

        private static bool IsIoException(Exception e)
        {
            return e is IOException
                   || e is UnauthorizedAccessException
                   || e is ArgumentException
                   || e is NotSupportedException
                   || e is System.Security.SecurityException;
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
        }

        private static string ToNativePath(string path)
        {
            if (path == null)
            {
                return "";
            }

            return Application.platform == RuntimePlatform.WindowsEditor ? path.Replace('/', '\\') : path;
        }

        private static StringComparison PathComparison =>
            Application.platform == RuntimePlatform.LinuxEditor ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        private static bool IsSameOrInside(string path, string folder)
        {
            return string.Equals(path, folder, PathComparison) || path.StartsWith(folder + "/", PathComparison);
        }

        private string RelativePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= rootPath.Length || !IsSameOrInside(path, rootPath))
            {
                return "";
            }

            return path.Substring(rootPath.Length + 1);
        }

        private static string Remap(string path, string from, string to)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            if (string.Equals(path, from, PathComparison))
            {
                return to;
            }

            return path.StartsWith(from + "/", PathComparison) ? to + path.Substring(from.Length) : path;
        }

        private static bool PathExists(string path)
        {
            return File.Exists(path) || Directory.Exists(path);
        }

        private static string UniquePath(string desired)
        {
            desired = NormalizePath(desired);
            if (!PathExists(desired))
            {
                return desired;
            }

            string dir = Path.GetDirectoryName(desired);
            string name = Path.GetFileNameWithoutExtension(desired);
            string extension = Path.GetExtension(desired);
            for (int i = 1; ; i++)
            {
                string candidate = NormalizePath(Path.Combine(dir, $"{name} ({i}){extension}"));
                if (!PathExists(candidate))
                {
                    return candidate;
                }
            }
        }

        private static bool IsValidFileName(string name)
        {
            return !string.IsNullOrWhiteSpace(name)
                   && name != "."
                   && name != ".."
                   && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        private static int CountChildren(string dir)
        {
            try
            {
                int count = 0;
                foreach (string _ in Directory.EnumerateFileSystemEntries(dir))
                {
                    count++;
                }

                return count;
            }
            catch (Exception e) when (IsIoException(e))
            {
                return 0;
            }
        }

        private static long DirectorySize(string dir)
        {
            try
            {
                long total = 0;
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    total += new FileInfo(file).Length;
                }

                return total;
            }
            catch (Exception e) when (IsIoException(e))
            {
                return 0;
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return $"{bytes} B";
            }

            if (bytes < 1024 * 1024)
            {
                return $"{bytes / 1024f:0.#} KB";
            }

            if (bytes < 1024L * 1024 * 1024)
            {
                return $"{bytes / (1024f * 1024f):0.#} MB";
            }

            return $"{bytes / (1024f * 1024f * 1024f):0.##} GB";
        }

        private GUIContent IconForPath(string path, bool isDirectory)
        {
            if (isDirectory)
            {
                return folderIcon;
            }

            return TextExtensions.Contains(Path.GetExtension(path) ?? "") ? textIcon : fileIcon;
        }
    }
}
