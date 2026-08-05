using System.Collections.Generic;
using System.Text;

namespace Percas.UnityTools.Fbx
{
    public enum FbxChangeKind
    {
        Renamed,
        TransformChanged,
        PivotChanged,
        Reparented,
        Deleted
    }

    public readonly struct FbxChangeEntry
    {
        public readonly FbxChangeKind Kind;
        public readonly string NodePath;
        public readonly string OldValue;
        public readonly string NewValue;

        public FbxChangeEntry(FbxChangeKind kind, string nodePath, string oldValue, string newValue)
        {
            Kind = kind;
            NodePath = nodePath;
            OldValue = oldValue;
            NewValue = newValue;
        }

        public override string ToString()
        {
            return $"[{Kind}] {NodePath}: {OldValue} -> {NewValue}";
        }
    }

    /// <summary>
    /// Records every mutation applied to an open FbxDocument. Backs both the
    /// pre-save dry-run diff shown to the artist and the undo/redo stack —
    /// FbxNode/FbxScene are not UnityEngine.Object, so Unity's own Undo system
    /// cannot record them.
    /// </summary>
    public class FbxChangeLog
    {
        private readonly List<FbxChangeEntry> _entries = new List<FbxChangeEntry>();

        public IReadOnlyList<FbxChangeEntry> Entries => _entries;
        public bool IsEmpty => _entries.Count == 0;

        public void Record(FbxChangeKind kind, string nodePath, string oldValue, string newValue)
        {
            _entries.Add(new FbxChangeEntry(kind, nodePath, oldValue, newValue));
        }

        public void Clear()
        {
            _entries.Clear();
        }

        public string BuildSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{_entries.Count} change(s) will be written to the .fbx file:");
            foreach (var entry in _entries)
            {
                sb.AppendLine(entry.ToString());
            }
            return sb.ToString();
        }
    }
}
