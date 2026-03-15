using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.UnifiedRayTracing;
using Object = UnityEngine.Object;

/// <summary>
/// Use Unity's Unified Ray Tracing API to trace rays against scene meshes.
/// When the Geometry Pool adapter is available, uses real hit attributes (normals); otherwise uses the approx shader.
/// </summary>
public class UnifiedRayTracingExample : MonoBehaviour {
    #region Fields

    [Header("Ray grid (from this transform)")] [SerializeField]
    int gridSize = 16;

    [SerializeField] float raySpacing = 0.5f;
    [SerializeField] float maxDistance = 50f;

    [Header("Visualization")] [SerializeField]
    bool drawRays = true;

    [SerializeField] bool drawHits = true;
    [SerializeField] bool drawNormals = true;
    [SerializeField] Color rayColor = new Color(0.2f, 0.6f, 1f);
    [SerializeField] Color hitColor = Color.green;
    [SerializeField] Color normalColor = Color.yellow;

    [Header("Shaders")] [Tooltip("Assign TraceRays.urtshader (with FetchGeometry) when using the Geometry Pool adapter. Required for real normals.")] [SerializeField]
    Object urtShaderAsset;

    [Tooltip("Assign TraceRaysApprox.urtshader when not using the adapter. Used as fallback.")] [SerializeField]
    Object urtShaderApproxAsset;

    RayTracingContext context;
    RayTracingResources resources;
    IRayTracingAccelStruct accelStruct;
    object adapter;
    bool useAdapter;
    IRayTracingShader rtShader;
    GraphicsBuffer raysBuffer;
    GraphicsBuffer hitsBuffer;
    GraphicsBuffer hitAttributesBuffer;
    GraphicsBuffer scratchBuffer;
    CommandBuffer cmd;
    RayWithFlags[] rays;
    Hit[] hits;
    HitGeomAttributes[] hitAttributes;
    int rayCount;
    bool resourcesValid;

    #endregion

    #region Structs

    [StructLayout(LayoutKind.Sequential)]
    struct RayWithFlags {
        public Vector3 origin;
        public float tMin;
        public Vector3 direction;
        public float tMax;
        public uint culling;
        public uint instanceMask;
        public uint padding;
        public uint padding2;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Hit {
        public uint instanceID;
        public uint primitiveIndex;
        public Vector2 uvBarycentrics;
        public float hitDistance;
        public uint isFrontFace;

        public bool Valid() => instanceID != 0xFFFFFFFF;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HitGeomAttributes {
        public Vector3 position;
        public Vector3 normal;
        public Vector3 faceNormal;
        public Vector2 uv0;
        public Vector2 uv1;
    }

    #endregion

    protected void OnEnable() {
        rayCount = gridSize * gridSize;
        rays = new RayWithFlags[rayCount];
        hits = new Hit[rayCount];
        hitAttributes = new HitGeomAttributes[rayCount];

        resources = new RayTracingResources();
        resourcesValid = resources.LoadFromRenderPipelineResources();

        if (!resourcesValid) {
            Debug.LogWarning("UnifiedRayTracingExample: RayTracingResources could not load. Ensure URP/HDRP and package com.unity.render-pipelines.core are present.");
            return;
        }

        var backend = RayTracingContext.IsBackendSupported(RayTracingBackend.Hardware)
            ? RayTracingBackend.Hardware
            : RayTracingBackend.Compute;

        context = new RayTracingContext(backend, resources);

        var options = new AccelerationStructureOptions();
        accelStruct = context.CreateAccelerationStructure(options);

        useAdapter = TryCreateAdapter();

        if (useAdapter && adapter != null) {
            PopulateViaAdapter();
        } else {
            PopulateAccelerationStructure();
        }

        IRayTracingAccelStruct asForScratch = useAdapter && adapter != null ? GetAdapterAccelStruct() : accelStruct;
        rtShader = LoadShader(useAdapter && adapter != null);
        if (rtShader == null) return;

        int rayStride = Marshal.SizeOf<RayWithFlags>();
        int hitStride = Marshal.SizeOf<Hit>();
        int attrStride = Marshal.SizeOf<HitGeomAttributes>();

        raysBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rayCount, rayStride);
        hitsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rayCount, hitStride);
        hitAttributesBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rayCount, attrStride);
        scratchBuffer = RayTracingHelper.CreateScratchBufferForBuildAndDispatch(asForScratch, rtShader, (uint)rayCount, 1, 1);
        cmd = new CommandBuffer { name = "URT Example" };

