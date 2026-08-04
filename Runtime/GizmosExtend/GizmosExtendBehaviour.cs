using System.Collections.Generic;
using UnityEngine;

namespace Kit
{
    public class GizmosExtendBehaviour : MonoBehaviour
    {
        private static GizmosExtendBehaviour _instance;

        public static GizmosExtendBehaviour instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("GizmosExtendBehaviour", typeof(GizmosExtendBehaviour));
                    _instance = go.GetComponent<GizmosExtendBehaviour>();
                }

                return _instance;
            }
        }

        public delegate void DragCallback();

        public Dictionary<int, DragCallback> draw { get; set; }

        private void Awake()
        {
            draw = new Dictionary<int, DragCallback>();
            DontDestroyOnLoad(gameObject);
        }

        private void OnDrawGizmos()
        {
            if (draw == null)
            {
                return;
            }

            foreach (var element in new List<KeyValuePair<int, DragCallback>>(draw))
            {
                element.Value?.Invoke();
            }
        }

        private void SetDraw(int id, DragCallback callback)
        {
            if (draw == null)
            {
                draw = new Dictionary<int, DragCallback>();
            }

            if (draw.ContainsKey(id))
            {
                draw[id] = callback;
            }
            else
            {
                draw.Add(id, callback);
            }
        }

        public void RemoveDraw(int id)
        {
            if (draw != null && draw.ContainsKey(id))
            {
                draw.Remove(id);
            }
        }

        public void DrawSquare(int id, Vector3 center, Vector3 direction, Vector2 size, float angle, Color color)
        {
            SetDraw(id, () => GizmosExtend.DrawSquare(center, direction, size, angle, color));
        }

        public void DrawLabel(int id, Vector3 position, string text, GUIStyle style = null, Color color = default(Color), float offsetX = 0f, float offsetY = 0f)
        {
            SetDraw(id, () => GizmosExtend.DrawLabel(position, text, style, color, offsetX, offsetY));
        }

        public void DrawLable(int id, Vector3 position, string text, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawLabel(position, text, color: color));
        }

        public void DrawPoint(int id, Vector3 position, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawPoint(position, color: color));
        }

        public void DrawRay(int id, Vector3 position, Vector3 direction, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawRay(position, direction, color));
        }

        public void DrawLine(int id, Vector3 from, Vector3 to, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawLine(from, to, color));
        }

        public void DrawBounds(int id, Bounds bounds, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawBounds(bounds, color));
        }

        public void DrawCircle(int id, Vector3 position, Vector3 up, Color color, float radius = 1f)
        {
            SetDraw(id, () => GizmosExtend.DrawCircle(position, up, color, radius));
        }

        public void DrawCylinder(int id, Vector3 start, Vector3 end, Color color = default(Color), float radius = 1f)
        {
            SetDraw(id, () => GizmosExtend.DrawCylinder(start, end, color, radius));
        }

        public void DrawCone(int id, Vector3 position, Vector3 direction, Color color = default(Color), float angle = 45f)
        {
            SetDraw(id, () => GizmosExtend.DrawCone(position, direction, color, angle));
        }

        public void DrawArrow(int id, Vector3 position, Vector3 direction, Color color = default(Color), float angle = 15f, float headLength = 0.3f)
        {
            SetDraw(id, () => GizmosExtend.DrawArrow(position, direction, color, angle, headLength));
        }

        public void DrawCapsule(int id, Vector3 point1, Vector3 point2, float radius = 1f, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawCapsule(point1, point2, radius, color));
        }

        public void DrawFrustum(int id, Camera camera, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawFrustum(camera, color));
        }

        public void DrawPlane(int id, Vector3 start, Vector3 end, Vector3 upward, float height = 1f, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawPlane(start, end, upward, height, color));
        }

        public void DrawPlane(int id, Transform self, float width, float height = 1f, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawPlane(self, width, height, color));
        }

        public void DrawSphere(int id, Transform self, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawSphere(self, color));
        }

        public void DrawSphere(int id, Vector3 position, float radius, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawSphere(position, radius, color));
        }

        public void DrawDirection(int id, Transform self, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawDirection(self, color));
        }

        public void DrawDirection(int id, Vector3 position, Vector3 direction, float distance = 1f, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawDirection(position, direction, distance, color));
        }

        public void DrawArc(int id, Vector3 center, Vector3 normal, Vector3 from, float angle, float radius, Color color, bool constantScreenSize = true)
        {
            SetDraw(id, () => GizmosExtend.DrawArc(center, normal, from, angle, radius, color, constantScreenSize));
        }

        public void DrawAngleBetween(int id, Vector3 center, Vector3 from, Vector3 to, Vector3 axis, float radius, Color color, bool constantScreenSize = true, bool label = false)
        {
            SetDraw(id, () => GizmosExtend.DrawAngleBetween(center, from, to, axis, radius, color, constantScreenSize, label));
        }

        public void DrawBox(int id, GizmosExtend.Box box, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawBox(box, color));
        }

        public void DrawBox(int id, Vector3 origin, Vector3 halfExtents, Quaternion orientation, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawBox(origin, halfExtents, orientation, color));
        }

        public void DrawBoxCastOnHit(int id, Vector3 origin, Vector3 halfExtents, Quaternion orientation, Vector3 direction, float hitInfoDistance, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawBoxCastOnHit(origin, halfExtents, orientation, direction, hitInfoDistance, color));
        }

        public void DrawBoxCastBox(int id, Vector3 origin, Vector3 halfExtents, Quaternion orientation, Vector3 direction, float distance, Color color = default(Color))
        {
            SetDraw(id, () => GizmosExtend.DrawBoxCastBox(origin, halfExtents, orientation, direction, distance, color));
        }

        [ContextMenu("CleanDraw")]
        private void CleanDraw()
        {
            if (draw != null)
            {
                draw.Clear();
            }
        }
    }
}