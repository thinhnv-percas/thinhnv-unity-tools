using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;

/// <summary>Which kind of member a <see cref="MemberValueEntry"/> came from.</summary>
public enum MemberValueKind
{
    Field,
    Property,
    Method
}

/// <summary>
/// One member found by <see cref="MemberValueScan"/>, resolved once and read on demand.
/// <para>
/// Reading is not free of consequence, which is why the viewer decides *when* to call
/// <see cref="TryRead"/> rather than doing it for everything: a property getter can have side effects
/// (<c>Renderer.material</c> instantiates a material just by being read) and a zero-argument method
/// that returns a value can still mutate (<c>Queue&lt;T&gt;.Dequeue</c>, <c>IEnumerator.MoveNext</c>).
/// Fields are the only kind that is always safe to read.
/// </para>
/// </summary>
public readonly struct MemberValueEntry
{
    private readonly MemberInfo info;

    public readonly MemberValueKind kind;

    /// <summary>Nicified member name, used as the row label.</summary>
    public readonly string label;

    /// <summary>Declared type of the field / property / return value.</summary>
    public readonly Type valueType;

    public readonly bool isStatic;

    /// <summary>True when Unity or the BCL declares this member, rather than project code.</summary>
    public readonly bool isExternal;

    /// <summary>Parameters to fill before a method can run; empty for fields, properties and getters.</summary>
    public readonly ParameterInfo[] parameters;

    public string name => info.Name;

    public string declaringType => info.DeclaringType != null ? info.DeclaringType.Name : string.Empty;

    /// <summary>False for a void method, whose only observable result is what it did.</summary>
    public bool returnsValue => valueType != typeof(void);

    /// <summary>
    /// True when the member cannot simply be read: a method that takes arguments, or returns nothing.
    /// These are only ever run from an explicit button press.
    /// </summary>
    public bool needsCall => kind == MemberValueKind.Method && (parameters.Length > 0 || !returnsValue);

    internal MemberValueEntry(MemberInfo info, MemberValueKind kind, Type valueType, bool isStatic, bool isExternal,
        ParameterInfo[] parameters)
    {
        this.info = info;
        this.kind = kind;
        this.valueType = valueType;
        this.isStatic = isStatic;
        this.isExternal = isExternal;
        this.parameters = parameters ?? MemberValueScan.NoParameters;
        label = ObjectNames.NicifyVariableName(info.Name);
    }

    /// <summary>
    /// Read the field / evaluate the property / invoke the method on <paramref name="owner"/>.
    /// A member that throws reports <paramref name="error"/> instead of propagating, so one bad getter
    /// cannot take down the window.
    /// </summary>
    public bool TryRead(object owner, out object value, out string error)
    {
        return TryInvoke(owner, null, out value, out error);
    }

    /// <summary>
    /// Same as <see cref="TryRead"/>, but passes <paramref name="args"/> to a method that takes parameters.
    /// A void method reports success with a null <paramref name="value"/>; read <see cref="returnsValue"/>
    /// to tell that apart from a method that genuinely returned null.
    /// </summary>
    public bool TryInvoke(object owner, object[] args, out object value, out string error)
    {
        value = null;
        error = null;
        object instance = isStatic ? null : owner;

        try
        {
            if (info is FieldInfo field) value = field.GetValue(instance);
            else if (info is PropertyInfo property) value = property.GetValue(instance);
            else if (info is MethodInfo method) value = method.Invoke(instance, args);
            else
            {
                error = "unsupported member";
                return false;
            }
            return true;
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

/// <summary>
/// Enumerates every member of a type worth showing — fields (serialized or not), readable properties, and
/// methods, including the void and parameterised ones that can only be run from a button — without needing
/// any attribute on them. Results are cached
/// per type, since the viewer repaints far too often to redo reflection each frame.
/// <para>
/// The scan is deliberately a superset: static and Unity-declared members are included and tagged, and the
/// window filters them out by default. That way toggling a filter never re-runs reflection.
/// </para>
/// </summary>
public static class MemberValueScan
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static |
                                       BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.DeclaredOnly;

    internal static readonly ParameterInfo[] NoParameters = new ParameterInfo[0];

    private static readonly MemberValueEntry[] None = new MemberValueEntry[0];
    private static readonly Dictionary<Type, MemberValueEntry[]> Cache = new Dictionary<Type, MemberValueEntry[]>();

    /// <summary>Every readable member of <paramref name="type"/> and its base classes, fields first.</summary>
    public static MemberValueEntry[] Of(Type type)
    {
        if (type == null) return None;
        if (Cache.TryGetValue(type, out MemberValueEntry[] cached)) return cached;

        var fields = new List<MemberValueEntry>();
        var properties = new List<MemberValueEntry>();
        var methods = new List<MemberValueEntry>();

        // Overrides report the base declaration too; keep the most-derived one, which the walk sees first.
        var seenProperties = new HashSet<string>();
        var seenMethods = new HashSet<string>();

        // DeclaredOnly + a manual walk: private members of base classes are invisible to a plain GetFields.
        for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            bool external = IsExternal(current);
            CollectFields(current, external, fields);
            CollectProperties(current, external, seenProperties, properties);
            CollectMethods(current, external, seenMethods, methods);
        }

        var all = new List<MemberValueEntry>(fields.Count + properties.Count + methods.Count);
        all.AddRange(fields);
        all.AddRange(properties);
        all.AddRange(methods);

        MemberValueEntry[] result = all.ToArray();
        Cache[type] = result;
        return result;
    }

    private static void CollectFields(Type type, bool external, List<MemberValueEntry> into)
    {
        foreach (FieldInfo field in type.GetFields(Flags))
        {
            // const is source, not state.
            if (field.IsLiteral) continue;
            // Auto-property backing fields, closures and field-like event storage: noise next to the real member.
            if (field.IsDefined(typeof(CompilerGeneratedAttribute), false) || field.Name.IndexOf('<') >= 0) continue;
            if (IsObsoleteError(field)) continue;

            into.Add(new MemberValueEntry(field, MemberValueKind.Field, field.FieldType, field.IsStatic, external, null));
        }
    }

    private static void CollectProperties(Type type, bool external, HashSet<string> seen, List<MemberValueEntry> into)
    {
        foreach (PropertyInfo property in type.GetProperties(Flags))
        {
            // Write-only has nothing to show; an indexer needs arguments we don't have.
            if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
            if (IsObsoleteError(property)) continue;
            if (!seen.Add(property.Name)) continue;

            into.Add(new MemberValueEntry(property, MemberValueKind.Property, property.PropertyType,
                property.GetMethod.IsStatic, external, null));
        }
    }

    private static void CollectMethods(Type type, bool external, HashSet<string> seen, List<MemberValueEntry> into)
    {
        foreach (MethodInfo method in type.GetMethods(Flags))
        {
            // IsSpecialName covers property accessors, operators and event add/remove — all duplicates here.
            if (method.IsSpecialName || method.IsGenericMethodDefinition) continue;
            if (IsObsoleteError(method)) continue;

            // ref/out parameters cannot be filled from a boxed argument array, so the method is uncallable.
            ParameterInfo[] parameters = method.GetParameters();
            bool byRef = false;
            foreach (ParameterInfo parameter in parameters)
            {
                if (parameter.ParameterType.IsByRef) byRef = true;
            }
            if (byRef) continue;

            // Overloads share a name, so only the first is kept — the viewer has no way to pick between them.
            if (!seen.Add(method.Name)) continue;

            into.Add(new MemberValueEntry(method, MemberValueKind.Method, method.ReturnType, method.IsStatic,
                external, parameters.Length > 0 ? parameters : NoParameters));
        }
    }

    /// <summary>Obsolete-as-error members are removed API kept for the compiler; touching them tends to throw.</summary>
    private static bool IsObsoleteError(MemberInfo member)
    {
        var obsolete = member.GetCustomAttribute<ObsoleteAttribute>();
        return obsolete != null && obsolete.IsError;
    }

    /// <summary>
    /// True for types owned by Unity or the BCL. This is the same assembly-name test Unity itself uses to
    /// rank custom editors, and it is what the window's "Unity" filter keys on — project code lives in
    /// Assembly-CSharp or its own asmdefs, so anything else is somebody else's member.
    /// </summary>
    private static bool IsExternal(Type type)
    {
        string assembly = type.Assembly.GetName().Name;
        return assembly.StartsWith("Unity", StringComparison.Ordinal) ||
               assembly.StartsWith("System", StringComparison.Ordinal) ||
               assembly == "mscorlib" || assembly == "netstandard";
    }
}
