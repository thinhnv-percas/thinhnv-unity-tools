# UI Texture Exporter V3

## Rendering pipeline

The exporter now renders UI directly from each `CanvasRenderer.GetMesh()` and its
`CanvasRenderer.GetMaterial(...)` instead of creating a temporary camera and
calling `Camera.Render()`.

Pipeline:

1. Force uGUI `Graphic` geometry/material rebuild. TextMeshPro components additionally
   receive a synchronous `ForceMeshUpdate(true, true)` call through reflection so their
   `CanvasRenderer.GetMesh()` contains the current glyph geometry before export.
2. Read the current mesh from each `CanvasRenderer`.
3. Read the renderer material from `CanvasRenderer.GetMaterial(...)`.
4. For normal uGUI Graphics, re-apply `Graphic.mainTexture` to `_MainTex` because
   `CanvasRenderer.SetTexture()` has no public getter. For TextMeshPro, preserve the
   `_MainTex` already present on the CanvasRenderer material so the font/sprite atlas
   is not overwritten.
5. Apply the renderer color and inherited CanvasGroup alpha to a temporary
   render mesh's vertex colors.
6. Draw the meshes into a `RenderTexture` through a `CommandBuffer`.
7. Read the final pixels into a `Texture2D`.

There is no temporary Canvas, no temporary Camera, and no `Camera.Render()` call.

## Scope / limitations

This path targets the Built-in Render Pipeline because
`CommandBuffer.SetViewProjectionMatrices` is used to establish the direct
orthographic render state.

The existing uGUI mask/clip system is more tightly coupled to CanvasRenderer's
internal render stack (`GetPopMaterial`, stencil push/pop, and rect clipping).
Basic UI, Image, RawImage, Text and custom Graphic meshes/materials are rendered
directly, but complex `Mask` / `RectMask2D` hierarchies should be validated
against the project's actual shader/material setup.


## Renderer selection

The editor window now lists every `CanvasRenderer` under the selected root with an
individual checkbox. Only checked renderers contribute geometry, bounds, and pixels to
the Preview/Export. `All` and `None` buttons provide bulk selection, and selections
are preserved across Scan/Refresh for renderers that still exist. TextMeshPro primary and
sub-mesh renderers are listed individually as `TMP`.

## TMP material / outline fix

The direct renderer preserves the exact material state used by TMP instead of replacing
its atlas texture. The exporter first reads `CanvasRenderer.GetMaterial(...)` and falls
back to `Graphic.materialForRendering` when necessary. For TMP, `_MainTex` is only filled
from `Graphic.mainTexture` when the render material does not already contain an atlas.
The cloned material also preserves shader keywords, render queue, instancing and the
TMP SDF properties used for face, outline and underlay rendering.

## TMP atlas / white-quad protection

For TextMeshPro renderers the exporter keeps all material state from
`CanvasRenderer.GetMaterial()` / `Graphic.materialForRendering`, but the current
`Graphic.mainTexture` atlas is now authoritative for `_MainTex` when available.
This protects TMP and TMP submesh renderers from a masking/material instance that
still contains Unity's default white texture. The exporter logs a diagnostic when
the material texture differs from the Graphic atlas.

The scan list also caches and displays the renderer's material. `Ping` selects and
pings that material in the Unity Editor, making it possible to isolate one TMP
renderer/material at a time.

## Preview window

Preview now uses a normal dockable `EditorWindow` again rather than a utility popup.
It includes checkerboard background, zoom, Fit and 1:1 controls.
