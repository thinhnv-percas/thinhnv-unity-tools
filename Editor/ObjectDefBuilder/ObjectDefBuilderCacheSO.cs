using System;
using System.Collections.Generic;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Everything needed to (re)build one <c>ObjectDefinitionSO</c>: the target asset, the naming
    /// and physics settings shared by all its sizes, and one <see cref="ObjectDefBuildRow"/> per size.
    /// </summary>
    [Serializable]
    public class ObjectDefBuildEntry
    {
        public string label = "New Object";

        [Header("Target")]
        [Tooltip("The ObjectDefinitionSO to write into. Left empty, the build creates one next to the prefabs.")]
        public ScriptableObject definition;

        [Tooltip("Name of an ObjectType enum value, e.g. IceSquare.")]
        public string objectTypeName = string.Empty;

        [Tooltip("Where a missing ObjectDefinitionSO is created.")]
        public string definitionFolder = "Assets/@SmashMarket/Data/Object";

        [Header("Naming")]
        [Tooltip("Model prefab name stem: '<prefix>' for 1x1, '<prefix>_2x' for variants.")]
        public string modelPrefix = string.Empty;

        [Tooltip("Break prefab name stem: '<prefix>_2x_Break_Piece'. Falls back to the model prefix.")]
        public string breakPrefix = string.Empty;

        public string prefabFolder = "Assets/_Use/GameObject";
        public string materialFolder = "Assets/_Use/Material";

        [Tooltip("Folder scanned by Auto-Fill Sources for '<name> 1x<n>' models, break models and textures.")]
        public string sourceFolder = "Assets/@SmashMarket/3D Models";

        [Header("Materials")]
        public MaterialRecipe modelMaterial = new MaterialRecipe();
        public MaterialRecipe pieceMaterial = new MaterialRecipe();

        [Header("Physics")]
        public PhysicsMaterial objectPhysicMaterial;
        public PhysicsMaterial piecePhysicMaterial;

        [Tooltip("Collider added to the object prefab. Mesh mode uses the model's largest renderer.")]
        public ColliderMode objectColliderMode = ColliderMode.MeshConvex;

        [Tooltip("Collider added to every break piece. The shipped prefabs use convex mesh.")]
        public ColliderMode pieceColliderMode = ColliderMode.MeshConvex;

        [Tooltip("Round the object BoxCollider's size to whole units (a 1x2 object gets a 1x2x1 box).")]
        public bool roundObjectColliderSize;

        [Tooltip("Round each piece BoxCollider's size to whole units.")]
        public bool roundPieceColliderSize;

        public float objectMass = 2f;
        public float pieceMass = 0.05f;

        [Header("Hierarchy")]
        public string objectTag = "Object";
        public int objectLayer = 6;   // Object
        public int pieceLayer = 9;    // Break Effect
        public Vector3 modelRotation = Vector3.zero;

        [Tooltip("Applied to the fractured model before its pieces are extracted; the shipped prefabs use Y 180.")]
        public Vector3 breakRotation = new Vector3(0f, 180f, 0f);

        [Tooltip("Insert a child GameObject that carries the collider and holds the 3D model, " +
                 "as the shipped object prefabs do. The wrapper also takes the Model Rotation, so the " +
                 "convex mesh stays aligned with what you see.")]
        public bool useWrapper = true;

        [Tooltip("Group every piece pivot under one wrapper child of the shatter root, mirroring the object " +
                 "prefab. The wrapper carries no collider or other component - it only groups, so an axis " +
                 "variant overrides that single transform instead of every pivot.\n\n" +
                 "Off by default: the shipped *_BreakBase prefabs parent their pivots straight to the root.")]
        public bool usePieceWrapper;

        public string wrapperName = "Wrapper";

        /// <summary>The wrapper node's name, never empty.</summary>
        public string EffectiveWrapperName =>
            string.IsNullOrWhiteSpace(wrapperName) ? "Wrapper" : wrapperName;

        [Tooltip("Unpack the dragged FBX inside the generated prefab, as the shipped object prefabs do.")]
        public bool unpackModel = true;

        [Header("Output")]
        [Tooltip("How the three axis variants of a stretched model are produced: rotate a shared base, " +
                 "build each from its own model, or share one prefab across all axes.")]
        public ModelVariantMode modelVariantMode = ModelVariantMode.RotateBase;

        [Tooltip("Off: a prefab that already exists is kept as-is and only reused, so hand edits survive. " +
                 "Missing prefabs are still created either way.")]
        public bool overwritePrefabs = true;

        public BreakTargetSlot breakSlot = BreakTargetSlot.Shatter;
        public int maxMagnitude = 5;

        public List<ObjectDefBuildRow> rows = new List<ObjectDefBuildRow>();

        /// <summary>Break prefab name stem, falling back to the model prefix.</summary>
        public string EffectiveBreakPrefix =>
            string.IsNullOrWhiteSpace(breakPrefix) ? modelPrefix : breakPrefix;

        /// <summary>
        /// Upper bound on <see cref="maxMagnitude"/>. The level data only ever reaches 8 (Ice Square);
        /// the headroom is there so the tool is not the thing blocking a longer object.
        /// </summary>
        public const int MagnitudeLimit = 16;

        /// <summary>Grow/shrink <see cref="rows"/> so there is exactly one row per magnitude 1..maxMagnitude.</summary>
        public void SyncRows()
        {
            maxMagnitude = Mathf.Clamp(maxMagnitude, 1, MagnitudeLimit);

            while (rows.Count < maxMagnitude)
            {
                rows.Add(new ObjectDefBuildRow { magnitude = rows.Count + 1 });
            }

            if (rows.Count > maxMagnitude)
            {
                rows.RemoveRange(maxMagnitude, rows.Count - maxMagnitude);
            }

            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].magnitude = i + 1;
            }
        }
    }

    /// <summary>
    /// Persistent cache for the Object Definition Builder: keeps every dragged model / break model /
    /// texture and the prefabs generated from them, so a definition can be rebuilt or tweaked later
    /// without re-dragging anything.
    ///
    /// Create via: Assets &gt; Create &gt; Thinhnv &gt; Object Definition Build Cache.
    /// </summary>
    [CreateAssetMenu(fileName = "ObjectDefBuildCache",
        menuName = "Thinhnv/Object Definition Build Cache", order = 20)]
    public class ObjectDefBuilderCacheSO : ScriptableObject
    {
        [SerializeField] private List<ObjectDefBuildEntry> entries = new List<ObjectDefBuildEntry>();

        public List<ObjectDefBuildEntry> Entries => entries;
    }
}
