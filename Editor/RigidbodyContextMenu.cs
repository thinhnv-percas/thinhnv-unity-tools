// Assets/Editor/RigidbodyContextMenu.cs
using UnityEditor;
using UnityEngine;

public static class RigidbodyContextMenu
{
    [MenuItem("CONTEXT/Rigidbody/Print Physics Info")]
    private static void PrintPhysicsInfo(MenuCommand command)
    {
        var rb = (Rigidbody)command.context;

        Debug.Log(
$@"========== Rigidbody Info ==========
Name                : {rb.name}
ActiveInHierarchy   : {rb.gameObject.activeInHierarchy}
Enabled             : {rb.gameObject.activeSelf}
Is Sleeping         : {rb.IsSleeping()}
Is Kinematic        : {rb.isKinematic}
Use Gravity         : {rb.useGravity}
Detect Collisions   : {rb.detectCollisions}
Linear Velocity     : {rb.linearVelocity}
Angular Velocity    : {rb.angularVelocity}
Mass                : {rb.mass}
Constraints         : {rb.constraints}
Position            : {rb.position}
====================================",
        rb);
    }

    [MenuItem("CONTEXT/Rigidbody/Wake Up")]
    private static void WakeUp(MenuCommand command)
    {
        var rb = (Rigidbody)command.context;
        rb.WakeUp();
        Debug.Log($"WakeUp: {rb.name}", rb);
    }

    [MenuItem("CONTEXT/Rigidbody/Sleep")]
    private static void Sleep(MenuCommand command)
    {
        var rb = (Rigidbody)command.context;
        rb.Sleep();
        Debug.Log($"Sleep: {rb.name}", rb);
    }

    [MenuItem("CONTEXT/Rigidbody/Toggle Is Kinematic")]
    private static void ToggleKinematic(MenuCommand command)
    {
        var rb = (Rigidbody)command.context;

        Undo.RecordObject(rb, "Toggle Is Kinematic");
        rb.isKinematic = !rb.isKinematic;
        EditorUtility.SetDirty(rb);

        Debug.Log($"isKinematic = {rb.isKinematic}", rb);
    }

    [MenuItem("CONTEXT/Rigidbody/Toggle Detect Collisions")]
    private static void ToggleDetectCollisions(MenuCommand command)
    {
        var rb = (Rigidbody)command.context;

        Undo.RecordObject(rb, "Toggle Detect Collisions");
        rb.detectCollisions = !rb.detectCollisions;
        EditorUtility.SetDirty(rb);

        Debug.Log($"detectCollisions = {rb.detectCollisions}", rb);
    }
}