using System;
using System.Collections.Generic;
using UnityEngine.Assertions;
using UnityEngine.Rendering;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering.UnifiedRayTracing;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnifiedRayTracing.Geometry {
    public class AccelStructAdapter : IDisposable {
        IRayTracingAccelStruct _accelStruct;
        AccelStructInstances _instances;

        public AccelStructInstances Instances => _instances;

        struct InstanceIDs {
            public int InstanceID;
            public int AccelStructID;
        }

        readonly Dictionary<int, InstanceIDs[]> _objectHandleToInstances = new();

        public AccelStructAdapter(IRayTracingAccelStruct accelStruct, GeometryPool geometryPool) {
            _accelStruct = accelStruct;
            _instances = new AccelStructInstances(geometryPool);
        }

        public AccelStructAdapter(IRayTracingAccelStruct accelStruct, ComputeShader geometryPoolKernels, ComputeShader copyBuffer)
            : this(accelStruct, new GeometryPool(GeometryPoolDesc.NewDefault(), geometryPoolKernels, copyBuffer)) {
        }

        public IRayTracingAccelStruct GetAccelerationStructure() {
            return _accelStruct;
        }

        public GeometryPool GeometryPool => _instances.geometryPool;

        public void Bind(CommandBuffer cmd, string propertyName, IRayTracingShader shader) {
            shader.SetAccelerationStructure(cmd, propertyName, _accelStruct);
            _instances.Bind(cmd, shader);
        }

        public void Dispose() {
            _instances?.Dispose();
            _instances = null;
            _accelStruct?.Dispose();
            _accelStruct = null;
            _objectHandleToInstances.Clear();
        }

        public void AddInstance(int objectHandle, Component meshRendererOrTerrain, Span<uint> perSubMeshMask, Span<uint> perSubMeshMaterialIDs, Span<bool> perSubMeshIsOpaque,
            uint renderingLayerMask) {
            if (meshRendererOrTerrain is Terrain) {
                throw new NotSupportedException("This adapter is mesh-only; Terrain is not supported. Use the (Mesh, Matrix4x4, ...) or array overload for mesh renderers.");
            }

            var meshRenderer = (MeshRenderer)meshRendererOrTerrain;
            Debug.Assert(meshRenderer.enabled, "Mesh renderers are expected to be enabled.");
            Debug.Assert(!meshRenderer.isPartOfStaticBatch, "Mesh renderers are expected to not be part of static batch.");
            var mesh = meshRenderer.GetComponent<MeshFilter>().sharedMesh;
            AddInstance(objectHandle, mesh, meshRenderer.transform.localToWorldMatrix, perSubMeshMask, perSubMeshMaterialIDs, perSubMeshIsOpaque, renderingLayerMask);
        }

        public void AddInstance(int objectHandle, Mesh mesh, Matrix4x4 localToWorldMatrix, uint[] perSubMeshMask, uint[] perSubMeshMaterialIDs, bool[] perSubMeshIsOpaque, uint renderingLayerMask) {
            AddInstance(objectHandle, mesh, localToWorldMatrix, perSubMeshMask.AsSpan(), perSubMeshMaterialIDs.AsSpan(), perSubMeshIsOpaque.AsSpan(), renderingLayerMask);
        }

        public void AddInstance(int objectHandle, Mesh mesh, Matrix4x4 localToWorldMatrix, Span<uint> perSubMeshMask, Span<uint> perSubMeshMaterialIDs, Span<bool> perSubMeshIsOpaque,
            uint renderingLayerMask) {
            int subMeshCount = mesh.subMeshCount;

            var instances = new InstanceIDs[subMeshCount];

            for (int i = 0; i < subMeshCount; ++i) {
                var instanceDesc = new MeshInstanceDesc(mesh, i) {
                    localToWorldMatrix = localToWorldMatrix,
                    mask = perSubMeshMask[i],
                    opaqueGeometry = perSubMeshIsOpaque[i]
                };

                instances[i].InstanceID = _instances.AddInstance(instanceDesc, perSubMeshMaterialIDs[i], renderingLayerMask);
                instanceDesc.instanceID = (uint)instances[i].InstanceID;
                instances[i].AccelStructID = _accelStruct.AddInstance(instanceDesc);
            }

            _objectHandleToInstances.Add(objectHandle, instances);
        }

        InstanceIDs AddInstance(MeshInstanceDesc instanceDesc, uint materialID, uint renderingLayerMask) {
            InstanceIDs res = new InstanceIDs();
            res.InstanceID = _instances.AddInstance(instanceDesc, materialID, renderingLayerMask);
            instanceDesc.instanceID = (uint)res.InstanceID;
            res.AccelStructID = _accelStruct.AddInstance(instanceDesc);

            return res;
        }


        public void RemoveInstance(int objectHandle) {
            bool success = _objectHandleToInstances.TryGetValue(objectHandle, out var instances);
            Assert.IsTrue(success);

            foreach (var instance in instances) {
                _instances.RemoveInstance(instance.InstanceID);
                _accelStruct.RemoveInstance(instance.AccelStructID);
            }

            _objectHandleToInstances.Remove(objectHandle);
        }

        public void UpdateInstanceTransform(int objectHandle, Matrix4x4 localToWorldMatrix) {
            bool success = _objectHandleToInstances.TryGetValue(objectHandle, out var instances);
            Assert.IsTrue(success);

            foreach (var instance in instances) {
                _instances.UpdateInstanceTransform(instance.InstanceID, localToWorldMatrix);
                _accelStruct.UpdateInstanceTransform(instance.AccelStructID, localToWorldMatrix);
            }
        }

        public void UpdateInstanceMaterialIDs(int objectHandle, Span<uint> perSubMeshMaterialIDs) {
            bool success = _objectHandleToInstances.TryGetValue(objectHandle, out var instances);
            Assert.IsTrue(success);
            Assert.IsTrue(perSubMeshMaterialIDs.Length >= instances.Length);
            int i = 0;

            foreach (var instance in instances) {
                _instances.UpdateInstanceMaterialID(instance.InstanceID, perSubMeshMaterialIDs[i++]);
            }
        }

        public void UpdateInstanceMask(int objectHandle, Span<uint> perSubMeshMask) {
            bool success = _objectHandleToInstances.TryGetValue(objectHandle, out var instances);
            Assert.IsTrue(success);
            Assert.IsTrue(perSubMeshMask.Length >= instances.Length);
            int i = 0;

            foreach (var instance in instances) {
                _instances.UpdateInstanceMask(instance.InstanceID, perSubMeshMask[i]);
                _accelStruct.UpdateInstanceMask(instance.AccelStructID, perSubMeshMask[i]);
                i++;
            }
        }

        public void UpdateInstanceMask(int objectHandle, uint mask) {
            bool success = _objectHandleToInstances.TryGetValue(objectHandle, out var instances);
            Assert.IsTrue(success);

            var perSubMeshMask = new uint[instances.Length];
            Array.Fill(perSubMeshMask, mask);

            int i = 0;

            foreach (var instance in instances) {
                _instances.UpdateInstanceMask(instance.InstanceID, perSubMeshMask[i]);
                _accelStruct.UpdateInstanceMask(instance.AccelStructID, perSubMeshMask[i]);
                i++;
            }
        }

        public void Build(CommandBuffer cmd, ref GraphicsBuffer scratchBuffer) {
            RayTracingHelper.ResizeScratchBufferForBuild(_accelStruct, ref scratchBuffer);
            _accelStruct.Build(cmd, scratchBuffer);
        }

        public void NextFrame() {
            _instances.NextFrame();
        }

        public bool GetInstanceIDs(int rendererID, out int[] instanceIDs) {
            if (!_objectHandleToInstances.TryGetValue(rendererID, out InstanceIDs[] instIDs)) {
                // This should never happen as long as the renderer was already added to the acceleration structure
                instanceIDs = null;
                return false;
            }

            instanceIDs = Array.ConvertAll(instIDs, item => item.InstanceID);
            return true;
        }
    }
}