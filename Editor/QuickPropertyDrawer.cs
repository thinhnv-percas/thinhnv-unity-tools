using UnityEngine;
using UnityEditor;

[CustomPropertyDrawer(typeof(Object), true)]
public class QuickPropertyDrawer : PropertyDrawer
{
    const float BUTTON_SIZE = 20f;
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        bool showButton = property.objectReferenceValue != null;

        float buttonSize = showButton ? BUTTON_SIZE - 2 : 0;
        Rect propertyRect = new Rect(position.x, position.y, position.width - buttonSize, position.height);
        EditorGUI.PropertyField(propertyRect, property, label);

        Rect buttonRect = new Rect(propertyRect.xMax + 4, position.y + 1, (position.width - propertyRect.width), EditorGUIUtility.singleLineHeight + 1f);
        if (showButton && GUI.Button(buttonRect, EditorGUIUtility.IconContent("_Popup"), EditorStyles.iconButton))  // remove showButton to make the button always visible
        {
            if (property.objectReferenceValue is Component component)     // Opens the entire object instead of just the component (context awareness)
                EditorUtility.OpenPropertyEditor(component.gameObject);
            else if (property.objectReferenceValue is Sprite sprite)           // Opens the texture instead of just the sprite (not useful)
                EditorUtility.OpenPropertyEditor(sprite.texture);
            else                                                          // Default to just opening it regularly
                EditorUtility.OpenPropertyEditor(property.objectReferenceValue);
        }
    }

    [MenuItem("CONTEXT/Component/Open Property", false, 12)]
    public static void OpenPropertyEditor(MenuCommand command)
    {
        Component component = command.context as Component;
        EditorUtility.OpenPropertyEditor(component.gameObject);
    }
}