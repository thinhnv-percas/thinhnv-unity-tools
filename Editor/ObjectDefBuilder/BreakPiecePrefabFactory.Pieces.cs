using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Turning a fractured model into the pivot + piece pairs a <c>BreakPieceEffect</c> drives: each mesh
    /// becomes a pivot carrying its pose and a child piece at local identity with mesh, collider and
    /// Rigidbody.
    /// </summary>
    public static partial class BreakPiecePrefabFactory
    {
        /// <summary>
        /// Where the piece pivots are parented: the root, or a single wrapper child of it when
        /// <see cref="ObjectDefBuildEntry.usePieceWrapper"/> is on. The wrapper is a plain transform - no
        /// collider, no components - so an axis variant only has to override that one node instead of
        /// every pivot.
        /// </summary>
        private static Transform CreateContentRoot(ObjectDefBuildEntry entry, GameObject root)
        {
            if (!entry.usePieceWrapper)
            {
                return root.transform;
            }

            var wrapper = new GameObject(entry.EffectiveWrapperName);
            wrapper.transform.SetParent(root.transform, false);
            return wrapper.transform;
        }

        /// <summary>
        /// Rebuild every mesh under <paramref name="modelInstance"/> as a pivot + piece pair under
        /// <paramref name="content"/>, baking the mesh's pose relative to that node onto the pivot and its
        /// accumulated scale onto the piece. Returns the number of pieces created.
        /// </summary>
        private static int ExtractPieces(ObjectDefBuildEntry entry, Transform content,
            GameObject modelInstance, Material pieceMaterial)
        {
            Matrix4x4 toContent = content.worldToLocalMatrix;
            int count = 0;

            foreach (MeshRenderer source in modelInstance.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!source.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null)
                {
                    continue;
                }

                Matrix4x4 relative = toContent * source.transform.localToWorldMatrix;

                var pivot = new GameObject(source.name);
                pivot.transform.SetParent(content, false);
                pivot.transform.SetLocalPositionAndRotation(relative.GetPosition(), relative.rotation);

                CreatePiece(entry, pivot.transform, source.name, filter.sharedMesh,
                    pieceMaterial != null ? pieceMaterial : source.sharedMaterial, relative.lossyScale);
                count++;
            }

            return count;
        }

        /// <summary>
        /// One piece under its pivot, carrying mesh, collider and Rigidbody on a single node the way the
        /// shipped *_BreakBase prefabs do - which is also what lets <c>BreakPieceEffect.Awake</c> find the
        /// fade material with its <c>TryGetComponent&lt;Renderer&gt;</c> on the Rigidbody's own GameObject.
        ///
        /// The authored scale goes on this node because it is the one whose local scale
        /// <c>BreakPieceEffect</c> caches and multiplies by the broken object's world scale on every reuse.
        /// </summary>
        private static void CreatePiece(ObjectDefBuildEntry entry, Transform pivot, string pieceName,
            Mesh mesh, Material material, Vector3 scale)
        {
            var piece = new GameObject(pieceName);
            piece.transform.SetParent(pivot, false);
            piece.transform.localScale = scale;

            piece.AddComponent<MeshFilter>().sharedMesh = mesh;
            piece.AddComponent<MeshRenderer>().sharedMaterial = material;

            ColliderFactory.Add(piece, entry.pieceColliderMode, mesh, mesh.bounds,
                entry.roundPieceColliderSize, entry.piecePhysicMaterial);

            piece.AddComponent<Rigidbody>().mass = entry.pieceMass;
        }

        /// <summary>Add <c>BreakPieceEffect</c> and fill its piece-rigidbody array in hierarchy order.</summary>
        private static void WirePieceRigidbodies(GameObject root)
        {
            Component effect = root.AddComponent(SmashMarketBridge.BreakPieceEffectType);
            var serialized = new SerializedObject(effect);
            SerializedProperty bodies = serialized.FindProperty(SmashMarketBridge.PropPieceRigidbodies);
            if (bodies == null)
            {
                Debug.LogError($"[ObjectDefBuilder] BreakPieceEffect has no " +
                               $"'{SmashMarketBridge.PropPieceRigidbodies}' field; pieces left unwired.");
                return;
            }

            Rigidbody[] found = root.GetComponentsInChildren<Rigidbody>(true);
            bodies.arraySize = found.Length;
            for (int i = 0; i < found.Length; i++)
            {
                bodies.GetArrayElementAtIndex(i).objectReferenceValue = found[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
