using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Shows the live value of every field, property and zero-argument method of the selected asset or
/// GameObject — no attribute needed anywhere in your code. Selecting a GameObject lists the object itself
/// and each of its components, which is where non-serialized runtime state actually lives.
/// <para>
/// Reading a member is not always free, so the three kinds are treated differently rather than uniformly:
/// fields are read every repaint (reading a field cannot have side effects), properties are read by
/// default but can be switched to click-to-evaluate, and methods are never invoked until you click. That
/// last one matters: <c>Queue&lt;T&gt;.Dequeue</c> and <c>IEnumerator.MoveNext</c> are both
/// zero-argument methods returning a value, and auto-invoking them every repaint would quietly corrupt
/// runtime state. Unity-declared members are hidden by default for the same reason — reading
/// <c>Renderer.material</c> instantiates a material.
/// </para>
/// </summary>
public partial class MemberValueViewerWindow : EditorWindow
{
    private static readonly string[] DepthLabels = { "1", "2", "3", "4", "5", "6" };
    private static readonly int[] DepthValues = { 1, 2, 3, 4, 5, 6 };

    [SerializeField] private bool locked;
    [SerializeField] private UnityEngine.Object lockedTarget;
    [SerializeField] private string search = string.Empty;
    [SerializeField] private bool showFields = true;
    [SerializeField] private bool showProperties = true;
    [SerializeField] private bool showMethods = true;
    [SerializeField] private bool showStatic;
    [SerializeField] private bool showExternal;
    [SerializeField] private bool autoReadProperties = true;
    [SerializeField] private bool autoInvokeMethods;
    [SerializeField] private bool live = true;
    [SerializeField] private int maxDepth = 3;
    [SerializeField] private Vector2 scroll;

    // Results of click-to-evaluate members, kept until the selection changes or Refresh is pressed.
    private readonly Dictionary<string, object> evaluated = new Dictionary<string, object>();
    private readonly Dictionary<string, string> evaluatedErrors = new Dictionary<string, string>();

    private double lastRepaint;

    [MenuItem("Tools/Thinhnv/Member Value Viewer")]
    private static void Open()
    {
        MemberValueViewerWindow window = GetWindow<MemberValueViewerWindow>("Member Values");
        window.minSize = new Vector2(340f, 220f);
    }

    private void OnSelectionChange()
    {
        ClearEvaluated();
        Repaint();
    }

    private void Update()
    {
        // Values are only live if we repaint; throttled so an idle window is not a spinning inspector.
        if (!live) return;
        double now = EditorApplication.timeSinceStartup;
        if (now - lastRepaint < 0.1) return;
        lastRepaint = now;
        Repaint();
    }

    private void OnGUI()
    {
        DrawToolbar();

        UnityEngine.Object target = locked ? lockedTarget : Selection.activeObject;
        if (target == null)
        {
            EditorGUILayout.HelpBox("Select an asset or a GameObject to inspect.", MessageType.Info);
            return;
        }

        scroll = EditorGUILayout.BeginScrollView(scroll);
        foreach (UnityEngine.Object owner in Expand(target))
        {
            DrawTarget(owner);
        }
        EditorGUILayout.EndScrollView();
    }

    /// <summary>The selection, plus its components when it is a GameObject.</summary>
    private static IEnumerable<UnityEngine.Object> Expand(UnityEngine.Object selected)
    {
        yield return selected;

        if (!(selected is GameObject go)) yield break;
        foreach (Component component in go.GetComponents<Component>())
        {
            // Null for a component whose script is missing.
            if (component != null) yield return component;
        }
    }

