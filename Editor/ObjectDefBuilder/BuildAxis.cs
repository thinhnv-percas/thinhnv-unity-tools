using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// The stretched axis/axes of a size variant. Values mirror the game's <c>SizeAxis</c> enum
    /// (asserted by <see cref="SmashMarketBridge.VerifySizeAxisMirror"/>).
    ///
    /// Single-axis (Bar): models authored along Y, rotated for X/Z.
    /// Dual-axis (Plate): models authored along YZ, rotated for XY/XZ.
    /// Triple-axis (Cube): single orientation, no rotation variants.
    /// </summary>
    public enum BuildAxis
    {
        None = 0,
        X = 1,
        Y = 2,
        Z = 3,
        XY = 4,
        YZ = 5,
        XZ = 6,
        XYZ = 7,
    }

    public enum BuildAxisFamily
    {
        Bar = 0,
        Plate = 1,
        Cube = 2,
    }

    public static class BuildAxisExtensions
    {
        public static readonly BuildAxis[] All =
        {
            BuildAxis.X, BuildAxis.Y, BuildAxis.Z,
            BuildAxis.XY, BuildAxis.YZ, BuildAxis.XZ,
            BuildAxis.XYZ,
        };

        public static readonly BuildAxis[] BarAxes = { BuildAxis.X, BuildAxis.Y, BuildAxis.Z };
        public static readonly BuildAxis[] PlateAxes = { BuildAxis.XY, BuildAxis.YZ, BuildAxis.XZ };
        public static readonly BuildAxis[] CubeAxes = { BuildAxis.XYZ };

        public static BuildAxis[] FamilyAxes(BuildAxisFamily family) => family switch
        {
            BuildAxisFamily.Plate => PlateAxes,
            BuildAxisFamily.Cube => CubeAxes,
            _ => BarAxes,
        };

        public static BuildAxisFamily Family(BuildAxis axis) => axis switch
        {
            BuildAxis.XY or BuildAxis.YZ or BuildAxis.XZ => BuildAxisFamily.Plate,
            BuildAxis.XYZ => BuildAxisFamily.Cube,
            _ => BuildAxisFamily.Bar,
        };

        /// <summary>The base axis of a family: Y for Bar, YZ for Plate, XYZ for Cube.</summary>
        public static BuildAxis BaseAxis(BuildAxisFamily family) => family switch
        {
            BuildAxisFamily.Plate => BuildAxis.YZ,
            BuildAxisFamily.Cube => BuildAxis.XYZ,
            _ => BuildAxis.Y,
        };

        public static bool IsDefined(int value) => value >= 0 && value <= 7;

        /// <summary>Suffix used in prefab names: "_2x" / "_2xy" / "_2xyz".</summary>
        public static string Suffix(this BuildAxis axis) => axis switch
        {
            BuildAxis.X => "x",
            BuildAxis.Y => "y",
            BuildAxis.Z => "z",
            BuildAxis.XY => "xy",
            BuildAxis.YZ => "yz",
            BuildAxis.XZ => "xz",
            BuildAxis.XYZ => "xyz",
            _ => "?",
        };

        /// <summary>
        /// Rotation that turns the family's base-authored arrangement onto this axis.
        /// Bar: Y = identity, X = -90° about Z, Z = +90° about X.
        /// Plate: YZ = identity, XZ = +90° about Z, XY = +90° about Y (rolls YZ onto XY).
        /// Cube: XYZ = identity (no rotation variants).
        /// </summary>
        public static Quaternion Rotation(this BuildAxis axis) => axis switch
        {
            BuildAxis.X => Quaternion.Euler(0f, 0f, -90f),
            BuildAxis.Z => Quaternion.Euler(90f, 0f, 0f),
            BuildAxis.XZ => Quaternion.Euler(0f, 0f, 90f),
            BuildAxis.XY => Quaternion.Euler(0f, 90f, 0f),
            _ => Quaternion.identity,
        };

        /// <summary>
        /// The level-data size vector this (axis, magnitude) stands for.
        /// Bar: X=(m,1,1), Y=(1,m,1), Z=(1,1,m).
        /// Plate: XY=(m,m,1), YZ=(1,m,m), XZ=(m,1,m).
        /// Cube: XYZ=(m,m,m).
        /// </summary>
        public static Vector3Int Size(this BuildAxis axis, int magnitude) => axis switch
        {
            BuildAxis.X => new Vector3Int(magnitude, 1, 1),
            BuildAxis.Y => new Vector3Int(1, magnitude, 1),
            BuildAxis.Z => new Vector3Int(1, 1, magnitude),
            BuildAxis.XY => new Vector3Int(magnitude, magnitude, 1),
            BuildAxis.YZ => new Vector3Int(1, magnitude, magnitude),
            BuildAxis.XZ => new Vector3Int(magnitude, 1, magnitude),
            BuildAxis.XYZ => new Vector3Int(magnitude, magnitude, magnitude),
            _ => Vector3Int.one,
        };

        public static string SizeLabel(this BuildAxis axis, int magnitude)
        {
            Vector3Int size = axis.Size(magnitude);
            return $"{size.x} x {size.y} x {size.z}";
        }
    }
}
