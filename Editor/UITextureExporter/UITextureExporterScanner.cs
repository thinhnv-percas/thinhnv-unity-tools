using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public static class UITextureExporterScanner
{
    public sealed class CanvasRendererEntry
    {
        public GameObject gameObject;
        public CanvasRenderer canvasRenderer;
        public Graphic graphic;
        public RectTransform rectTransform;
        public int depth;
        public int materialCount;
        public Material material;
        public bool culled;
        public float alpha;
        public float inheritedAlpha;
        public Mesh mesh;
        public bool selected = true;

        public string Name => gameObject != null ? gameObject.name : "<Missing>";

        public bool IsTextMeshPro
        {
            get
            {
                if (graphic == null)
                    return false;

                string typeName = graphic.GetType().FullName;
                return !string.IsNullOrEmpty(typeName) &&
                       typeName.StartsWith("TMPro.");
            }
        }

        public int InstanceId =>
            canvasRenderer != null
                ? canvasRenderer.GetInstanceID()
                : 0;
    }

    public sealed class ScanResult
    {
        public GameObject root;
        public Canvas rootCanvas;
        public List<CanvasRendererEntry> renderers = new List<CanvasRendererEntry>();
    }

    public static ScanResult Scan(GameObject root, bool includeInactive)
    {
        if (root == null)
            return null;

        var result = new ScanResult
        {
            root = root,
            rootCanvas = root.GetComponentInParent<Canvas>()?.rootCanvas
        };

        CanvasRenderer[] renderers =
            root.GetComponentsInChildren<CanvasRenderer>(includeInactive);

        for (int i = 0; i < renderers.Length; i++)
        {
            CanvasRenderer renderer = renderers[i];
            if (renderer == null)
                continue;

            GameObject go = renderer.gameObject;

            if (!includeInactive && !go.activeInHierarchy)
                continue;

            RectTransform rect = go.transform as RectTransform;
            if (rect == null)
                continue;

            Graphic graphic = go.GetComponent<Graphic>();

            Mesh mesh = null;
            try
            {
                mesh = renderer.GetMesh();
            }
            catch
            {
                // Some renderers may not have generated geometry yet.
            }

            Material material = null;
            try
            {
                material = renderer.GetMaterial(0);
            }
            catch
            {
                if (graphic != null)
                {
                    try
                    {
                        material = graphic.materialForRendering;
                    }
                    catch { }
                }
            }

            var entry = new CanvasRendererEntry
            {
                gameObject = go,
                canvasRenderer = renderer,
                graphic = graphic,
                rectTransform = rect,
                depth = renderer.absoluteDepth,
                materialCount = renderer.materialCount,
                material = material,
                culled = renderer.cull,
                alpha = renderer.GetAlpha(),
                inheritedAlpha = renderer.GetInheritedAlpha(),
                mesh = mesh,
                selected = true
            };

            result.renderers.Add(entry);
        }

        result.renderers.Sort((a, b) =>
        {
            int depthCompare = a.depth.CompareTo(b.depth);
            if (depthCompare != 0)
                return depthCompare;

            return GetHierarchyPath(a.gameObject)
                .CompareTo(GetHierarchyPath(b.gameObject));
        });

        return result;
    }

    private static string GetHierarchyPath(GameObject go)
    {
        if (go == null)
            return string.Empty;

        string result = go.transform.GetSiblingIndex().ToString("D6");

        Transform current = go.transform.parent;
        while (current != null)
        {
            result = current.GetSiblingIndex().ToString("D6") + "/" + result;
            current = current.parent;
        }

        return result;
    }
}
