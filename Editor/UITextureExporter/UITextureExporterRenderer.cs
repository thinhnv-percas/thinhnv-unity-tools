using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

using Object = UnityEngine.Object;

public static class UITextureExporterRenderer
{
    private const float BoundsPadding = 2f;
    private const float ViewDistance = 100f;

    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

    public static Texture2D Render(
        GameObject source,
        ISet<int> selectedRendererIds,
        int scale,
        int maxSize,
        bool transparentBackground)
    {
        if (source == null)
            return null;

        Canvas sourceCanvas =
            source.GetComponentInParent<Canvas>()?.rootCanvas;

        if (sourceCanvas == null)
        {
            Debug.LogError(
                "UI Texture Exporter: Source is not under a Canvas.");

            return null;
        }

        // Rebuild uGUI geometry/material state first. We intentionally render
        // the existing CanvasRenderer data directly; no clone and no Camera.Render().
        PrepareSource(source);

        UITextureExporterScanner.ScanResult scan =
            UITextureExporterScanner.Scan(source, true);

        if (scan == null || scan.renderers.Count == 0)
        {
            Debug.LogWarning(
                $"UI Texture Exporter: No CanvasRenderer found under '{source.name}'.");

            return null;
        }

        Matrix4x4 canvasWorldToLocal =
            sourceCanvas.transform.worldToLocalMatrix;

        Bounds canvasBounds;

        if (!TryCalculateCanvasSpaceBounds(
                scan.renderers,
                selectedRendererIds,
                canvasWorldToLocal,
                out canvasBounds))
        {
            Debug.LogError(
                "UI Texture Exporter: CanvasRenderers have no visible geometry. " +
                "Check CanvasRenderer.cull, alpha, Graphic.enabled and CanvasGroup alpha.");

            return null;
        }

        canvasBounds.Expand(BoundsPadding * 2f);

        int width = Mathf.Max(
            1,
            Mathf.CeilToInt(canvasBounds.size.x * scale));

        int height = Mathf.Max(
            1,
            Mathf.CeilToInt(canvasBounds.size.y * scale));

        if (width > maxSize || height > maxSize)
        {
            float factor = Mathf.Min(
                (float)maxSize / width,
                (float)maxSize / height);

            width = Mathf.Max(
                1,
                Mathf.RoundToInt(width * factor));

            height = Mathf.Max(
                1,
                Mathf.RoundToInt(height * factor));
        }

        RenderTexture previousActive = RenderTexture.active;
        RenderTexture renderTexture = null;
        var temporaryMeshes = new List<Mesh>();
        var temporaryMaterials = new List<Material>();
        CommandBuffer commandBuffer = null;

        try
        {
            renderTexture = new RenderTexture(
                width,
                height,
                24,
                RenderTextureFormat.ARGB32);

            renderTexture.name =
                "__UITextureExporter_V3_DirectRT";

            renderTexture.hideFlags =
                HideFlags.HideAndDontSave;

            renderTexture.filterMode =
                FilterMode.Bilinear;

            renderTexture.wrapMode =
                TextureWrapMode.Clamp;

            renderTexture.Create();

            Matrix4x4 projection =
                Matrix4x4.Ortho(
                    canvasBounds.min.x,
                    canvasBounds.max.x,
                    canvasBounds.min.y,
                    canvasBounds.max.y,
                    0.01f,
                    ViewDistance * 2f);

            // CommandBuffer camera-space convention uses -Z as forward.
            // The source is drawn in root-canvas local space, so no Camera object
            // is needed at all.
            Matrix4x4 view =
                Matrix4x4.Scale(
                    new Vector3(1f, 1f, -1f)) *
                Matrix4x4.Translate(
                    new Vector3(0f, 0f, ViewDistance));

            commandBuffer = new CommandBuffer
            {
                name = "UI Texture Exporter - Direct CanvasRenderer Mesh"
            };

            commandBuffer.SetRenderTarget(renderTexture);
            commandBuffer.SetViewport(
                new Rect(
                    0,
                    0,
                    width,
                    height));

            commandBuffer.ClearRenderTarget(
                true,
                true,
                transparentBackground
                    ? Color.clear
                    : Color.black);

            commandBuffer.SetViewProjectionMatrices(
                view,
                projection);

            Matrix4x4 renderRoot =
                canvasWorldToLocal;

            for (int i = 0; i < scan.renderers.Count; i++)
            {
                UITextureExporterScanner.CanvasRendererEntry entry =
                    scan.renderers[i];

                if (!CanRender(entry, selectedRendererIds))
                    continue;

                CanvasRenderer canvasRenderer =
                    entry.canvasRenderer;

                Mesh sourceMesh = canvasRenderer.GetMesh();

                if (sourceMesh == null ||
                    sourceMesh.vertexCount == 0)
                {
                    continue;
                }

                Mesh renderMesh =
                    CreateRenderableMesh(
                        sourceMesh,
                        canvasRenderer);

                if (renderMesh == null)
                    continue;

                temporaryMeshes.Add(renderMesh);

                Matrix4x4 localToCanvas =
                    renderRoot *
                    canvasRenderer.transform.localToWorldMatrix;

                int materialCount =
                    Mathf.Max(1, canvasRenderer.materialCount);

                Graphic graphic = entry.graphic;

                int drawCount =
                    Mathf.Min(
                        materialCount,
                        Mathf.Max(1, renderMesh.subMeshCount));

                for (int materialIndex = 0;
                     materialIndex < drawCount;
                     materialIndex++)
                {
                    Material canvasMaterial = null;

                    try
                    {
                        canvasMaterial =
                            canvasRenderer.GetMaterial(materialIndex);
                    }
                    catch
                    {
                        // Some custom CanvasRenderer implementations may
                        // expose a material count without a material at an index.
                    }

                    // TMP_Text.materialForRendering is the same material path
                    // TMP uses when it calls CanvasRenderer.SetMaterial(). Use
                    // it as a fallback if a renderer implementation does not
                    // return the material through GetMaterial().
                    if (canvasMaterial == null &&
                        materialIndex == 0 &&
                        graphic != null)
                    {
                        try
                        {
                            canvasMaterial =
                                graphic.materialForRendering;
                        }
                        catch
                        {
                            // Keep the renderer silent here; null means that
                            // this sub-renderer simply cannot be drawn.
                        }
                    }

                    if (canvasMaterial == null)
                        continue;

                    Material renderMaterial =
                        CreateRenderableMaterial(
                            canvasMaterial,
                            graphic,
                            entry.IsTextMeshPro);

                    if (renderMaterial == null)
                        continue;

                    if (entry.IsTextMeshPro &&
                        materialIndex == 0)
                    {
                        ValidateTmpMaterial(
                            entry,
                            canvasMaterial,
                            renderMaterial,
                            graphic);
                    }

                    temporaryMaterials.Add(renderMaterial);

                    int subMeshIndex =
                        renderMesh.subMeshCount > materialIndex
                            ? materialIndex
                            : 0;

                    commandBuffer.DrawMesh(
                        renderMesh,
                        localToCanvas,
                        renderMaterial,
                        subMeshIndex,
                        -1,
                        null);
                }
            }

            // Built-in Render Pipeline direct command-buffer execution.
            // There is deliberately no Camera and no Camera.Render() call.
            Graphics.ExecuteCommandBuffer(commandBuffer);

            RenderTexture.active = renderTexture;

            Texture2D texture = new Texture2D(
                width,
                height,
                TextureFormat.RGBA32,
                false,
                false);

            texture.name =
                source.name + "_Export";

            texture.ReadPixels(
                new Rect(
                    0,
                    0,
                    width,
                    height),
                0,
                0,
                false);

            texture.Apply(false, false);

            return texture;
        }
        finally
        {
            RenderTexture.active = previousActive;

            if (commandBuffer != null)
                commandBuffer.Release();

            for (int i = 0;
                 i < temporaryMaterials.Count;
                 i++)
            {
                if (temporaryMaterials[i] != null)
                    Object.DestroyImmediate(
                        temporaryMaterials[i]);
            }

            for (int i = 0;
                 i < temporaryMeshes.Count;
                 i++)
            {
                if (temporaryMeshes[i] != null)
                    Object.DestroyImmediate(
                        temporaryMeshes[i]);
            }

            if (renderTexture != null)
            {
                if (renderTexture.IsCreated())
                    renderTexture.Release();

                Object.DestroyImmediate(renderTexture);
            }
        }
    }

