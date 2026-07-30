using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// The stretched axis of a size variant. Values mirror the game's <c>SizeAxis</c> enum
    /// (asserted by <see cref="SmashMarketBridge.VerifySizeAxisMirror"/>).
    ///
    /// The fractured models are authored along Y, so an axis also implies the rotation that
    /// re-aligns a break arrangement - see <see cref="Rotation"/>.
    /// </summary>
    public enum BuildAxis
    {
        None = 0,
        X = 1,
        Y = 2,
        Z = 3,
    }

    public static class BuildAxisExtensions
    {
        /// <summary>The three stretchable axes, in the order the shipped prefabs are named.</summary>
        public static readonly BuildAxis[] All = { BuildAxis.X, BuildAxis.Y, BuildAxis.Z };

        /// <summary>Suffix letter used in prefab names: "_2x" / "_2y" / "_2z".</summary>
        public static char Suffix(this BuildAxis axis) => axis switch
        {
            BuildAxis.X => 'x',
            BuildAxis.Y => 'y',
            BuildAxis.Z => 'z',
            _ => '?',
        };

        /// <summary>
        /// Rotation that turns the Y-authored break arrangement into this axis. Matches the
        /// overrides in the shipped *_Break_Piece prefab variants: X is -90 about Z, Z is +90
        /// about X, Y is identity.
        /// </summary>
        public static Quaternion Rotation(this BuildAxis axis) => axis switch
        {
            BuildAxis.X => Quaternion.Euler(0f, 0f, -90f),
            BuildAxis.Z => Quaternion.Euler(90f, 0f, 0f),
            _ => Quaternion.identity,
        };

        /// <summary>
        /// The level-data size vector this (axis, magnitude) stands for - the value
        /// <c>SizeKey.Decompose</c> reads back. Magnitude sits on the stretched axis, the other two
        /// stay 1: X 3 is (3,1,1), Y 3 is (1,3,1), Z 3 is (1,1,3).
        /// </summary>
        public static Vector3Int Size(this BuildAxis axis, int magnitude) => axis switch
        {
            BuildAxis.X => new Vector3Int(magnitude, 1, 1),
            BuildAxis.Y => new Vector3Int(1, magnitude, 1),
            BuildAxis.Z => new Vector3Int(1, 1, magnitude),
            _ => Vector3Int.one,
        };

        /// <summary>"2 x 1 x 1"-style label for the size an (axis, magnitude) resolves to.</summary>
        public static string SizeLabel(this BuildAxis axis, int magnitude)
        {
            Vector3Int size = axis.Size(magnitude);
            return $"{size.x} x {size.y} x {size.z}";
        }
    }
}
