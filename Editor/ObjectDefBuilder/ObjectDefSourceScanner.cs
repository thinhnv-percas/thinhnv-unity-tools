using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Fills an entry's size rows from a folder that follows the art naming convention:
    /// "<c>Ice 1x2.fbx</c>" (model), "<c>ice_1x2_Break.fbx</c>" (fractured model) and
    /// "<c>Ice 1x2.png</c>" (texture, assigned to both the model and the piece slot). A texture shared by
    /// several sizes can list them all - "<c>Ice 1x5 6 7 8.png</c>" fills rows 5 through 8.
    ///
    /// A scan **overwrites** whatever the slots held, so re-scanning after renaming or replacing art
    /// always lands the current files. Paths are walked in sorted order so a folder with two files
    /// matching the same size resolves the same way every time (the last one wins).
    /// </summary>
    public static class ObjectDefSourceScanner
    {
        private static readonly Regex SizePattern =
            new Regex(@"1\s*x\s*(\d+)((?:[\s_]+\d+)*)", RegexOptions.IgnoreCase);

        /// <summary>Scan <see cref="ObjectDefBuildEntry.sourceFolder"/> and report what was assigned.</summary>
        public static string AutoFill(ObjectDefBuildEntry entry)
        {
            if (!AssetDatabase.IsValidFolder(entry.sourceFolder))
            {
                return $"Source folder not found: {entry.sourceFolder}";
            }

            var rows = new Dictionary<int, ObjectDefBuildRow>();
            foreach (ObjectDefBuildRow row in entry.rows)
            {
                rows[row.magnitude] = row;
            }

            var search = new[] { entry.sourceFolder };
            int filled = 0;
            filled += FillModels(SortedPaths(AssetDatabase.FindAssets("t:GameObject", search)), rows);
            filled += FillTextures(SortedPaths(AssetDatabase.FindAssets("t:Texture2D", search)), rows);

            return filled == 0
                ? $"Auto-Fill matched no '1x<n>' file names under {entry.sourceFolder}."
                : $"Auto-Fill assigned {filled} slot(s) from {entry.sourceFolder} (existing values replaced).";
        }

        /// <summary>Asset paths for a GUID list, sorted so repeated scans assign the same files.</summary>
        private static List<string> SortedPaths(string[] guids)
        {
            var paths = new List<string>(guids.Length);
            foreach (string guid in guids)
            {
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            }

            paths.Sort(System.StringComparer.Ordinal);
            return paths;
        }

        private static int FillModels(List<string> paths, Dictionary<int, ObjectDefBuildRow> rows)
        {
            int filled = 0;

            foreach (string path in paths)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                bool isBreak = name.IndexOf("break", System.StringComparison.OrdinalIgnoreCase) >= 0;

                foreach (int magnitude in Magnitudes(name))
                {
                    if (!rows.TryGetValue(magnitude, out ObjectDefBuildRow row))
                    {
                        continue;
                    }

                    var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (isBreak)
                    {
                        row.breakSource = model;
                    }
                    else
                    {
                        row.modelSource = model;
                    }

                    filled++;
                }
            }

            return filled;
        }

        /// <summary>
        /// Assign each matched texture to both the model and the piece slot - the art ships one texture
        /// per size and the pieces reuse it, so leaving the piece slot empty just hid that.
        /// </summary>
        private static int FillTextures(List<string> paths, Dictionary<int, ObjectDefBuildRow> rows)
        {
            int filled = 0;

            foreach (string path in paths)
            {
                string name = Path.GetFileNameWithoutExtension(path);

                foreach (int magnitude in Magnitudes(name))
                {
                    if (!rows.TryGetValue(magnitude, out ObjectDefBuildRow row))
                    {
                        continue;
                    }

                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    row.modelTexture = texture;
                    row.pieceTexture = texture;
                    filled += 2;
                }
            }

            return filled;
        }

        /// <summary>Every magnitude named in a file name: "Ice 1x5 6 7 8" yields 5, 6, 7, 8.</summary>
        private static IEnumerable<int> Magnitudes(string fileName)
        {
            Match match = SizePattern.Match(fileName);
            if (!match.Success)
            {
                yield break;
            }

            yield return int.Parse(match.Groups[1].Value);

            string trailing = match.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(trailing))
            {
                yield break;
            }

            foreach (string token in trailing.Split(' ', '_'))
            {
                if (int.TryParse(token, out int extra))
                {
                    yield return extra;
                }
            }
        }
    }
}
