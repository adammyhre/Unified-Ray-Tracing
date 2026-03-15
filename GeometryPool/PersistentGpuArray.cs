using System;
using System.Collections;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace UnifiedRayTracing.Geometry {
    public class PersistentGpuArray<Tstruct> : IDisposable
        where Tstruct : struct {
        BlockAllocator slotAllocator;
        ComputeBuffer gpuBuffer;
        NativeArray<Tstruct> cpuList;
        BitArray updates;
        bool gpuBufferDirty = true;
        int elementCountVal = 0;

        public int elementCount {
            get => elementCountVal;
            set => elementCountVal = value;
        }

        public PersistentGpuArray(int initialSize) {
            slotAllocator.Initialize(initialSize);
            gpuBuffer = new ComputeBuffer(initialSize, Marshal.SizeOf<Tstruct>());
            cpuList = new NativeArray<Tstruct>(initialSize, Allocator.Persistent);
            updates = new BitArray(initialSize);
            elementCount = 0;
        }

        public void Dispose() {
            elementCountVal = 0;
            slotAllocator.Dispose();
            gpuBuffer.Dispose();
            cpuList.Dispose();
        }

        public BlockAllocator.Allocation Add(Tstruct element) {
            elementCountVal++;
            var slotAllocation = slotAllocator.Allocate(1);

            if (!slotAllocation.valid) {
                Grow();
                slotAllocation = slotAllocator.Allocate(1);
                Assert.IsTrue(slotAllocation.valid);
            }

            cpuList[slotAllocation.block.offset] = element;
            updates[slotAllocation.block.offset] = true;
            gpuBufferDirty = true;
            return slotAllocation;
        }

        public BlockAllocator.Allocation[] Add(int count) {
            elementCountVal += count;
            var slotAllocation = slotAllocator.Allocate(count);

            if (!slotAllocation.valid) {
                Grow();
                slotAllocation = slotAllocator.Allocate(count);
                Assert.IsTrue(slotAllocation.valid);
            }

            return slotAllocator.SplitAllocation(slotAllocation, count);
        }

        public void Remove(BlockAllocator.Allocation allocation) {
            elementCountVal--;
            slotAllocator.FreeAllocation(allocation);
        }

        public void Clear() {
            elementCount = 0;
            var currentCapacity = slotAllocator.capacity;
            slotAllocator.Dispose();
            slotAllocator = new BlockAllocator();
            slotAllocator.Initialize(currentCapacity);
            updates = new BitArray(currentCapacity);
            gpuBufferDirty = false;
        }

        public void Set(BlockAllocator.Allocation allocation, Tstruct element) {
            cpuList[allocation.block.offset] = element;
            updates[allocation.block.offset] = true;
            gpuBufferDirty = true;
        }

        public Tstruct Get(BlockAllocator.Allocation allocation) {
            return cpuList[allocation.block.offset];
        }

        public void ModifyForEach(Func<Tstruct, Tstruct> lambda) {
            for (int i = 0; i < cpuList.Length; ++i) {
                cpuList[i] = lambda(cpuList[i]);
                updates[i] = true;
            }

            gpuBufferDirty = true;
        }

        public ComputeBuffer GetGpuBuffer(CommandBuffer cmd) {
            if (gpuBufferDirty) {
                int copyStartIndex = -1;

                for (int i = 0; i < updates.Length; ++i) {
                    if (updates[i]) {
                        if (copyStartIndex == -1)
                            copyStartIndex = i;

                        updates[i] = false;
                    } else if (copyStartIndex != -1) {
                        int copyEndIndex = i;
                        cmd.SetBufferData(gpuBuffer, cpuList, copyStartIndex, copyStartIndex, copyEndIndex - copyStartIndex);
                        copyStartIndex = -1;
                    }
                }

                if (copyStartIndex != -1) {
                    int copyEndIndex = updates.Length;
                    cmd.SetBufferData(gpuBuffer, cpuList, copyStartIndex, copyStartIndex, copyEndIndex - copyStartIndex);
                }

                gpuBufferDirty = false;
            }

            return gpuBuffer;
        }

        void Grow() {
            var oldCapacity = slotAllocator.capacity;
            slotAllocator.Grow(slotAllocator.capacity + 1);
            gpuBuffer.Dispose();
            gpuBuffer = new ComputeBuffer(slotAllocator.capacity, Marshal.SizeOf<Tstruct>());
            var oldList = cpuList;
            cpuList = new NativeArray<Tstruct>(slotAllocator.capacity, Allocator.Persistent);
            NativeArray<Tstruct>.Copy(oldList, cpuList, oldCapacity);
            oldList.Dispose();
            var oldUpdates = updates;
            updates = new BitArray(slotAllocator.capacity);
            for (int i = 0; i < oldCapacity; ++i)
                updates[i] = oldUpdates[i];
        }
    }
}