    public static void PrepareSource(
        GameObject source)
    {
        if (source == null)
            return;

        Canvas.ForceUpdateCanvases();

        Graphic[] graphics =
            source.GetComponentsInChildren<Graphic>(true);

        for (int i = 0; i < graphics.Length; i++)
        {
            Graphic graphic = graphics[i];

            if (graphic == null ||
                !graphic.enabled)
            {
                continue;
            }

            // TMP keeps its text mesh generation in TMP's own update path.
            // Force it synchronously so CanvasRenderer.GetMesh() returns the
            // current glyph geometry instead of a stale/empty mesh.
            ForceTextMeshProUpdate(graphic);
            graphic.SetAllDirty();
        }

        Canvas.ForceUpdateCanvases();

        // One more explicit TMP pass after the uGUI rebuild. This covers cases
        // where SetAllDirty() caused a material/sub-mesh change during the pass.
        for (int i = 0; i < graphics.Length; i++)
        {
            if (graphics[i] == null || !graphics[i].enabled)
                continue;

            ForceTextMeshProUpdate(graphics[i]);
        }

        Canvas.ForceUpdateCanvases();
    }

    private static readonly Dictionary<System.Type, MethodInfo> TmpForceMeshUpdateMethods =
        new Dictionary<System.Type, MethodInfo>();