    private void DrawTarget(UnityEngine.Object owner)
    {
        Type type = owner.GetType();
        string header = owner is Component ? type.Name : $"{owner.name}  ({type.Name})";
        string key = $"target:{owner.GetInstanceID()}";

        GUILayout.Space(4f);
        Rect rule = EditorGUILayout.GetControlRect(false, 6f);
        rule.height = 2f;
        EditorGUI.DrawRect(rule, new Color32(232, 143, 43, 255));

        if (!Foldout(key, header, true)) return;

        MemberValueEntry[] members = MemberValueScan.Of(type);
        int shown = 0;
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            foreach (MemberValueEntry member in members)
            {
                if (!Passes(member, ignoreSearch: false)) continue;
                DrawMemberRow(owner, member, $"{key}/{member.name}", 0);
                shown++;
            }
            if (shown == 0) MemberValueGUI.DrawNote("…", $"no members match ({members.Length} scanned)");
        }
    }

    /// <summary>
    /// One member row. Fields are read outright; properties and methods obey their auto toggles and
    /// otherwise wait behind an evaluate button.
    /// </summary>
    private void DrawMemberRow(object owner, MemberValueEntry member, string key, int depth)
    {
        string label = member.kind == MemberValueKind.Method ? member.label + " ()" : member.label;

        bool auto = member.kind == MemberValueKind.Field ||
                    (member.kind == MemberValueKind.Property && autoReadProperties) ||
                    (member.kind == MemberValueKind.Method && autoInvokeMethods);

        if (auto)
        {
            path.Clear();
            if (member.TryRead(owner, out object value, out string error)) DrawValue(label, member.valueType, value, key, depth);
            else MemberValueGUI.DrawNote(label, $"⚠ {error}");
            return;
        }

        bool hasValue = evaluated.TryGetValue(key, out object cached);
        evaluatedErrors.TryGetValue(key, out string cachedError);
        bool inline = !hasValue || cached == null || MemberValueGUI.IsLeaf(cached.GetType());

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("▷", EditorStyles.miniButton, GUILayout.Width(22f))) Evaluate(owner, member, key);

            if (!hasValue && cachedError != null) MemberValueGUI.DrawNote(label, $"⚠ {cachedError}");
            else if (!hasValue) MemberValueGUI.DrawNote(label, member.kind == MemberValueKind.Method ? "not invoked" : "not read");
            else if (inline) MemberValueGUI.DrawLeaf(label, member.valueType, cached);
            else GUILayout.Label(label, EditorStyles.label);
        }

        // A structured result cannot share the button's row — a foldout needs its own vertical space.
        if (hasValue && !inline)
        {
            path.Clear();
            using (new EditorGUI.IndentLevelScope()) DrawValue("Value", member.valueType, cached, key, depth);
        }
    }

    private void Evaluate(object owner, MemberValueEntry member, string key)
    {
        if (member.TryRead(owner, out object value, out string error))
        {
            evaluated[key] = value;
            evaluatedErrors.Remove(key);
        }
        else
        {
            evaluatedErrors[key] = error;
            evaluated.Remove(key);
        }
    }

    /// <summary>
    /// Filters a scanned member against the toolbar. <paramref name="ignoreSearch"/> is set while drilling
    /// into a nested object: the search box matched the member you opened, not the fields inside it.
    /// </summary>
    private bool Passes(MemberValueEntry member, bool ignoreSearch)
    {
        if (member.isStatic && !showStatic) return false;
        if (member.isExternal && !showExternal) return false;

        if (member.kind == MemberValueKind.Field && !showFields) return false;
        if (member.kind == MemberValueKind.Property && !showProperties) return false;
        if (member.kind == MemberValueKind.Method && !showMethods) return false;

        if (!ignoreSearch && !string.IsNullOrEmpty(search) &&
            member.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }
        return true;
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            bool wasLocked = locked;
            locked = GUILayout.Toggle(locked, "Lock", EditorStyles.toolbarButton, GUILayout.Width(46f));
            // Capture the target at the moment of locking, so it survives later selection changes.
            if (locked && !wasLocked) lockedTarget = Selection.activeObject;

            UnityEngine.Object target = locked ? lockedTarget : Selection.activeObject;
            GUILayout.Label(target != null ? target.name : "nothing selected", EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();
            live = GUILayout.Toggle(live, "Live", EditorStyles.toolbarButton, GUILayout.Width(42f));
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(58f))) ClearEvaluated();
        }

        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            search = EditorGUILayout.TextField(search, EditorStyles.toolbarSearchField);
            showFields = GUILayout.Toggle(showFields, "Fields", EditorStyles.toolbarButton, GUILayout.Width(50f));
            showProperties = GUILayout.Toggle(showProperties, "Props", EditorStyles.toolbarButton, GUILayout.Width(46f));
            showMethods = GUILayout.Toggle(showMethods, "Methods", EditorStyles.toolbarButton, GUILayout.Width(58f));
        }

        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            showStatic = GUILayout.Toggle(showStatic, "Static", EditorStyles.toolbarButton, GUILayout.Width(48f));
            showExternal = GUILayout.Toggle(showExternal, "Unity", EditorStyles.toolbarButton, GUILayout.Width(46f));
            autoReadProperties = GUILayout.Toggle(autoReadProperties, "Auto-read props", EditorStyles.toolbarButton, GUILayout.Width(100f));
            autoInvokeMethods = GUILayout.Toggle(autoInvokeMethods, "Auto-invoke", EditorStyles.toolbarButton, GUILayout.Width(80f));

            GUILayout.FlexibleSpace();
            GUILayout.Label("Depth", EditorStyles.miniLabel);
            maxDepth = EditorGUILayout.IntPopup(maxDepth, DepthLabels, DepthValues, EditorStyles.toolbarPopup, GUILayout.Width(40f));
        }
    }

    private void ClearEvaluated()
    {
        evaluated.Clear();
        evaluatedErrors.Clear();
    }
}
