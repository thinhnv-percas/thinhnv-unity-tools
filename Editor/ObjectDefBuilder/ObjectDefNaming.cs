namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// The single source of truth for generated asset names, so the size table in the window previews
    /// exactly what a build writes.
    ///
    /// Follows the shipped convention: the uniform model is the bare prefix, variants append the
    /// magnitude and axis suffix, and effects use their own prefix.
    /// </summary>
    public static class ObjectDefNaming
    {
        /// <summary>Sub-folder holding the shared base prefabs the axis variants derive from.</summary>
        public static string BaseFolder(ObjectDefBuildEntry entry) => $"{entry.prefabFolder}/Base";

        /// <summary>"Ice_Cylinder" at magnitude 1, else "Ice_Cylinder_2x" (or "Ice_Cylinder_2" when shared).</summary>
        public static string ModelPrefab(ObjectDefBuildEntry entry, int magnitude, BuildAxis axis)
        {
            if (magnitude <= 1)
            {
                return entry.modelPrefix;
            }

            return entry.modelVariantMode == ModelVariantMode.Shared
                ? $"{entry.modelPrefix}_{magnitude}"
                : $"{entry.modelPrefix}_{magnitude}{axis.Suffix()}";
        }

        /// <summary>
        /// The base prefab axis variants derive from, named per family so Bar/Plate/Cube bases
        /// coexist: "Ice_Cylinder_2_ModelBase" (Bar), "Ice_Cylinder_2_PlateBase" (Plate),
        /// "Ice_Cylinder_2_CubeBase" (Cube).
        /// </summary>
        public static string ModelBasePrefab(ObjectDefBuildEntry entry, int magnitude,
            BuildAxisFamily family = BuildAxisFamily.Bar) =>
            $"{entry.modelPrefix}_{magnitude}_{FamilyBaseTag(family)}";

        /// <summary>"IceCircle_1x1_Break_Piece" at magnitude 1, else "IceCircle_2x_Break_Piece".</summary>
        public static string BreakPiecePrefab(ObjectDefBuildEntry entry, int magnitude, BuildAxis axis) =>
            magnitude <= 1
                ? $"{entry.EffectiveBreakPrefix}_1x1_Break_Piece"
                : $"{entry.EffectiveBreakPrefix}_{magnitude}{axis.Suffix()}_Break_Piece";

        /// <summary>
        /// The break base prefab, named per family: "IceCircle_2_BreakBase" (Bar),
        /// "IceCircle_2_PlateBreakBase" (Plate), "IceCircle_2_CubeBreakBase" (Cube).
        /// </summary>
        public static string BreakBasePrefab(ObjectDefBuildEntry entry, int magnitude,
            BuildAxisFamily family = BuildAxisFamily.Bar) =>
            $"{entry.EffectiveBreakPrefix}_{magnitude}_{FamilyBreakBaseTag(family)}";

        /// <summary>"Ice_Cylinder_1x2" - the model material for a size.</summary>
        public static string ModelMaterial(ObjectDefBuildEntry entry, int magnitude) =>
            $"{entry.modelPrefix}_1x{magnitude}";

        /// <summary>"IceCircle_1x2_Piece" - the break-piece material for a size.</summary>
        public static string PieceMaterial(ObjectDefBuildEntry entry, int magnitude) =>
            $"{entry.EffectiveBreakPrefix}_1x{magnitude}_Piece";

        private static string FamilyBaseTag(BuildAxisFamily family) => family switch
        {
            BuildAxisFamily.Plate => "PlateBase",
            BuildAxisFamily.Cube => "CubeBase",
            _ => "ModelBase",
        };

        private static string FamilyBreakBaseTag(BuildAxisFamily family) => family switch
        {
            BuildAxisFamily.Plate => "PlateBreakBase",
            BuildAxisFamily.Cube => "CubeBreakBase",
            _ => "BreakBase",
        };
    }
}
