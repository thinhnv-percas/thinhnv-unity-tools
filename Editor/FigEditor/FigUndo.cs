using System;
using System.Collections.Generic;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// Undo / redo for edits to a <see cref="FigFile"/>. An entry snapshots what an edit is about to
    /// change — individual fields of node changes (<see cref="Capture"/>) or the whole
    /// <c>nodeChanges</c> list for structural edits (<see cref="CaptureStructure"/>) — and undoing
    /// swaps the snapshot with the current state, so the same entry then redoes.
    ///
    /// Consecutive entries with the same merge key within <see cref="MergeSeconds"/> collapse into
    /// one, so dragging a slider or a layer is one step.
    /// </summary>
    public sealed class FigUndo
    {
        public const double MergeSeconds = 1.0;
        private const int MaxEntries = 200;

        internal sealed class FieldSnapshot
        {
            public KiwiValue Owner;
            public string Field;
            public KiwiValue Value; // null: the field was absent
        }

        public sealed class Entry
        {
            public string Label;
            public string MergeKey;
            public double Time;
            internal readonly List<FieldSnapshot> Fields = new();
            internal List<KiwiValue> Structure; // nodeChanges contents before the edit
            public bool IsStructural => Structure != null;
        }

        private readonly List<Entry> entries = new();
        private Entry open;

        /// <summary>Entries [0, Position) can be undone; [Position, Count) redone.</summary>
        public int Position { get; private set; }

        public int Count => entries.Count;

        /// <summary>Entries dropped from the front; <c>Offset + Position</c> numbers states stably.</summary>
        public int Offset { get; private set; }

        public int Revision => Offset + Position;

        public bool CanUndo => Position > 0;

        public bool CanRedo => Position < entries.Count;

        public string UndoLabel => CanUndo ? entries[Position - 1].Label : null;

        public string RedoLabel => CanRedo ? entries[Position].Label : null;

        public void Clear()
        {
            entries.Clear();
            open = null;
            Position = 0;
            Offset = 0;
        }

        /// <summary>
        /// Start an entry. Returns false (and records nothing new) when it merges into the previous
        /// one: same <paramref name="mergeKey"/>, within <see cref="MergeSeconds"/>, nothing undone since.
        /// </summary>
        public bool Begin(string label, string mergeKey, double now)
        {
            if (mergeKey != null && Position == entries.Count && Position > 0)
            {
                Entry last = entries[Position - 1];
                if (last.MergeKey == mergeKey && now - last.Time <= MergeSeconds)
                {
                    last.Time = now;
                    open = null;
                    return false;
                }
            }

            open = new Entry { Label = label, MergeKey = mergeKey, Time = now };
            return true;
        }

        /// <summary>Snapshot field <paramref name="field"/> of <paramref name="owner"/> (once per entry).</summary>
        public void Capture(KiwiValue owner, string field)
        {
            if (open == null || owner == null)
            {
                return;
            }

            foreach (FieldSnapshot f in open.Fields)
            {
                if (f.Owner == owner && f.Field == field)
                {
                    return;
                }
            }

            open.Fields.Add(new FieldSnapshot { Owner = owner, Field = field, Value = owner.Get(field)?.Clone() });
        }

        /// <summary>Snapshot the node change list (cheap: the list of references, not the nodes).</summary>
        public void CaptureStructure(FigFile file)
        {
            if (open != null && open.Structure == null)
            {
                open.Structure = new List<KiwiValue>(file.NodeChanges);
            }
        }

        /// <summary>Finish the entry started by <see cref="Begin"/>; drops the redo history.</summary>
        public Entry Commit()
        {
            Entry entry = open;
            open = null;
            if (entry == null || (entry.Fields.Count == 0 && entry.Structure == null))
            {
                return null;
            }

            entries.RemoveRange(Position, entries.Count - Position);
            entries.Add(entry);
            if (entries.Count > MaxEntries)
            {
                entries.RemoveAt(0);
                Offset++;
            }

            Position = entries.Count;
            return entry;
        }

        public void Cancel()
        {
            open = null;
        }

        /// <summary>Undo one step; returns the entry undone, or null.</summary>
        public Entry Undo(FigFile file)
        {
            if (!CanUndo)
            {
                return null;
            }

            Entry entry = entries[--Position];
            Swap(entry, file);
            return entry;
        }

        public Entry Redo(FigFile file)
        {
            if (!CanRedo)
            {
                return null;
            }

            Entry entry = entries[Position++];
            Swap(entry, file);
            return entry;
        }

        private static void Swap(Entry entry, FigFile file)
        {
            // Fields in reverse, so a field captured twice by different owners restores cleanly.
            for (int i = entry.Fields.Count - 1; i >= 0; i--)
            {
                FieldSnapshot f = entry.Fields[i];
                KiwiValue current = f.Owner.Get(f.Field);
                if (f.Value == null)
                {
                    f.Owner.Remove(f.Field);
                }
                else
                {
                    f.Owner.Set(f.Field, f.Value);
                }

                f.Value = current;
            }

            if (entry.Structure != null)
            {
                List<KiwiValue> changes = file.NodeChanges;
                var current = new List<KiwiValue>(changes);
                changes.Clear();
                changes.AddRange(entry.Structure);
                entry.Structure = current;
            }
        }
    }
}
