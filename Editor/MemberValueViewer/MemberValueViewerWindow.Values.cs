using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Recursive value drawing for <see cref="MemberValueViewerWindow"/>: leaves go to
/// <see cref="MemberValueGUI"/>, collections become a capped foldout, and anything else is drilled into by
/// scanning its own members.
/// <para>
/// Two guards keep the recursion finite. A depth cap stops it going arbitrarily deep, and a
/// reference-identity set tracks the objects on the *current path* so a cycle
/// (<c>child.parent.child…</c>) reports itself instead of recursing forever. The set is a path, not a
/// history: an object is removed again on the way out, so the same object appearing under two different
/// members is still shown twice.
/// </para>
/// </summary>
public partial class MemberValueViewerWindow
{
    /// <summary>Enumeration cap — an iterator member can be long, or endless.</summary>
    private const int MaxEnumerated = 256;

    private readonly Dictionary<string, bool> foldouts = new Dictionary<string, bool>();
    private readonly HashSet<object> path = new HashSet<object>(ReferenceComparer.Instance);

    /// <summary>Identity, not equality: two equal-but-distinct objects are not the same node.</summary>
    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();

        public new bool Equals(object a, object b) => ReferenceEquals(a, b);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private void DrawValue(string label, Type declaredType, object value, string key, int depth)
    {
        Type type = Nullable.GetUnderlyingType(declaredType) ?? declaredType ?? typeof(object);
        // A member declared as object, an interface or an abstract base says nothing; the value's own type does.
        if (value != null && (type == typeof(object) || type.IsInterface || type.IsAbstract)) type = value.GetType();

        if (value == null)
        {
            MemberValueGUI.DrawLeaf(label, type, null);
            return;
        }
        if (MemberValueGUI.IsLeaf(type))
        {
            MemberValueGUI.DrawLeaf(label, type, value);
            return;
        }
        // Leaf check runs first on purpose: Transform implements IEnumerable, and it is an object reference.
        if (value is IEnumerable enumerable && !(value is string))
        {
            DrawCollection(label, enumerable, key, depth);
            return;
        }
        DrawNested(label, type, value, key, depth);
    }

    /// <summary>Foldout with the item count, then one recursive row per element.</summary>
    private void DrawCollection(string label, IEnumerable source, string key, int depth)
    {
        var items = new List<object>();
        bool truncated = false;
        try
        {
            foreach (object item in source)
            {
                if (items.Count >= MaxEnumerated)
                {
                    truncated = true;
                    break;
                }
                items.Add(item);
            }
        }
        catch (Exception exception)
        {
            // Enumerating is running someone's code; a collection mutated meanwhile throws right here.
            MemberValueGUI.DrawNote(label, $"⚠ {exception.GetType().Name}: {exception.Message}");
            return;
        }

        string header = $"{label}  [{items.Count}{(truncated ? "+" : string.Empty)}]";
        if (!Foldout(key, header, items.Count > 0)) return;

        if (depth >= maxDepth)
        {
            using (new EditorGUI.IndentLevelScope()) MemberValueGUI.DrawNote("…", "depth limit reached");
            return;
        }

        using (new EditorGUI.IndentLevelScope())
        {
            for (int i = 0; i < items.Count; i++)
            {
                DrawValue($"[{i}]", items[i] != null ? items[i].GetType() : typeof(object), items[i],
                    $"{key}[{i}]", depth + 1);
            }
            if (truncated) MemberValueGUI.DrawNote("…", $"stopped after {MaxEnumerated} items");
        }
    }

    /// <summary>Foldout that expands a plain class / struct into its own members.</summary>
    private void DrawNested(string label, Type type, object value, string key, int depth)
    {
        bool isReference = !type.IsValueType;
        if (isReference && path.Contains(value))
        {
            MemberValueGUI.DrawNote(label, $"({type.Name}) cycle — already on this path");
            return;
        }

        if (!Foldout(key, $"{label}  ({type.Name})", false)) return;

        if (depth >= maxDepth)
        {
            using (new EditorGUI.IndentLevelScope()) MemberValueGUI.DrawNote("…", "depth limit reached");
            return;
        }

        if (isReference) path.Add(value);
        try
        {
            using (new EditorGUI.IndentLevelScope())
            {
                MemberValueEntry[] members = MemberValueScan.Of(type);
                bool drewAny = false;
                foreach (MemberValueEntry member in members)
                {
                    if (!Passes(member, ignoreSearch: true)) continue;
                    DrawMemberRow(value, member, $"{key}.{member.name}", depth + 1);
                    drewAny = true;
                }
                if (!drewAny) MemberValueGUI.DrawNote("…", "no members pass the current filters");
            }
        }
        finally
        {
            // Popped on the way out, so this is a path and not a global visited set.
            if (isReference) path.Remove(value);
        }
    }

    private bool Foldout(string key, string header, bool defaultOpen)
    {
        if (!foldouts.TryGetValue(key, out bool open)) open = defaultOpen;
        open = EditorGUILayout.Foldout(open, header, true);
        foldouts[key] = open;
        return open;
    }
}