        MarkHitsInvalid();
    }

    protected void OnDisable() {
        cmd?.Release();
        scratchBuffer?.Release();
        hitAttributesBuffer?.Release();
        hitsBuffer?.Release();
        raysBuffer?.Release();

        if (useAdapter && adapter != null) {
            DisposeAdapter();
            adapter = null;
        } else {
            accelStruct?.Dispose();
        }

        accelStruct = null;
        context?.Dispose();
    }

    void Update() {
        if (!resourcesValid || rtShader == null) return;
        if (useAdapter && adapter == null) return;
        if (!useAdapter && accelStruct == null) return;

        UpdateRays();
        raysBuffer.SetData(rays);

        cmd.Clear();

        if (useAdapter && adapter != null) {
            AdapterBuild(cmd, ref scratchBuffer);
            AdapterBind(cmd, "_AccelStruct", rtShader);
        } else {
            accelStruct.Build(cmd, scratchBuffer);
            rtShader.SetAccelerationStructure(cmd, "_AccelStruct", accelStruct);
        }

        rtShader.SetBufferParam(cmd, Shader.PropertyToID("_Rays"), raysBuffer);
        rtShader.SetBufferParam(cmd, Shader.PropertyToID("_Hits"), hitsBuffer);
        rtShader.SetBufferParam(cmd, Shader.PropertyToID("_HitAttributes"), hitAttributesBuffer);

        rtShader.Dispatch(cmd, scratchBuffer, (uint)rayCount, 1, 1);
        Graphics.ExecuteCommandBuffer(cmd);

        AsyncGPUReadback.Request(hitsBuffer, OnHitsReadback);
        AsyncGPUReadback.Request(hitAttributesBuffer, OnHitAttributesReadback);
    }

    void OnHitsReadback(AsyncGPUReadbackRequest request) {
        if (request.hasError || !request.done) return;
        request.GetData<Hit>().CopyTo(hits);
    }

    void OnHitAttributesReadback(AsyncGPUReadbackRequest request) {
        if (request.hasError || !request.done) return;
        request.GetData<HitGeomAttributes>().CopyTo(hitAttributes);
    }

    void MarkHitsInvalid() {
        for (int i = 0; i < rayCount; i++)
            hits[i].instanceID = 0xFFFFFFFF;
    }

    void OnDrawGizmos() {
        if (!resourcesValid || hits == null || rays == null || rayCount <= 0) return;
        if (!drawRays && !drawHits && !drawNormals) return;

        for (int i = 0; i < rayCount; i++) {
            Vector3 origin = rays[i].origin;
            Vector3 dir = rays[i].direction;
            float tMax = rays[i].tMax;

            if (drawRays) {
                float dist = hits[i].Valid() ? hits[i].hitDistance : tMax;
                dist = Mathf.Clamp(dist, 0f, tMax);
                Gizmos.color = rayColor;
                Gizmos.DrawLine(origin, origin + dir * dist);
            }

            if (drawHits && hits[i].Valid()) {
                float dist = Mathf.Clamp(hits[i].hitDistance, 0.001f, tMax - 0.001f);
                Vector3 pos = origin + dir * dist;
                Gizmos.color = hitColor;
                Gizmos.DrawWireSphere(pos, 0.05f);

                if (drawNormals && useAdapter && hitAttributes != null) {
                    Vector3 n = hitAttributes[i].normal;

                    if (n.sqrMagnitude > 0.01f) {
                        Gizmos.color = normalColor;
                        Gizmos.DrawLine(pos, pos + n * 0.5f);
                    }
                } else if (drawNormals && !useAdapter) {
                    Gizmos.color = normalColor;
                    Gizmos.DrawLine(pos, pos - dir.normalized * 0.5f);
                }
            }
        }
    }

    void UpdateRays() {
        Transform t = transform;
        Vector3 center = t.position;
        Vector3 right = t.right;
        Vector3 up = t.up;
        Vector3 forward = t.forward;

        int halfGrid = gridSize / 2;
        int idx = 0;

        for (int y = 0; y < gridSize; y++) {
            for (int x = 0; x < gridSize; x++) {
                float ox = (x - halfGrid) * raySpacing;
                float oy = (y - halfGrid) * raySpacing;
                Vector3 origin = center + right * ox + up * oy;
                Vector3 direction = forward;

                rays[idx] = new RayWithFlags {
                    origin = origin,
                    tMin = 0f,
                    direction = direction,
                    tMax = maxDistance,
                    culling = 0,
                    instanceMask = 0xFFFFFFFF,
                    padding = 0,
                    padding2 = 0
                };

                idx++;
            }
        }
    }

    bool TryCreateAdapter() {
        ComputeShader kernels = GetResourceComputeShader("geometryPoolKernels");
        ComputeShader copyBuffer = GetResourceComputeShader("copyBuffer");
        if (kernels == null || copyBuffer == null) return false;

        try {
            Type adapterType = GetType("UnifiedRayTracing.Geometry.AccelStructAdapter");

            if (adapterType != null) {
                adapter = Activator.CreateInstance(adapterType, accelStruct, kernels, copyBuffer);
                if (adapter != null) return true;
            }

            return false;
        }
        catch (Exception e) {
            Debug.LogWarning("UnifiedRayTracingExample: Adapter creation failed (using public API and approx normals): " + e.Message);
            return false;
        }
    }

    static Type GetType(string typeName) {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
            var t = asm.GetType(typeName, false);
            if (t != null) return t;
        }

        return null;
    }

    ComputeShader GetResourceComputeShader(string propertyName) {
        if (resources == null) return null;
        var prop = typeof(RayTracingResources).GetProperty(propertyName,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase,
            null, typeof(ComputeShader), Type.EmptyTypes, null);

        return prop?.GetValue(resources) as ComputeShader;
    }

    void PopulateViaAdapter() {
        var renderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);

        foreach (var r in renderers) {
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            Mesh mesh = mf.sharedMesh;
            int n = mesh.subMeshCount;
            uint[] masks = new uint[n];
            uint[] materialIds = new uint[n];
            bool[] opaque = new bool[n];

            for (int i = 0; i < n; i++) {
                masks[i] = 0xFFFFFFFF;
                materialIds[i] = 0xFFFFFFFF;
                opaque[i] = true;
            }

            AdapterAddInstance(r.GetInstanceID(), mesh, r.transform.localToWorldMatrix, masks, materialIds, opaque, 0xFFFFFFFF);
        }
    }

    void AdapterAddInstance(int objectHandle, Mesh mesh, Matrix4x4 localToWorld, uint[] masks, uint[] materialIds, bool[] opaque, uint layerMask) {
        if (adapter == null) return;
        Type t = adapter.GetType();
        var method = t.GetMethod("AddInstance", new Type[] {
            typeof(int), typeof(Mesh), typeof(Matrix4x4),
            typeof(uint[]), typeof(uint[]), typeof(bool[]), typeof(uint)
        });

        if (method != null) {
            method.Invoke(adapter, new object[] { objectHandle, mesh, localToWorld, masks, materialIds, opaque, layerMask });
            return;
        }

        var meshRendererMethod = t.GetMethod("AddInstance", new Type[] {
            typeof(int), typeof(Component), typeof(uint[]), typeof(uint[]), typeof(bool[]), typeof(uint)
        });

        if (meshRendererMethod != null) {
            foreach (var r in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None)) {
                if (r.GetInstanceID() == objectHandle) {
                    meshRendererMethod.Invoke(adapter, new object[] { objectHandle, r, masks, materialIds, opaque, layerMask });
                    return;
                }
            }
        }
    }

    void AdapterBuild(CommandBuffer commandBuffer, ref GraphicsBuffer scratch) {
        if (adapter == null) return;
        var method = adapter.GetType().GetMethod("Build", BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(CommandBuffer), typeof(GraphicsBuffer).MakeByRefType() }, null);

        if (method != null) {
            object[] args = { commandBuffer, scratch };
            method.Invoke(adapter, args);
            scratch = (GraphicsBuffer)args[1];
        }
    }

    void AdapterBind(CommandBuffer commandBuffer, string propertyName, IRayTracingShader shader) {
        if (adapter == null) return;
        adapter.GetType().GetMethod("Bind", BindingFlags.Public | BindingFlags.Instance)?.Invoke(adapter, new object[] { commandBuffer, propertyName, shader });
    }

    IRayTracingAccelStruct GetAdapterAccelStruct() {
        if (adapter == null) return null;
        var method = adapter.GetType().GetMethod("GetAccelerationStructure", BindingFlags.Public | BindingFlags.Instance);
        return method?.Invoke(adapter, null) as IRayTracingAccelStruct;
    }

    void DisposeAdapter() {
        if (adapter is IDisposable disp)
            disp.Dispose();
    }

    void PopulateAccelerationStructure() {
        var renderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);

        foreach (var r in renderers) {
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            Mesh mesh = mf.sharedMesh;

            for (int i = 0; i < mesh.subMeshCount; i++) {
                var desc = new MeshInstanceDesc(mesh, i);
                desc.localToWorldMatrix = r.transform.localToWorldMatrix;
                accelStruct.AddInstance(desc);
            }
        }
    }

    IRayTracingShader LoadShader(bool withGeometryPool) {
        Object asset = withGeometryPool ? urtShaderAsset : (urtShaderApproxAsset != null ? urtShaderApproxAsset : urtShaderAsset);

        if (asset == null) {
            Debug.LogError("UnifiedRayTracingExample: Assign the URT shader(s) in the inspector (TraceRays.urtshader and/or TraceRaysApprox.urtshader).");
            return null;
        }

        return context.CreateRayTracingShader(asset);
    }
}