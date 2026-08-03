using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(BoxCollider))]
public class BoxColliderEditor : Editor
{
    public override void OnInspectorGUI()
    {
        base.OnInspectorGUI();

        EditorGUI.BeginChangeCheck();
        EditorGUILayout.BeginVertical();
        EditorGUILayout.Space();

        Rect rect = EditorGUILayout.GetControlRect(false, 10);
        rect.height = 1;
        EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 1));

        if (GUILayout.Button("Fill Box"))
        {
            BoxCollider boxCollider = (BoxCollider)target;
            FitBox(boxCollider.gameObject);
        }

        EditorGUILayout.EndVertical();
    }

    [MenuItem("CONTEXT/BoxCollider/Fill Box")]
    private static void FitBoxContextMenu(MenuCommand menuCommand)
    {
        BoxCollider boxCollider = menuCommand.context as BoxCollider;
        FitBox(boxCollider.gameObject);
    }

    public static void FitBox(GameObject gameObject)
    {
        //get default index
        var prentDefault = gameObject.transform.parent;
        var order = gameObject.transform.GetSiblingIndex();
        var localPositionDefault = gameObject.transform.localPosition;
        var localEulerAnglesDefault = gameObject.transform.localEulerAngles;
        var localScaleDefault = gameObject.transform.localScale;

        //get index before create box
        gameObject.transform.parent = null;
        gameObject.transform.position = Vector3.zero;
        gameObject.transform.eulerAngles = Vector3.zero;
        gameObject.transform.localScale = Vector3.one;

        CalculateSize(gameObject);

        //set index to default
        gameObject.transform.parent = prentDefault;
        gameObject.transform.SetSiblingIndex(order);
        gameObject.transform.localPosition = localPositionDefault;
        gameObject.transform.localEulerAngles = localEulerAnglesDefault;
        gameObject.transform.localScale = localScaleDefault;
    }

    public static Bounds CalculateSize(GameObject gameObject, bool fixMat = true)
    {
        Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);

        var renderers = gameObject.GetComponentsInChildren<Renderer>(false);
        var firstRenderer = false;

        //Debug.LogFormat("Create box with renderers.count = {0}", renderers.Length);
        if (renderers.Length > 0)
        {
            foreach (var renderer in renderers)
            {
                if (renderer.enabled)
                {
                    if (firstRenderer)
                    {
                        bounds.Encapsulate(renderer.bounds);
                    }
                    else
                    {
                        bounds = renderer.bounds;
                        firstRenderer = true;
                    }
                }
            }
        }
        else
        {
            var boxs = gameObject.GetComponentsInChildren<BoxCollider>(false);
            foreach (var box in boxs)
            {
                if (firstRenderer)
                {
                    bounds.Encapsulate(box.bounds);
                }
                else
                {
                    bounds = box.bounds;
                    firstRenderer = true;
                }
            }
        }

        var collider = gameObject.GetComponent<BoxCollider>();
        if (collider == null)
        {
            collider = gameObject.AddComponent<BoxCollider>();
        }
        collider.center = bounds.center + gameObject.transform.position;
        collider.size = bounds.size;

        if (collider.size == Vector3.zero)
        {
            collider.size = new Vector2(1, 1);
        }

        return bounds;
    }
}
