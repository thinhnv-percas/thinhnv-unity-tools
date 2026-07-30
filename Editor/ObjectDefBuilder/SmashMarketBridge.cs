using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Late-bound access to the game types this tool authors (<c>ObjectDefinitionSO</c>,
    /// <c>ObjectElement</c>, <c>BreakPieceEffect</c>, <c>ObjectType</c>).
    ///
    /// Why reflection: those types live in the predefined <c>Assembly-CSharp</c>, and Unity does not
    /// allow an .asmdef assembly (this package) to reference a predefined assembly. Every write goes
    /// through <see cref="UnityEditor.SerializedObject"/> using the field-name constants below, so the
    /// only coupling is these strings - keep them in sync if the game renames a field.
    /// </summary>
    public static class SmashMarketBridge
    {
        private const string GameAssembly = "Assembly-CSharp";

        // --- game type names ----------------------------------------------------
        private const string ObjectDefinitionTypeName = "ObjectDefinitionSO";
        private const string ObjectElementTypeName = "ObjectElement";
        private const string BreakPieceEffectTypeName = "BreakPieceEffect";
        private const string ObjectTypeEnumName = "ObjectType";
        private const string SizeAxisEnumName = "SizeAxis";

        // --- ObjectDefinitionSO field paths -------------------------------------
        public const string PropType = "type";
        public const string PropBaseModel = "baseModel";
        public const string PropBaseBreakEffects = "baseBreakEffects";
        public const string PropVariants = "variants";
        public const string PropVariantAxis = "axis";
        public const string PropVariantMagnitude = "magnitude";
        public const string PropVariantModel = "model";
        public const string PropVariantBreakEffects = "breakEffects";
        public const string PropBreakShatter = "shatter";
        public const string PropBreakTntShatter = "tntShatter";

        // --- component field paths ----------------------------------------------
        public const string PropObjectRigidbody = "objectRigidbody";
        public const string PropObjectCollider = "objectCollider";
        public const string PropPieceRigidbodies = "pieceRigidbodies";

        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        public static Type ObjectDefinitionType => Resolve(ObjectDefinitionTypeName);
        public static Type ObjectElementType => Resolve(ObjectElementTypeName);
        public static Type BreakPieceEffectType => Resolve(BreakPieceEffectTypeName);
        public static Type ObjectTypeEnum => Resolve(ObjectTypeEnumName);
        public static Type SizeAxisEnum => Resolve(SizeAxisEnumName);

        /// <summary>True when every game type this tool needs was found.</summary>
        public static bool IsAvailable =>
            ObjectDefinitionType != null && ObjectElementType != null &&
            BreakPieceEffectType != null && ObjectTypeEnum != null;

        /// <summary>Human-readable list of the game types that could not be resolved.</summary>
        public static string MissingTypesReport()
        {
            var missing = new List<string>();
            Check(ObjectDefinitionTypeName, ObjectDefinitionType);
            Check(ObjectElementTypeName, ObjectElementType);
            Check(BreakPieceEffectTypeName, BreakPieceEffectType);
            Check(ObjectTypeEnumName, ObjectTypeEnum);
            return string.Join(", ", missing);

            void Check(string name, Type type)
            {
                if (type == null)
                {
                    missing.Add(name);
                }
            }
        }

        /// <summary>Names of every <c>ObjectType</c> enum value, in declaration order.</summary>
        public static string[] ObjectTypeNames()
        {
            Type type = ObjectTypeEnum;
            return type == null ? Array.Empty<string>() : Enum.GetNames(type);
        }

        /// <summary>
        /// The raw integer behind an <c>ObjectType</c> name (the value stored in the level JSON),
        /// or null when the name is unknown.
        /// </summary>
        public static int? ObjectTypeValue(string name)
        {
            Type type = ObjectTypeEnum;
            if (type == null || string.IsNullOrEmpty(name) || !Enum.IsDefined(type, name))
            {
                return null;
            }

            return Convert.ToInt32(Enum.Parse(type, name));
        }

        /// <summary>
        /// Raw <c>SizeAxis</c> value for an axis. Mirrors the game enum (None 0, X 1, Y 2, Z 3);
        /// <see cref="VerifySizeAxisMirror"/> asserts the mirror still holds.
        /// </summary>
        public static int SizeAxisValue(BuildAxis axis) => (int)axis;

        /// <summary>
        /// Log an error if the game's <c>SizeAxis</c> enum no longer matches <see cref="BuildAxis"/> -
        /// the one place where this tool hard-codes game enum numbers.
        /// </summary>
        public static void VerifySizeAxisMirror()
        {
            Type type = SizeAxisEnum;
            if (type == null)
            {
                return;
            }

            foreach (BuildAxis axis in new[] { BuildAxis.X, BuildAxis.Y, BuildAxis.Z })
            {
                string name = axis.ToString();
                if (!Enum.IsDefined(type, name) || Convert.ToInt32(Enum.Parse(type, name)) != (int)axis)
                {
                    Debug.LogError($"[ObjectDefBuilder] SizeAxis.{name} no longer equals {(int)axis}; " +
                                   "update BuildAxis in the tool to match the game enum.");
                }
            }
        }

        private static Type Resolve(string name)
        {
            if (TypeCache.TryGetValue(name, out Type cached) && cached != null)
            {
                return cached;
            }

            Type type = Type.GetType($"{name}, {GameAssembly}") ?? ScanLoadedAssemblies(name);
            TypeCache[name] = type;
            return type;
        }

        private static Type ScanLoadedAssemblies(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(name, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }
    }
}
