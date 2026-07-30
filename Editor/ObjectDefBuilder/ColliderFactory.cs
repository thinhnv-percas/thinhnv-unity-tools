using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Adds the collider a <see cref="ColliderMode"/> asks for. Shared by the object prefab and the
    /// break pieces so both honour the same modes and the same size rounding.
    /// </summary>
    public static class ColliderFactory
    {
        /// <summary>
        /// Add a collider to <paramref name="owner"/>. Mesh mode needs <paramref name="mesh"/>; box mode
        /// is fitted to <paramref name="localBounds"/> (in <paramref name="owner"/>'s local space) and
        /// optionally rounded to whole units. Returns null for <see cref="ColliderMode.None"/> or when
        /// there is nothing to fit.
        /// </summary>
        public static Collider Add(GameObject owner, ColliderMode mode, Mesh mesh, Bounds localBounds,
            bool roundSize, PhysicsMaterial physicsMaterial)
        {
            Collider collider = mode switch
            {
                ColliderMode.MeshConvex => AddConvexMesh(owner, mesh),
                ColliderMode.BoxBounds => AddBox(owner, localBounds, roundSize),
                _ => null,
            };

            if (collider != null)
            {
                collider.sharedMaterial = physicsMaterial;
            }

            return collider;
        }

        private static Collider AddConvexMesh(GameObject owner, Mesh mesh)
        {
            if (mesh == null)
            {
                Debug.LogWarning($"[ObjectDefBuilder] '{owner.name}' has no mesh for a convex MeshCollider.");
                return null;
            }

            MeshCollider collider = owner.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            collider.convex = true;
            return collider;
        }

        private static Collider AddBox(GameObject owner, Bounds localBounds, bool roundSize)
        {
            if (localBounds.size == Vector3.zero)
            {
                Debug.LogWarning($"[ObjectDefBuilder] '{owner.name}' has empty bounds; no BoxCollider added.");
                return null;
            }

            BoxCollider collider = owner.AddComponent<BoxCollider>();
            collider.center = localBounds.center;
            collider.size = roundSize ? RoundSize(localBounds.size) : localBounds.size;
            return collider;
        }

        /// <summary>
        /// Round each extent to the nearest whole unit so a 1x2 object gets an exactly 1x2x1 box.
        /// A component that would round to zero keeps its measured value - otherwise a thin shard
        /// would collapse into a degenerate collider.
        /// </summary>
        private static Vector3 RoundSize(Vector3 size) => new Vector3(
            RoundExtent(size.x), RoundExtent(size.y), RoundExtent(size.z));

        private static float RoundExtent(float extent)
        {
            float rounded = Mathf.Round(extent);
            return Mathf.Approximately(rounded, 0f) ? extent : rounded;
        }
    }
}
