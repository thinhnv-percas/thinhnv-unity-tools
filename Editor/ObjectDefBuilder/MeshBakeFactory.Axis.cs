using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Per-axis baked meshes: instead of the three axis variants sharing the base's mesh and each rotating
    /// its wrapper, every variant gets its own mesh with the axis rotation baked into the vertices and its
    /// wrapper back at identity.
    ///
    /// This keeps the variant a variant. A prefab variant may not delete GameObjects it inherits - which is
    /// why the hierarchy itself can only be baked on the base - but it may freely *override properties* on
    /// them, and that is all this needs: the MeshFilter's mesh, the collider's mesh, and the wrapper's
    /// rotation.
    /// </summary>
    public static partial class MeshBakeFactory
    {
        /// <summary>
        /// Point <paramref name="variantPrefab"/> at its own copy of <paramref name="baseMesh"/> rotated by
        /// whatever rotation the prefab actually carries, then reset that node to identity.
        ///
        /// The angle is read off the prefab, **not** from <see cref="BuildAxisExtensions.Rotation"/>, so a
        /// variant whose rotation was nudged by hand bakes the nudged angle rather than the canonical one.
        ///
        /// The mesh always derives from the base's mesh, and the total is composed as
        /// <c>current * alreadyBaked</c>. That makes a re-bake lossless (the node reads identity by then, so
        /// the stored rotation carries it) without double-applying, and lets a fresh tweak on top of an
        /// already-baked mesh accumulate correctly.
        /// </summary>
        public static bool BakeAxis(ObjectDefBuildEntry entry, ObjectDefBuildRow row,
            GameObject variantPrefab, Mesh baseMesh, BuildAxis axis)
        {
            if (variantPrefab == null || baseMesh == null)
            {
                return false;
            }

            string path = AssetDatabase.GetAssetPath(variantPrefab);
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Transform axisNode = AxisNode(contents.transform);
                Quaternion total = TotalRotation(axisNode.localRotation, row.BakedRotationFor(axis));

                Mesh saved = SaveMesh(entry, RotateMesh(baseMesh, total), $"{variantPrefab.name}_Baked");

                // The rotation now lives in the vertices, so the node goes back to identity.
                axisNode.localRotation = Quaternion.identity;

                Transform content = ContentRoot(entry, contents.transform);
                FlattenTransforms(content);
                ApplyMesh(content, saved);
                PrefabUtility.SaveAsPrefabAsset(contents, path);

                row.SetBakedRotation(axis, total);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            return true;
        }

        /// <summary>
        /// The rotation to bake, given what the node carries now and what is already in the mesh.
        ///
        /// The node means two different things depending on how we got here, and comparing it against the
        /// stored angle is what tells them apart:
        /// <list type="bullet">
        /// <item>equal to stored - a rebuild just re-applied the authored angle, so bake it once;</item>
        /// <item>identity - the mesh already holds the rotation, so keep what is stored;</item>
        /// <item>anything else - a fresh nudge on top of the baked mesh, so compose the two.</item>
        /// </list>
        /// </summary>
        private static Quaternion TotalRotation(Quaternion node, Quaternion alreadyBaked) =>
            Quaternion.Angle(node, alreadyBaked) < 0.01f ? alreadyBaked : node * alreadyBaked;

        /// <summary>
        /// The node an axis variant carries its rotation on: the prefab root's first child, which is what
        /// <see cref="AxisVariantFactory"/> rotates - the wrapper when there is one, else the model node.
        /// </summary>
        private static Transform AxisNode(Transform root) =>
            root.childCount > 0 ? root.GetChild(0) : root;

        /// <summary>
        /// Zero every transform under the content root, because the baked mesh is expressed in that node's
        /// space and has to land there exactly. Only the FBX's own node chain lives here, and it is about to
        /// be replaced by a single mesh anyway.
        /// </summary>
        private static void FlattenTransforms(Transform content)
        {
            foreach (Transform node in content.GetComponentsInChildren<Transform>(true))
            {
                if (node == content)
                {
                    continue;
                }

                node.localPosition = Vector3.zero;
                node.localRotation = Quaternion.identity;
                node.localScale = Vector3.one;
            }
        }

        /// <summary>
        /// Put the baked mesh on the first mesh node and silence the rest, since they are already merged
        /// into it. Their renderers are only *disabled* rather than their GameObjects deactivated - a
        /// variant may not delete inherited objects, and deactivating one could take a collider with it.
        /// </summary>
        private static void ApplyMesh(Transform content, Mesh mesh)
        {
            MeshFilter primary = null;
            foreach (MeshFilter filter in content.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null)
                {
                    continue;
                }

                bool isPrimary = primary == null;
                if (isPrimary)
                {
                    primary = filter;
                    filter.sharedMesh = mesh;
                }

                if (filter.TryGetComponent(out MeshRenderer renderer))
                {
                    renderer.enabled = isPrimary;
                }
            }

            foreach (MeshCollider collider in content.GetComponentsInChildren<MeshCollider>(true))
            {
                if (collider.convex)
                {
                    collider.sharedMesh = mesh;
                }
            }
        }

        /// <summary>A copy of <paramref name="source"/> with its positions, normals and tangents rotated.</summary>
        private static Mesh RotateMesh(Mesh source, Quaternion rotation)
        {
            Mesh mesh = Object.Instantiate(source);
            Matrix4x4 matrix = Matrix4x4.Rotate(rotation);

            Vector3[] vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
            }

            mesh.vertices = vertices;

            Vector3[] normals = mesh.normals;
            if (normals.Length > 0)
            {
                for (int i = 0; i < normals.Length; i++)
                {
                    normals[i] = matrix.MultiplyVector(normals[i]);
                }

                mesh.normals = normals;
            }

            Vector4[] tangents = mesh.tangents;
            if (tangents.Length > 0)
            {
                for (int i = 0; i < tangents.Length; i++)
                {
                    Vector3 direction = matrix.MultiplyVector(
                        new Vector3(tangents[i].x, tangents[i].y, tangents[i].z));
                    tangents[i] = new Vector4(direction.x, direction.y, direction.z, tangents[i].w);
                }

                mesh.tangents = tangents;
            }

            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