    private static void ForceTextMeshProUpdate(Graphic graphic)
    {
        if (graphic == null)
            return;

        Type graphicType = graphic.GetType();
        string typeName = graphicType.FullName;

        if (string.IsNullOrEmpty(typeName) ||
            !typeName.StartsWith("TMPro."))
        {
            return;
        }

        MethodInfo forceMeshUpdate;

        if (!TmpForceMeshUpdateMethods.TryGetValue(
                graphicType,
                out forceMeshUpdate))
        {
            forceMeshUpdate = graphicType.GetMethod(
                "ForceMeshUpdate",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(bool), typeof(bool) },
                null);

            // Older TMP versions can expose only the parameterless/optional
            // signature through reflection metadata.
            if (forceMeshUpdate == null)
            {
                forceMeshUpdate = graphicType.GetMethod(
                    "ForceMeshUpdate",
                    BindingFlags.Instance | BindingFlags.Public);
            }

            TmpForceMeshUpdateMethods[graphicType] = forceMeshUpdate;
        }

        if (forceMeshUpdate == null)
            return;

        try
        {
            ParameterInfo[] parameters =
                forceMeshUpdate.GetParameters();

            if (parameters.Length == 2)
            {
                forceMeshUpdate.Invoke(
                    graphic,
                    new object[] { true, true });
            }
            else if (parameters.Length == 1)
            {
                forceMeshUpdate.Invoke(
                    graphic,
                    new object[] { true });
            }
            else
            {
                forceMeshUpdate.Invoke(
                    graphic,
                    null);
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                $"UI Texture Exporter: TMP mesh update failed for '{graphic.name}': {exception.Message}");
        }
    }

    private static bool CanRender(
        UITextureExporterScanner.CanvasRendererEntry entry,
        ISet<int> selectedRendererIds = null)
    {
        if (entry == null)
            return false;

        CanvasRenderer renderer =
            entry.canvasRenderer;

        if (renderer == null)
            return false;

        if (selectedRendererIds != null &&
            !selectedRendererIds.Contains(renderer.GetInstanceID()))
        {
            return false;
        }

        if (entry.gameObject == null ||
            !entry.gameObject.activeInHierarchy)
        {
            return false;
        }

        if (entry.graphic != null &&
            !entry.graphic.enabled)
        {
            return false;
        }

        if (renderer.cull)
            return false;

        if (renderer.GetInheritedAlpha() <= 0.0001f)
            return false;

        return true;
    }

    private static Mesh CreateRenderableMesh(
        Mesh sourceMesh,
        CanvasRenderer renderer)
    {
        if (sourceMesh == null ||
            renderer == null ||
            sourceMesh.vertexCount == 0)
        {
            return null;
        }

        var mesh = Object.Instantiate(sourceMesh);

        mesh.name =
            "__UITextureExporter_V3_RenderMesh";

        mesh.hideFlags =
            HideFlags.HideAndDontSave;

        // Graphic.OnPopulateMesh already writes Graphic.color into
        // the vertex color. CanvasRenderer.GetColor() is a second
        // renderer-level multiplier and therefore has to be applied
        // before direct Graphics/CommandBuffer rendering.
        Color rendererColor =
            renderer.GetColor();

        float rendererAlpha =
            Mathf.Max(
                renderer.GetAlpha(),
                0.000001f);

        float inheritedAlpha =
            renderer.GetInheritedAlpha();

        // GetInheritedAlpha includes the renderer's own alpha.
        // Keep only the parent/canvas-group multiplier here to avoid
        // multiplying the local renderer alpha twice.
        float parentAlpha =
            inheritedAlpha / rendererAlpha;

        parentAlpha =
            Mathf.Clamp01(parentAlpha);

        Color32[] colors =
            mesh.colors32;

        if (colors == null ||
            colors.Length != mesh.vertexCount)
        {
            colors =
                new Color32[mesh.vertexCount];

            for (int i = 0; i < colors.Length; i++)
                colors[i] = Color.white;
        }

        for (int i = 0; i < colors.Length; i++)
        {
            Color c = colors[i];

            c.r *= rendererColor.r;
            c.g *= rendererColor.g;
            c.b *= rendererColor.b;
            c.a *= rendererColor.a;
            c.a *= parentAlpha;

            colors[i] = c;
        }

        mesh.colors32 = colors;

        return mesh;
    }

    private static Material CreateRenderableMaterial(
        Material sourceMaterial,
        Graphic graphic,
        bool isTextMeshPro)
    {
        if (sourceMaterial == null)
            return null;

        var material =
            new Material(sourceMaterial);

        material.name =
            "__UITextureExporter_V3_RenderMaterial";

        material.hideFlags =
            HideFlags.HideAndDontSave;

        // Material cloning should keep TMP's shader, keywords, render queue and
        // all per-material properties such as Face / Outline / Underlay. Keep
        // these explicit because TMP materials are frequently material
        // instances rather than project assets.
        material.shaderKeywords =
            sourceMaterial.shaderKeywords;
        material.renderQueue =
            sourceMaterial.renderQueue;
        material.enableInstancing =
            sourceMaterial.enableInstancing;
        material.doubleSidedGI =
            sourceMaterial.doubleSidedGI;

        if (isTextMeshPro)
        {
            // TMP stores the font/sprite atlas on its render material. Do not
            // blindly replace it with Graphic.mainTexture: doing so can replace
            // the atlas with a white/default texture and makes SDF outline,
            // underlay and sprite rendering disappear.
            Texture atlas = null;

            if (material.HasProperty(MainTexId))
                atlas = material.GetTexture(MainTexId);

            // Some TMP/material/shader combinations expose the atlas through
            // Graphic.mainTexture while _MainTex on the cloned material is not
            // populated yet. Only use it as a fallback.
            Texture graphicTexture = graphic != null ? graphic.mainTexture : null;

            // TMP_Text.materialForRendering can be an instantiated masking
            // material. In that case its serialized _MainTex is not always the
            // authoritative atlas for the current TMP renderer/submesh. TMP's
            // own rendering path associates the render material with the
            // current font/sprite atlas, so prefer Graphic.mainTexture when it
            // exists and keep the material as the property source. This also
            // prevents a default white texture from filling the entire TMP quad.
            if (graphicTexture != null && material.HasProperty(MainTexId))
            {
                if (atlas == null ||
                    atlas.GetInstanceID() != graphicTexture.GetInstanceID())
                {
                    atlas = graphicTexture;
                }
            }

            if (atlas != null && material.HasProperty(MainTexId))
            {
                material.SetTexture(
                    MainTexId,
                    atlas);
            }

            // Keep the TMP material properties untouched. In particular:
            // _FaceColor, _FaceDilate, _OutlineColor, _OutlineWidth,
            // _OutlineSoftness, _UnderlayColor, _UnderlayDilate,
            // _UnderlaySoftness, _ScaleRatioA and _GradientScale are part of
            // the SDF shader state and must remain exactly as authored.
            return material;
        }

        // CanvasRenderer.SetTexture() is not exposed as a public getter. For
        // normal uGUI Graphics, Graphic.mainTexture is the authoritative UI
        // texture and needs to be copied into the cloned material.
        Texture graphicMainTexture =
            graphic != null
                ? graphic.mainTexture
                : null;

        if (material.HasProperty(MainTexId) &&
            graphicMainTexture != null)
        {
            material.SetTexture(
                MainTexId,
                graphicMainTexture);
        }

        return material;
    }

    private static void ValidateTmpMaterial(
        UITextureExporterScanner.CanvasRendererEntry entry,
        Material sourceMaterial,
        Material renderMaterial,
        Graphic graphic)
    {
        if (sourceMaterial == null || renderMaterial == null)
            return;

        Texture materialTexture =
            renderMaterial.HasProperty(MainTexId)
                ? renderMaterial.GetTexture(MainTexId)
                : null;

        Texture graphicTexture =
            graphic != null
                ? graphic.mainTexture
                : null;

        if (materialTexture == null)
        {
            Debug.LogWarning(
                $"UI Texture Exporter: TMP renderer '{entry.Name}' has no _MainTex after material preparation. Shader='{renderMaterial.shader?.name}'.");
        }
        else if (graphicTexture != null &&
                 materialTexture.GetInstanceID() != graphicTexture.GetInstanceID())
        {
            Debug.Log(
                $"UI Texture Exporter: TMP renderer '{entry.Name}' used its Graphic atlas '{graphicTexture.name}' instead of material texture '{materialTexture.name}'. Shader='{renderMaterial.shader?.name}', Keywords=[{string.Join(", ", renderMaterial.shaderKeywords)}]");
        }

    }

    private static bool TryCalculateCanvasSpaceBounds(
        List<UITextureExporterScanner.CanvasRendererEntry> renderers,
        ISet<int> selectedRendererIds,
        Matrix4x4 canvasWorldToLocal,
        out Bounds bounds)
    {
        bounds = new Bounds();
        bool hasBounds = false;

        for (int i = 0; i < renderers.Count; i++)
        {
            UITextureExporterScanner.CanvasRendererEntry entry =
                renderers[i];

            if (!CanRender(entry, selectedRendererIds))
                continue;

            CanvasRenderer renderer =
                entry.canvasRenderer;

            Mesh mesh = null;

            try
            {
                mesh = renderer.GetMesh();
            }
            catch
            {
                continue;
            }

            if (mesh == null ||
                mesh.vertexCount == 0)
            {
                continue;
            }

            Vector3[] corners =
            {
                mesh.bounds.min,
                new Vector3(
                    mesh.bounds.min.x,
                    mesh.bounds.min.y,
                    mesh.bounds.max.z),
                new Vector3(
                    mesh.bounds.min.x,
                    mesh.bounds.max.y,
                    mesh.bounds.min.z),
                new Vector3(
                    mesh.bounds.min.x,
                    mesh.bounds.max.y,
                    mesh.bounds.max.z),
                new Vector3(
                    mesh.bounds.max.x,
                    mesh.bounds.min.y,
                    mesh.bounds.min.z),
                new Vector3(
                    mesh.bounds.max.x,
                    mesh.bounds.min.y,
                    mesh.bounds.max.z),
                new Vector3(
                    mesh.bounds.max.x,
                    mesh.bounds.max.y,
                    mesh.bounds.min.z),
                mesh.bounds.max
            };

            Matrix4x4 localToCanvas =
                canvasWorldToLocal *
                renderer.transform.localToWorldMatrix;

            for (int c = 0; c < corners.Length; c++)
            {
                Vector3 canvasPosition =
                    localToCanvas.MultiplyPoint3x4(
                        corners[c]);

                if (!hasBounds)
                {
                    bounds =
                        new Bounds(
                            canvasPosition,
                            Vector3.zero);

                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(
                        canvasPosition);
                }
            }
        }

        return hasBounds &&
               bounds.size.x > 0.0001f &&
               bounds.size.y > 0.0001f;
    }
}
