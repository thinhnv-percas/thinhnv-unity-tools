using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Shows the live value of every field, property and zero-argument method of the selected asset or
/// GameObject — no attribute needed anywhere in your code. Selecting a GameObject lists the object itself
/// and each of its components, which is where non-serialized runtime state actually lives.
/// <para>
/// Reading a member is not always free, so the kinds are treated differently rather than uniformly:
/// fields are read every repaint (reading a field cannot have side effects), properties are read by
/// default but can be switched to click-to-evaluate, and methods are never invoked until you click. That
/// last one matters: <c>Queue&lt;T&gt;.Dequeue</c> and <c>IEnumerator.MoveNext</c> are both
/// zero-argument methods returning a value, and auto-invoking them every repaint would quietly corrupt
/// runtime state. Unity-declared members are hidden by default for the same reason — reading
/// <c>Renderer.material</c> instantiates a material.
/// </para>
/// <para>
/// Methods that return nothing or take arguments cannot be browsed at all, only run, so they sit behind a
/// call button — with a field per parameter, reusing <see cref="ContextMenuButtonGUI.DrawField"/> so the
/// argument fields match the ContextMenu buttons. A call on a Unity object is wrapped in
/// <see cref="Undo.RecordObject"/> and marks the object dirty outside play mode, because a method is free
/// to change state that has to be saved. Overloads are not offered: they share a name, and the viewer has
/// no way to let you pick between them.
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
    [SerializeField] private bool showCallable = true;
    [SerializeField] private bool showStatic;
    [SerializeField] private bool showExternal;
    [SerializeField] private bool autoReadProperties = true;
    [SerializeField] private bool autoInvokeMethods;
    [SerializeField] private bool live = true;
    [SerializeField] private int maxDepth = 3;
    [SerializeField] private Vector2 scroll;

    /// <summary>Outcome of one deferred read or one method call, kept so the row can show it.</summary>
    private struct CallResult
    {
        public int count;
        public object value;
        public string error;
    }

    // Results of everything that had to be clicked, cleared on selection change or Refresh.
    private readonly Dictionary<string, CallResult> results = new Dictionary<string, CallResult>();

    // Argument values per method row, so typing them survives repaints.
    private readonly Dictionary<string, object[]> arguments = new Dictionary<string, object[]>();

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
    /// One member row. Fields are read outright; a property or getter obeys its auto toggle and otherwise
    /// waits behind an evaluate button; a method that takes arguments or returns nothing always waits behind
    /// a call button, and gets a field per parameter.
    /// </summary>
    private void DrawMemberRow(object owner, MemberValueEntry member, string key, int depth)
    {
        if (member.needsCall)
        {
            DrawCallRow(owner, member, key, depth);
            return;
        }

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

        results.TryGetValue(key, out CallResult result);
        bool inline = IsInline(member, result);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("▷", EditorStyles.miniButton, GUILayout.Width(22f))) Invoke(owner, member, key, null);
            if (inline) DrawStatus(label, member, result);
            else GUILayout.Label(label, EditorStyles.label);
        }

        // A structured result cannot share the button's row — a foldout needs its own vertical space.
        if (!inline) DrawStructuredResult(member, result, key, depth);
    }

    /// <summary>A method that has to be called: parameter fields behind a foldout, then the button.</summary>
    private void DrawCallRow(object owner, MemberValueEntry member, string key, int depth)
    {
        string signature = $"{member.label} ({Signature(member)})";
        results.TryGetValue(key, out CallResult result);

        if (member.parameters.Length == 0)
        {
            bool inlineVoid = IsInline(member, result);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("▶", EditorStyles.miniButton, GUILayout.Width(22f))) Invoke(owner, member, key, null);
                if (inlineVoid) DrawStatus(signature, member, result);
                else GUILayout.Label(signature, EditorStyles.label);
            }
            if (!inlineVoid) DrawStructuredResult(member, result, key, depth);
            return;
        }

        if (!Foldout(key, signature, false)) return;

        object[] args = Arguments(key, member.parameters);
        using (new EditorGUI.IndentLevelScope())
        {
            for (int i = 0; i < member.parameters.Length; i++)
            {
                ParameterInfo parameter = member.parameters[i];
                args[i] = ContextMenuButtonGUI.DrawField(ObjectNames.NicifyVariableName(parameter.Name),
                    parameter.ParameterType, args[i], $"{key}({parameter.Name}");
            }
            if (GUILayout.Button($"▶  Call {member.label}")) Invoke(owner, member, key, args);

            if (IsInline(member, result)) DrawStatus("Result", member, result);
            else DrawStructuredResult(member, result, key, depth);
        }
    }

    /// <summary>True when the outcome fits on the button's row rather than needing a tree below it.</summary>
    private static bool IsInline(MemberValueEntry member, CallResult result)
    {
        if (result.count == 0 || result.error != null || !member.returnsValue) return true;
        return result.value == null || MemberValueGUI.IsLeaf(result.value.GetType());
    }

    private static void DrawStatus(string label, MemberValueEntry member, CallResult result)
    {
        if (result.error != null) MemberValueGUI.DrawNote(label, $"⚠ {result.error}");
        else if (result.count == 0) MemberValueGUI.DrawNote(label, member.needsCall ? "not called" : "not read");
        else if (!member.returnsValue) MemberValueGUI.DrawNote(label, $"called ×{result.count}");
        else MemberValueGUI.DrawLeaf(label, member.valueType, result.value);
    }

    private void DrawStructuredResult(MemberValueEntry member, CallResult result, string key, int depth)
    {
        path.Clear();
        using (new EditorGUI.IndentLevelScope())
        {
            DrawValue($"Result ×{result.count}", member.valueType, result.value, $"{key}=", depth);
        }
    }

    private static string Signature(MemberValueEntry member)
    {
        if (member.parameters.Length == 0) return string.Empty;

        var names = new string[member.parameters.Length];
        for (int i = 0; i < member.parameters.Length; i++) names[i] = member.parameters[i].ParameterType.Name;
        return string.Join(", ", names);
    }

    /// <summary>Argument array for one method row, seeded from parameter defaults on first use.</summary>
    private object[] Arguments(string key, ParameterInfo[] parameters)
    {
        if (arguments.TryGetValue(key, out object[] args) && args.Length == parameters.Length) return args;

        args = new object[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            Type type = parameters[i].ParameterType;
            args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue
                : type.IsValueType ? Activator.CreateInstance(type) : null;
        }
        arguments[key] = args;
        return args;
    }

    /// <summary>
    /// Run the member and keep the outcome. A call on a Unity object is recorded for undo and marks the
    /// object dirty outside play mode, since a method is free to change state that has to be saved.
    /// </summary>
    private void Invoke(object owner, MemberValueEntry member, string key, object[] args)
    {
        var unityOwner = owner as UnityEngine.Object;
        if (unityOwner != null && member.kind == MemberValueKind.Method)
        {
            Undo.RecordObject(unityOwner, $"Call {member.name}");
        }

        results.TryGetValue(key, out CallResult previous);
        CallResult result = new CallResult { count = previous.count + 1 };

        if (member.TryInvoke(owner, args, out object value, out string error)) result.value = value;
        else result.error = error;
        results[key] = result;

        if (unityOwner != null && member.kind == MemberValueKind.Method && result.error == null &&
            !Application.isPlaying)
        {
            EditorUtility.SetDirty(unityOwner);
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
        // Readable getters and callable methods get their own toggle: browsing values and firing off
        // side effects are different jobs, and a type can have plenty of the latter.
        if (member.kind == MemberValueKind.Method && !(member.needsCall ? showCallable : showMethods)) return false;

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
            showMethods = GUILayout.Toggle(showMethods, "Getters", EditorStyles.toolbarButton, GUILayout.Width(56f));
            showCallable = GUILayout.Toggle(showCallable, "Void/Args", EditorStyles.toolbarButton, GUILayout.Width(68f));
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
        results.Clear();
        // Arguments are deliberately kept: they belong to the method, not to the run you just did.
    }
}
