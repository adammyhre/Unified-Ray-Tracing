# Unity Unified Ray Tracing Example

![Unified Ray Tracing Example](https://github.com/user-attachments/assets/placeholder-add-image-url-here)

## Description

This example shows how to use Unity’s **Unified Ray Tracing (URT)** API (`UnityEngine.Rendering.UnifiedRayTracing`) to trace rays on the GPU against scene meshes instead of using `Physics.Raycast`. It uses **generators** (a ray grid from a transform), **acceleration structures** built from `MeshRenderer` meshes, and optional **Geometry Pool** integration for real hit attributes (position, normal, UVs). When the Geometry Pool adapter is available, the example uses `FetchGeometry.hlsl` for triangle normals; otherwise it falls back to the public API and an approximate-normal shader.

All configuration is on the component: grid size, ray spacing, max distance, visualization toggles, and the two URT shader assets (full and approx). No custom plugins—only Unity’s built-in URT from the `com.unity.render-pipelines.core` package (URP/HDRP).

## Repository structure (two commits)

This repo is split into two commits:

- **First commit (video code)** – The example that the tutorial video is built from: public URT API only, a single shader, acceleration structure from `MeshRenderer`s, ray grid, and basic hit readback and Gizmo visualization. Use this commit if you want to follow along with the video or keep the example minimal.

- **Second commit (extended example)** – Adds the Geometry Pool integration (local copies of Unity’s internal adapter and pool), `TraceRays.urtshader` with `FetchGeometry.hlsl` for real hit normals, `TraceRaysApprox.urtshader` as fallback, `AsyncGPUReadback` for stable readback, hit-attribute buffers and normal visualization, and the mesh-only adapter with array-based `AddInstance`. See the Code Structure and Geometry Pool sections below for details.

## Code Structure

**Runtime**
- `Runtime/UnifiedRayTracingExample.cs` – MonoBehaviour: creates `RayTracingContext` and acceleration structure, optionally uses the Geometry Pool adapter, fills a ray grid each frame, dispatches the URT shader, and uses `AsyncGPUReadback` for hits and hit attributes. Exposes `BestPosition`-style data via the read-back arrays and draws rays/hits/normals in Gizmos.

**Shaders** (`Shaders/`)
- `TraceRays.urtshader` – Full path: includes `FetchGeometry.hlsl`, writes `_Hits` and `_HitAttributes` (real normals when the adapter is used).
- `TraceRaysApprox.urtshader` – Fallback: no FetchGeometry, writes `_Hits` and `_HitAttributes` with approximate normal (`-ray.direction`).

**Geometry Pool** (`GeometryPool/`)
- Local copies of Unity’s internal Geometry Pool and `AccelStructAdapter` (namespace `UnifiedRayTracing.Geometry`) so the example can use an array-based `AddInstance` and real hit attributes.
- `AccelStructAdapter.cs` – Mesh-only adapter: constructor `(accelStruct, geometryPoolKernels, copyBuffer)` and `AddInstance(..., uint[], uint[], bool[], uint)`.
- `GeometryPool.cs`, `AccelStructInstances.cs`, `BlockAllocator.cs`, `PersistentGpuArray.cs`, `GeometryPoolDefs.cs`, `GraphicsHelpers.cs` – Pool and allocator types used by the adapter.
- `CopyFromPackage.ps1` – Regenerates the Geometry Pool copies from the package (ensure the script’s package path matches your Unity package cache).

## Example Usage

Add the component to a GameObject, assign shaders, and press Play. Rays are cast in the transform’s forward direction; hits and normals are visualized in the Scene view.

```csharp
// Add UnifiedRayTracingExample to a GameObject. Assign:
// - URT Shader Asset → TraceRays.urtshader (for real normals when Geometry Pool is used)
// - URT Shader Approx Asset → TraceRaysApprox.urtshader (fallback)

// The component runs every frame: builds the acceleration structure (or adapter),
// fills the ray grid from the transform, dispatches the shader, and read-backs hits.
// In the Scene view you’ll see:
// - Blue lines = rays (to hit or max distance)
// - Green wire spheres = hit points
// - Yellow lines = normals (real from Geometry Pool, or approximate)
```

**Minimal setup**
1. Open a URP (or HDRP) scene with `com.unity.render-pipelines.core` in Packages.
2. Add meshes (e.g. Plane, Cubes) with `MeshRenderer` + `MeshFilter` for rays to hit.
3. Create an empty GameObject, add **Unified Ray Tracing Example**, assign both shader assets.
4. Position and rotate the object so its **forward** axis points at the meshes.
5. Press Play and check the Scene view for blue rays and green hit spheres.

**Optional** – Ensure the `GeometryPool` folder and copied adapter are present so the example can use the Geometry Pool for real normals; otherwise it uses the public API and the approx shader automatically.

## YouTube

[**Watch the tutorial video here**](https://youtu.be/placeholder-add-video-url-here)

You can also check out my [YouTube channel](https://www.youtube.com/@git-amend?sub_confirmation=1) for more Unity content.

## Installation and Setup

Place this folder (e.g. `Assets/_Project/Scripts/UnifiedRayTracingExample/`) in your Unity project. The example uses only Unity’s URT API and the render pipeline package; no extra dependencies are required. For the Geometry Pool path, the `GeometryPool` copies must be present (they are included); to regenerate them from the package, run `GeometryPool/CopyFromPackage.ps1` and ensure the script’s package path matches your Unity package cache.

**Requirements**
- Unity with URP or HDRP.
- Package `com.unity.render-pipelines.core` (Unified Ray Tracing).
- Scene meshes with `MeshRenderer` and `MeshFilter` to hit.

## Documentation

- [Unity – Get started with ray tracing](https://docs.unity3d.com/Packages/com.unity.render-pipelines.core@17.3/manual/UnifiedRayTracing/get-started.html)
- [Unity – URT workflow](https://docs.unity3d.com/Packages/com.unity.render-pipelines.core@17.3/manual/UnifiedRayTracing/workflow.html)
