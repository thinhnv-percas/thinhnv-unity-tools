using UnityEditor;
using UnityEngine;

namespace ThinhnvTools
{
    public class CircularArrangeTool : EditorWindow
    {
        private float radius = 5f;
        private float startAngle = 0f;
        private float endAngle = 180f;
        private Vector3 center = Vector3.zero;

        private Plane plane = Plane.XZ;

        enum Plane
        {
            XY,
            XZ,
            YZ
        }

        [MenuItem("Tools/Thinhnv/Circular Arrange")]
        static void Open()
        {
            GetWindow<CircularArrangeTool>("Circular Arrange");
        }

        private void OnGUI()
        {
            GUILayout.Label("Arrange Selected Objects", EditorStyles.boldLabel);

            radius = EditorGUILayout.FloatField("Radius", radius);
            startAngle = EditorGUILayout.FloatField("Start Angle", startAngle);
            endAngle = EditorGUILayout.FloatField("End Angle", endAngle);

            center = EditorGUILayout.Vector3Field("Center", center);

            plane = (Plane)EditorGUILayout.EnumPopup("Plane", plane);

            GUILayout.Space(10);

            if (GUILayout.Button("Arrange"))
            {
                Arrange();
            }
        }

        void Arrange()
        {
            var objects = Selection.transforms;

            if (objects.Length == 0)
            {
                EditorUtility.DisplayDialog("Warning", "No objects selected.", "OK");
                return;
            }

            Undo.RecordObjects(objects, "Circular Arrange");

            bool fullCircle = Mathf.Abs(endAngle - startAngle) >= 360f;

            float step = 0;

            if (objects.Length > 1)
            {
                step = fullCircle
                    ? (endAngle - startAngle) / objects.Length
                    : (endAngle - startAngle) / (objects.Length - 1);
            }

            for (int i = 0; i < objects.Length; i++)
            {
                float angle = startAngle + step * i;
                float rad = angle * Mathf.Deg2Rad;

                Vector3 pos = center;

                switch (plane)
                {
                    case Plane.XY:
                        pos += new Vector3(
                            Mathf.Cos(rad),
                            Mathf.Sin(rad),
                            0) * radius;
                        break;

                    case Plane.XZ:
                        pos += new Vector3(
                            Mathf.Cos(rad),
                            0,
                            Mathf.Sin(rad)) * radius;
                        break;

                    case Plane.YZ:
                        pos += new Vector3(
                            0,
                            Mathf.Cos(rad),
                            Mathf.Sin(rad)) * radius;
                        break;
                }

                objects[i].position = pos;
            }
        }
    }
}