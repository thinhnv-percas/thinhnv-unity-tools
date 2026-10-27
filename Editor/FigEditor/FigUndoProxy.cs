using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// A hidden object Unity's Undo system records, so Ctrl+Z / Ctrl+Y (and Edit &gt; Undo) step
    /// through the Fig Editor's own history: each edit records this object and bumps
    /// <see cref="Revision"/>; when Unity undoes or redoes, the window walks its
    /// <see cref="FigUndo"/> to the restored revision.
    /// </summary>
    public sealed class FigUndoProxy : ScriptableObject
    {
        public int Revision;
    }
}
