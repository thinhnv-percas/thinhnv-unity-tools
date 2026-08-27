using System;

namespace Thinhnv.UnityTools.LiveDictionary
{
    /// <summary>
    /// Shows a <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/> field in the Inspector
    /// as a live debug/tuning view: read-only outside Play mode, editable (add/remove/edit entries)
    /// while Play mode is running. The field is never serialized or persisted — leaving Play mode (or
    /// a domain reload) resets it just like any other unserialized field, exactly the same as if this
    /// attribute weren't there. No base class, interface, or source generator required:
    /// <code>
    /// [LiveDictionary]
    /// public Dictionary&lt;string, GameObject&gt; dictionary = new();
    /// </code>
    ///
    /// This is a view only, not a serializer — if you need the Dictionary's contents to survive Play
    /// mode or a domain reload, this attribute isn't enough; that requires an actual serialization
    /// mechanism, not just an Inspector view. See LiveDictionaryFieldCache/LiveDictionaryDrawerGUI
    /// (Editor) for the drawing side.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class LiveDictionaryAttribute : Attribute
    {
    }
}
