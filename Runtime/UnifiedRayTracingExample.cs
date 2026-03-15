using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.UnifiedRayTracing;
using Object = UnityEngine.Object;

/// <summary>
/// Use Unity's Unified Ray Tracing API to trace rays against scene meshes
/// instead of Physics.Raycast. One dispatch traces many rays; results are read back and visualized.
/// </summary>
public class UnifiedRayTracingExample : MonoBehaviour {
    #region Fields

    [Header("Ray grid (from this transform)")]
    [SerializeField] int gridSize = 16;
    [SerializeField] float raySpacing = 0.5f;
    [SerializeField] float maxDistance = 50f;
    
    [Header("Visualization")]
    [SerializeField] bool drawRays = true;
    [SerializeField] bool drawHits = true;
    [SerializeField] Color rayColor = new Color(0.2f, 0.6f, 1f);
    [SerializeField] Color hitColor = Color.green;
    
    [Header("Shader")]
    [Tooltip("Assign the TraceRays.urtshader from this folder. Required.")]
    [SerializeField] Object urtShaderAsset;
    
    RayTracingContext context;
    RayTracingResources resources;
    IRayTracingAccelStruct accelStruct;
    IRayTracingShader rtShader;
    GraphicsBuffer raysBuffer;
    GraphicsBuffer hitsBuffer;
    GraphicsBuffer scratchBuffer;
    CommandBuffer cmd;
    RayWithFlags[] rays;
    Hit[] hits;
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

    #endregion

    void OnEnable() {
        rayCount = gridSize * gridSize;
        rays = new RayWithFlags[rayCount];
        hits = new Hit[rayCount];
        
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
        PopulateAccelerationStructure();
        
        IRayTracingShader shader = LoadShader();
        if (shader == null) return;
        rtShader = shader;
        
        int rayStride = Marshal.SizeOf<RayWithFlags>();
        int hitStride = Marshal.SizeOf<Hit>();
        raysBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rayCount, rayStride);
        hitsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rayCount, hitStride);
        scratchBuffer = RayTracingHelper.CreateScratchBufferForBuildAndDispatch(accelStruct, rtShader, (uint)rayCount, 1, 1);
        cmd = new CommandBuffer { name = "URT Example" };
    }

    void OnDisable() {
        cmd?.Release();
        scratchBuffer?.Release();
        hitsBuffer?.Release();
        raysBuffer?.Release();
        accelStruct?.Dispose();
        context?.Dispose();
    }

    void Update() {
        if (!resourcesValid || rtShader == null || accelStruct == null) return;
        
        UpdateRays();
        raysBuffer.SetData(rays);
        
        cmd.Clear();
        accelStruct.Build(cmd, scratchBuffer);
        rtShader.SetAccelerationStructure(cmd, "_AccelStruct", accelStruct);
        rtShader.SetBufferParam(cmd, Shader.PropertyToID("_Rays"), raysBuffer);
        rtShader.SetBufferParam(cmd, Shader.PropertyToID("_Hits"), hitsBuffer);
        rtShader.Dispatch(cmd, scratchBuffer, (uint)rayCount, 1, 1);
        Graphics.ExecuteCommandBuffer(cmd);
        
        hitsBuffer.GetData(hits);
    }

    void OnDrawGizmos() {
        if (hits == null || !drawRays && !drawHits) return;

        for (int i = 0; i < rayCount; i++) {
            Vector3 origin = rays[i].origin;
            Vector3 dir = rays[i].direction;
            float tMax = rays[i].tMax;

            if (drawRays) {
                float dist = hits[i].Valid() ? hits[i].hitDistance : tMax;
                Gizmos.color = rayColor;
                Gizmos.DrawLine(origin, origin + dir * Mathf.Min(dist, tMax));
            }

            if (drawHits && hits[i].Valid()) {
                Gizmos.color = hitColor;
                Gizmos.DrawWireSphere(origin + dir * hits[i].hitDistance, 0.05f);
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

    IRayTracingShader LoadShader() {
        if (urtShaderAsset == null) {
            Debug.LogError("UnifiedRayTracingExample: Assign the URT shader (e.g. TraceRays.urtshader) in the inspector.");
            return null;
        }
        
        return context.CreateRayTracingShader(urtShaderAsset);
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
}














