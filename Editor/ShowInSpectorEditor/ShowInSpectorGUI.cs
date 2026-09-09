using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Shared drawing for the generic inspectors: renders a read-only "Show In Spector" section with the
/// LIVE value of every member marked with <see cref="ShowInSpectorAttribute"/> — or, for fields, with
/// <see cref="TooltipAttribute"/> whose text is <c>"ShowInSpector"</c>. Three member kinds are picked up:
/// <list type="bullet">
/// <item>non-serialized fields (private/internal without <c>[SerializeField]</c>, <c>[NonSerialized]</c>,
/// static, or of a type Unity cannot serialize) — serialized ones already show in the default inspector;</item>
/// <item>readable properties without index parameters;</item>
/// <item>zero-parameter, non-generic methods that return a value.</item>
/// </list>
/// Values are read every repaint (see <see cref="RequiresConstantRepaint"/>), so getters and methods used
/// here should stay cheap and side-effect free. Reads are guarded: a throwing member shows its exception
/// instead of breaking the inspector. Multi-object selections show <c>—</c> where targets disagree.
/// <para>
/// Unity allows a single custom editor per type, so this does not declare its own
/// <c>[CustomEditor]</c> — it is called from <see cref="ContextMenuButtonEditor"/> and
/// <see cref="ScriptableObjectContextMenuButtonEditor"/>, which already cover MonoBehaviour and
/// ScriptableObject.
/// </para>
/// </summary>
public static partial class ShowInSpectorGUI
{
    /// <summary>Tooltip texts accepted as the marker, longest-first so prefixes can't shadow each other.</summary>
    private static readonly string[] Markers = { "ShowInInspector", "ShowInSpector" };

    private const BindingFlags MemberFlags = BindingFlags.Instance | BindingFlags.Static |
                                             BindingFlags.Public | BindingFlags.NonPublic |
                                             BindingFlags.DeclaredOnly;

    private const string ExpandedPrefKey = "ShowInSpector.Expanded.";

    // Reflection is walked once per type; inspectors repaint far too often to redo it every frame.
    private static readonly Dictionary<Type, Member[]> MemberCache = new Dictionary<Type, Member[]>();

    // Built lazily: EditorStyles is only valid once a GUI pass is running.
    private static GUIStyle headerStyle;

