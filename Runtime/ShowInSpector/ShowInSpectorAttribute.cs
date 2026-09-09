using System;

/// <summary>
/// Marks a member whose live value should be drawn (read-only) in the inspector, under the
/// "Show In Spector" section added by <c>ShowInSpectorGUI</c>: non-serialized fields, readable
/// properties and zero-parameter methods that return a value.
/// <para>
/// <see cref="UnityEngine.TooltipAttribute"/> with the text <c>"ShowInSpector"</c> is honoured as
/// an equivalent marker, but Unity declares that attribute as <see cref="AttributeTargets.Field"/>
/// only — so properties and methods must use this attribute instead.
/// </para>
/// <example>
/// <code>
/// [ShowInSpector] private int hitCount;                       // non-serialized field
/// [ShowInSpector] public bool IsGrounded => grounded;         // property
/// [ShowInSpector("Distance To Player")] float Distance() ...  // method + custom label
/// [Tooltip("ShowInSpector")] private float cachedSpeed;       // Tooltip marker (fields only)
/// </code>
/// </example>
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method,
    Inherited = true, AllowMultiple = false)]
public sealed class ShowInSpectorAttribute : Attribute
{
    /// <summary>Header shown instead of the nicified member name; null keeps the member name.</summary>
    public readonly string label;

    public ShowInSpectorAttribute(string label = null)
    {
        this.label = label;
    }
}
