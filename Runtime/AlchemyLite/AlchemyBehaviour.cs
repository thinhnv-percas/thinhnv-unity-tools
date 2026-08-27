using UnityEngine;

namespace Thinhnv.UnityTools.AlchemyLite
{
    /// <summary>
    /// Base MonoBehaviour that makes <c>[AlchemySerializeField, NonSerialized]</c> Dictionary fields on
    /// the derived class actually round-trip through Unity's serializer (domain reload, prefab save,
    /// scene save). See <see cref="AlchemySerializeFieldAttribute"/> for the field-level usage.
    /// </summary>
    public abstract class AlchemyBehaviour : MonoBehaviour, ISerializationCallbackReceiver
    {
        [SerializeField, HideInInspector]
        private AlchemyDictionaryStorage alchemyStorage = new();

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            AlchemyDictionarySerializer.OnBeforeSerialize(this, alchemyStorage);
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            AlchemyDictionarySerializer.OnAfterDeserialize(this, alchemyStorage);
        }
    }
}