    public static void Draw(UnityEngine.Object[] targets)
    {
        UnityEngine.Object primary = targets != null && targets.Length > 0 ? targets[0] : null;
        if (primary == null) return;

        Member[] members = GetMembers(primary.GetType());
        if (members.Length == 0) return;

        GUILayout.Space(6);
        Rect rect = EditorGUILayout.GetControlRect(false, 10);
        rect.height = 2;
        EditorGUI.DrawRect(rect, new Color32(232, 143, 43, 255));

        // Collapsing the section is also what stops the constant repaint, so keep the state sticky.
        string prefKey = ExpandedPrefKey + primary.GetType().FullName;
        bool expanded = EditorPrefs.GetBool(prefKey, true);
        bool toggled = EditorGUILayout.Foldout(expanded, $"Show In Spector  [{members.Length}]", true, HeaderStyle);
        if (toggled != expanded) EditorPrefs.SetBool(prefKey, toggled);
        if (!toggled) return;

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            foreach (Member member in members)
            {
                DrawMember(member, targets);
            }
        }
    }

    private static GUIStyle HeaderStyle =>
        headerStyle ?? (headerStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold });

    /// <summary>True while the target has marked members and its section is open — values must stay live.</summary>
    public static bool RequiresConstantRepaint(UnityEngine.Object target)
    {
        if (target == null) return false;
        Type type = target.GetType();
        return GetMembers(type).Length > 0 &&
               EditorPrefs.GetBool(ExpandedPrefKey + type.FullName, true);
    }

    private static void DrawMember(Member member, UnityEngine.Object[] targets)
    {
        // Read every target so a multi-object selection can fall back to the mixed-value dash.
        if (!member.TryRead(targets[0], out object value, out string error))
        {
            EditorGUILayout.LabelField(member.label, $"⚠ {error}");
            return;
        }

        bool mixed = false;
        for (int i = 1; i < targets.Length && !mixed; i++)
        {
            if (!member.TryRead(targets[i], out object other, out _) || !Equals(value, other)) mixed = true;
        }

        // DrawValue applies the disabled scope per leaf widget, so collection foldouts stay clickable.
        DrawValue(member.label, member.valueType, value, mixed, member.key);
    }

    private static Member[] GetMembers(Type type)
    {
        if (MemberCache.TryGetValue(type, out Member[] cached)) return cached;

        var members = new List<Member>();
        var fields = new List<Member>();
        var properties = new List<Member>();
        var methods = new List<Member>();

        // DeclaredOnly + manual walk: private members of base classes are invisible to a plain GetFields.
        for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            if (current == typeof(MonoBehaviour) || current == typeof(ScriptableObject) ||
                current == typeof(Component) || current == typeof(UnityEngine.Object))
            {
                break;
            }

            CollectFields(current, fields);
            CollectProperties(current, properties);
            CollectMethods(current, methods);
        }

        // Declared members first, base classes after — the walk above visits the hierarchy top-down already.
        members.AddRange(fields);
        members.AddRange(properties);
        members.AddRange(methods);

        Member[] result = members.ToArray();
        MemberCache[type] = result;
        return result;
    }

    private static void CollectFields(Type type, List<Member> into)
    {
        foreach (FieldInfo field in type.GetFields(MemberFlags))
        {
            if (!TryGetMarker(field, out string label)) continue;
            // Serialized fields are already drawn by DrawDefaultInspector; showing them twice is noise.
            if (IsUnitySerialized(field)) continue;
            into.Add(new Member(field, label ?? ObjectNames.NicifyVariableName(field.Name), field.FieldType));
        }
    }

    private static void CollectProperties(Type type, List<Member> into)
    {
        foreach (PropertyInfo property in type.GetProperties(MemberFlags))
        {
            if (!TryGetMarker(property, out string label)) continue;
            // Write-only properties have nothing to show, and indexers need arguments we don't have.
            if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
            into.Add(new Member(property, label ?? ObjectNames.NicifyVariableName(property.Name),
                property.PropertyType));
        }
    }

    private static void CollectMethods(Type type, List<Member> into)
    {
        foreach (MethodInfo method in type.GetMethods(MemberFlags))
        {
            if (!TryGetMarker(method, out string label)) continue;
            // Only a plain value-returning getter can be invoked blindly on every repaint.
            if (method.ReturnType == typeof(void) || method.IsGenericMethodDefinition) continue;
            if (method.GetParameters().Length > 0) continue;
            string name = label ?? $"{ObjectNames.NicifyVariableName(method.Name)} ()";
            into.Add(new Member(method, name, method.ReturnType));
        }
    }

    /// <summary>
    /// True when the marker is present, either as <see cref="ShowInSpectorAttribute"/> or as a
    /// <see cref="TooltipAttribute"/> reading <c>"ShowInSpector"</c>. An optional custom label may follow
    /// the marker after <c>:</c>, <c>|</c> or <c>-</c> (e.g. <c>[Tooltip("ShowInSpector: Live Speed")]</c>).
    /// </summary>
    private static bool TryGetMarker(MemberInfo member, out string label)
    {
        label = null;

        var explicitMarker = member.GetCustomAttribute<ShowInSpectorAttribute>();
        if (explicitMarker != null)
        {
            label = string.IsNullOrWhiteSpace(explicitMarker.label) ? null : explicitMarker.label.Trim();
            return true;
        }

        string tooltip = member.GetCustomAttribute<TooltipAttribute>()?.tooltip?.Trim();
        if (string.IsNullOrEmpty(tooltip)) return false;

        foreach (string marker in Markers)
        {
            if (!tooltip.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = tooltip.Substring(marker.Length).Trim(' ', ':', '|', '-');
            label = string.IsNullOrEmpty(rest) ? null : rest;
            return true;
        }
        return false;
    }

    /// <summary>Mirrors Unity's field serialization rules closely enough to spot fields it already draws.</summary>
    private static bool IsUnitySerialized(FieldInfo field)
    {
        if (field.IsStatic || field.IsLiteral || field.IsInitOnly) return false;
        if (field.IsDefined(typeof(NonSerializedAttribute), false)) return false;
        if (field.IsDefined(typeof(SerializeReference), false)) return true;
        if (!field.IsPublic && !field.IsDefined(typeof(SerializeField), false)) return false;
        return IsSerializableType(field.FieldType);
    }

    private static bool IsSerializableType(Type type)
    {
        if (type.IsArray) return type.GetArrayRank() == 1 && IsSerializableType(type.GetElementType());
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return IsSerializableType(type.GetGenericArguments()[0]);
        if (type.IsPrimitive || type.IsEnum || type == typeof(string)) return true;
        if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return true;
        // Custom structs/classes need [Serializable]; open generics are never serialized by Unity.
        return !type.IsGenericType && type.IsDefined(typeof(SerializableAttribute), false);
    }

    /// <summary>One marked field / property / method, resolved once and read per repaint.</summary>
    private readonly struct Member
    {
        private readonly MemberInfo info;
        private readonly bool isStatic;

        public readonly string label;
        public readonly Type valueType;

        /// <summary>Stable path used to key collection foldout state, so two members never share a toggle.</summary>
        public string key => $"{info.DeclaringType?.FullName}.{info.Name}";

        public Member(MemberInfo info, string label, Type valueType)
        {
            this.info = info;
            this.label = label;
            this.valueType = valueType;
            isStatic = info is FieldInfo field ? field.IsStatic
                : info is MethodInfo method ? method.IsStatic
                : ((PropertyInfo)info).GetMethod.IsStatic;
        }

        /// <summary>Read the current value; a throwing member reports <paramref name="error"/> instead.</summary>
        public bool TryRead(UnityEngine.Object target, out object value, out string error)
        {
            value = null;
            error = null;
            object instance = isStatic ? null : target;

            try
            {
                switch (info)
                {
                    case FieldInfo field: value = field.GetValue(instance); return true;
                    case PropertyInfo property: value = property.GetValue(instance); return true;
                    case MethodInfo method: value = method.Invoke(instance, null); return true;
                    default: error = "unsupported member"; return false;
                }
            }
            catch (Exception exception)
            {
                // TargetInvocationException hides the real cause one level down.
                Exception cause = exception is TargetInvocationException invocation && invocation.InnerException != null
                    ? invocation.InnerException
                    : exception;
                error = $"{cause.GetType().Name}: {cause.Message}";
                return false;
            }
        }
    }
}
