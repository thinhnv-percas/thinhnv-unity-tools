using System;

namespace Thinhnv.UnityTools.AlchemyLite
{
    /// <summary>
    /// Shows a <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/> field in the Inspector
    /// as a live debug/tuning view: read-only outside Play mode, editable (add/remove/edit entries)
    /// while Play mode is running. The field is never serialized or persisted — leaving Play mode (or
    /// a domain reload) resets it just like any other unserialized field, exactly the same as if this
    /// attribute weren't there. No base class, interface, or source generator required:
    /// <code>
    /// [AlchemyDictionary]
    /// public Dictionary&lt;string, GameObject&gt; dictionary = new();
    /// </code>
    ///
    /// This intentionally drops the "actually serialize it" half of annulusgames/Alchemy's
    /// [AlchemySerializeField] — see AlchemyDictionaryFieldCache/AlchemyDictionaryDrawerGUI (Editor) for
    /// the drawing side. If you need the Dictionary's contents to survive play mode or reloads, this
    /// attribute isn't enough; that requires a serialization mechanism (custom or Alchemy's own), not
    /// just an Inspector view.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class AlchemyDictionaryAttribute : Attribute
    {
    }
}
