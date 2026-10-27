using UnityEditor;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// Undo / redo of the Fig Editor's edits: each edit snapshots what it changes into
    /// <see cref="FigUndo"/> and records the hidden <see cref="FigUndoProxy"/> with Unity's Undo, so
    /// Ctrl/Cmd+Z, Ctrl+Y and Edit &gt; Undo step through this history too.
    /// </summary>
    public partial class FigEditorWindow
    {
        private FigUndo undo = new();
        private FigUndoProxy undoProxy;

        // ─── Undo ────────────────────────────────────────────────────────────────

        /// <summary>Snapshot <paramref name="fields"/> of <paramref name="owner"/> as one undo step (before changing them).</summary>
        private void RecordFields(string label, string mergeKey, KiwiValue owner, params string[] fields)
        {
            if (file == null || owner == null)
            {
                return;
            }

            bool fresh = undo.Begin(label, mergeKey, EditorApplication.timeSinceStartup);
            if (!fresh)
            {
                return;
            }

            foreach (string f in fields)
            {
                undo.Capture(owner, f);
            }

            CommitUndo(label);
        }

        /// <summary>Snapshot the whole layer list (before a delete / duplicate) as one undo step.</summary>
        private void RecordStructure(string label)
        {
            if (file == null)
            {
                return;
            }

            undo.Begin(label, null, EditorApplication.timeSinceStartup);
            undo.CaptureStructure(file);
            CommitUndo(label);
        }

        /// <summary>Begin a multi-owner step; add fields with <see cref="FigUndo.Capture"/>, then <see cref="CommitUndo"/>.</summary>
        private bool BeginUndo(string label, string mergeKey)
        {
            return file != null && undo.Begin(label, mergeKey, EditorApplication.timeSinceStartup);
        }

        private void CommitUndo(string label)
        {
            if (undo.Commit() == null || undoProxy == null)
            {
                return;
            }

            Undo.RecordObject(undoProxy, "Fig Editor: " + label);
            undoProxy.Revision = undo.Revision;
        }

        private void OnUnityUndoRedo()
        {
            if (file == null || undoProxy == null || undoProxy.Revision == undo.Revision)
            {
                return;
            }

            StepUndoTo(undoProxy.Revision);
        }

        private void StepUndoTo(int revision)
        {
            bool changed = false;
            while (undo.Revision > revision && undo.CanUndo)
            {
                undo.Undo(file);
                changed = true;
            }

            while (undo.Revision < revision && undo.CanRedo)
            {
                undo.Redo(file);
                changed = true;
            }

            if (changed)
            {
                AfterUndoRedo();
            }
        }

        private void UndoStep(bool redo)
        {
            FigUndo.Entry entry = redo ? undo.Redo(file) : undo.Undo(file);
            if (entry == null)
            {
                return;
            }

            // Keep Unity's copy of the revision in step without recording a Unity undo step.
            if (undoProxy != null)
            {
                undoProxy.Revision = undo.Revision;
            }

            AfterUndoRedo();
        }

        private void AfterUndoRedo()
        {
            string keep = selectedId;
            file.RebuildIndex();
            OnStructureChanged();
            boardSelection.RemoveWhere(id => file.Find(id) == null);
            if (file.Find(keep) == null)
            {
                selectedId = null;
            }

            MarkDirty();
            Repaint();
        }
    }
}
