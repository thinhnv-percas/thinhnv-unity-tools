using System;

namespace Thinhnv.UnityTools.AlchemyLite
{
    /// <summary>
    /// Marks a field for serialization by <see cref="AlchemyDictionarySerializer"/> instead of Unity's
    /// native serializer. Pair with <c>[NonSerialized]</c> so Unity doesn't also try (and silently fail)
    /// to serialize it natively:
    /// <code>
    /// [AlchemySerializeField, NonSerialized]
    /// public Dictionary&lt;string, GameObject&gt; dictionary = new();
    /// </code>
    ///
    /// Only <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/> fields are supported by
    /// this lightweight port. The containing class must derive from <see cref="AlchemyBehaviour"/> or
    /// <see cref="AlchemyScriptableObject"/> — there is no Roslyn source generator here, so the base
    /// class is what actually implements <see cref="UnityEngine.ISerializationCallbackReceiver"/> and
    /// hosts the hidden backing storage that the field's data round-trips through.
    ///
    /// Ported (lightly — no Unity.Serialization/Unity.Properties dependency, no source generator, no
    /// unsafe code) from the idea behind annulusgames/Alchemy (https://github.com/annulusgames/Alchemy,
    /// MIT licensed), which serializes Dictionary/HashSet/etc. fields marked with the same attribute
    /// pair via a source-generated <c>ISerializationCallbackReceiver</c> and Unity.Serialization.Json.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class AlchemySerializeFieldAttribute : Attribute
    {
    }
}
