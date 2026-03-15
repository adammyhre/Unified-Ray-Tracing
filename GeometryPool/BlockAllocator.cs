using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;

namespace UnifiedRayTracing.Geometry {
    public struct BlockAllocator : IDisposable {
        public struct Block {
            public int offset;
            public int count;

            public static readonly Block Invalid = new Block() { offset = 0, count = 0 };
        }

        public struct Allocation {
            public int handle;
            public Block block;

            public static readonly Allocation Invalid = new Allocation() { handle = -1 };
            public readonly bool valid => handle != -1;
        }

        int freeElementCount;
        int maxElementCount;
        NativeList<Block> freeBlocks;
        NativeList<Block> usedBlocks;
        NativeList<int> freeSlots;

        public int freeElementsCount => freeElementCount;
        public int freeBlocksCount => freeBlocks.Length;
        public int capacity => maxElementCount;
        public int allocatedSize => maxElementCount - freeElementCount;

        public void Initialize(int maxElementCounts) {
            maxElementCount = maxElementCounts;
            freeElementCount = maxElementCounts;

            if (!freeBlocks.IsCreated)
                freeBlocks = new NativeList<Block>(Allocator.Persistent);
            else
                freeBlocks.Clear();

            freeBlocks.Add(new Block() { offset = 0, count = freeElementCount });

            if (!usedBlocks.IsCreated)
                usedBlocks = new NativeList<Block>(Allocator.Persistent);
            else
                usedBlocks.Clear();

            if (!freeSlots.IsCreated)
                freeSlots = new NativeList<int>(Allocator.Persistent);
            else
                freeSlots.Clear();
        }

        int CalculateGeometricGrowthCapacity(int desiredNewCapacity, int maxAllowedNewCapacity) {
            var oldCapacity = capacity;
            if (oldCapacity > maxAllowedNewCapacity - oldCapacity / 2)
                return maxAllowedNewCapacity;

            var geometricNewCapacity = oldCapacity + oldCapacity / 2;
            if (geometricNewCapacity < desiredNewCapacity)
                return desiredNewCapacity;

            return geometricNewCapacity;
        }

        public int Grow(int newDesiredCapacity, int maxAllowedCapacity = Int32.MaxValue) {
            Debug.Assert(newDesiredCapacity > 0);
            Debug.Assert(maxAllowedCapacity > 0);
            Debug.Assert(capacity < newDesiredCapacity);
            Debug.Assert(maxAllowedCapacity >= newDesiredCapacity);

            var newCapacity = CalculateGeometricGrowthCapacity(newDesiredCapacity, maxAllowedCapacity);
            var oldCapacity = maxElementCount;
            var addedElements = newCapacity - oldCapacity;
            Debug.Assert(addedElements > 0);

            freeElementCount += addedElements;
            maxElementCount = newCapacity;

            int blockToMerge = freeBlocks.Length;
            freeBlocks.Add(new Block() { offset = oldCapacity, count = addedElements });

            while (blockToMerge != -1)
                blockToMerge = MergeBlockFrontBack(blockToMerge);

            return maxElementCount;
        }

        public bool GetExpectedGrowthToFitAllocation(int elementCounts, int maxAllowedCapacity, out int newCapacity) {
            newCapacity = 0;
            var additionalRequiredElements = freeBlocks.IsEmpty ? elementCounts : math.max(elementCounts - freeBlocks[freeBlocks.Length - 1].count, 0);
            if (maxAllowedCapacity < capacity || (maxAllowedCapacity - capacity) < additionalRequiredElements)
                return false;

            newCapacity = additionalRequiredElements > 0 ? CalculateGeometricGrowthCapacity(capacity + additionalRequiredElements, maxAllowedCapacity) : capacity;
            return true;
        }

        public Allocation GrowAndAllocate(int elementCounts, out int oldCapacity, out int newCapacity) {
            return GrowAndAllocate(elementCounts, Int32.MaxValue, out oldCapacity, out newCapacity);
        }

        public Allocation GrowAndAllocate(int elementCounts, int maxAllowedCapacity, out int oldCapacity, out int newCapacity) {
            oldCapacity = capacity;
            var additionalRequiredElements = freeBlocks.IsEmpty ? elementCounts : math.max(elementCounts - freeBlocks[freeBlocks.Length - 1].count, 0);

            if (maxAllowedCapacity < capacity || (maxAllowedCapacity - capacity) < additionalRequiredElements) {
                newCapacity = capacity;
                return Allocation.Invalid;
            }

            newCapacity = additionalRequiredElements > 0 ? Grow(capacity + additionalRequiredElements, maxAllowedCapacity) : capacity;
            Debug.Assert(newCapacity >= oldCapacity + additionalRequiredElements);
            var alloc = Allocate(elementCounts);
            Assert.IsTrue(alloc.valid);
            return alloc;
        }

        public void Dispose() {
            maxElementCount = 0;
            freeElementCount = 0;
            if (freeBlocks.IsCreated)
                freeBlocks.Dispose();

            if (usedBlocks.IsCreated)
                usedBlocks.Dispose();

            if (freeSlots.IsCreated)
                freeSlots.Dispose();
        }

        public Allocation Allocate(int elementCounts) {
            if (elementCounts > freeElementCount || freeBlocks.IsEmpty)
                return Allocation.Invalid;

            int selectedBlock = -1;
            int currentBlockCount = 0;

            for (int b = 0; b < freeBlocks.Length; ++b) {
                Block block = freeBlocks[b];

                if (elementCounts <= block.count && (selectedBlock == -1 || block.count < currentBlockCount)) {
                    currentBlockCount = block.count;
                    selectedBlock = b;
                }
            }

            if (selectedBlock == -1)
                return Allocation.Invalid;

            Block allocationBlock = freeBlocks[selectedBlock];
            Block split = allocationBlock;
            split.offset += elementCounts;
            split.count -= elementCounts;
            allocationBlock.count = elementCounts;

            if (split.count > 0)
                freeBlocks[selectedBlock] = split;
            else
                freeBlocks.RemoveAtSwapBack(selectedBlock);

            int allocationHandle;

            if (freeSlots.IsEmpty) {
                allocationHandle = usedBlocks.Length;
                usedBlocks.Add(allocationBlock);
            } else {
                allocationHandle = freeSlots[freeSlots.Length - 1];
                freeSlots.RemoveAtSwapBack(freeSlots.Length - 1);
                usedBlocks[allocationHandle] = allocationBlock;
            }

            freeElementCount -= elementCounts;
            return new Allocation() { handle = allocationHandle, block = allocationBlock };
        }

        int MergeBlockFrontBack(int freeBlockId) {
            Block targetBlock = freeBlocks[freeBlockId];

            for (int i = 0; i < freeBlocks.Length; ++i) {
                if (i == freeBlockId)
                    continue;

                Block freeBlock = freeBlocks[i];
                bool mergeTargetBlock = false;

                if (targetBlock.offset == (freeBlock.offset + freeBlock.count)) {
                    freeBlock.count += targetBlock.count;
                    mergeTargetBlock = true;
                } else if (freeBlock.offset == (targetBlock.offset + targetBlock.count)) {
                    freeBlock.offset = targetBlock.offset;
                    freeBlock.count += targetBlock.count;
                    mergeTargetBlock = true;
                }

                if (mergeTargetBlock) {
                    freeBlocks[i] = freeBlock;
                    freeBlocks.RemoveAtSwapBack(freeBlockId);
                    return i == freeBlocks.Length ? freeBlockId : i;
                }
            }

            return -1;
        }

        public void FreeAllocation(in Allocation allocation) {
            Debug.Assert(allocation.valid);
            freeSlots.Add(allocation.handle);
            usedBlocks[allocation.handle] = Block.Invalid;
            int blockToMerge = freeBlocks.Length;
            freeBlocks.Add(allocation.block);
            while (blockToMerge != -1)
                blockToMerge = MergeBlockFrontBack(blockToMerge);

            freeElementCount += allocation.block.count;
        }

        public Allocation[] SplitAllocation(in Allocation allocation, int count) {
            Debug.Assert(allocation.valid);
            var newAllocs = new Allocation[count];
            var newAllocsSize = allocation.block.count / count;
            var newBlock0 = new Block { offset = allocation.block.offset, count = newAllocsSize };
            usedBlocks[allocation.handle] = newBlock0;
            newAllocs[0] = new Allocation() { handle = allocation.handle, block = newBlock0 };

            for (int i = 1; i < count; ++i) {
                Block block = new Block { offset = allocation.block.offset + i * newAllocsSize, count = newAllocsSize };
                int allocationHandle;

                if (freeSlots.IsEmpty) {
                    allocationHandle = usedBlocks.Length;
                    usedBlocks.Add(block);
                } else {
                    allocationHandle = freeSlots[freeSlots.Length - 1];
                    freeSlots.RemoveAtSwapBack(freeSlots.Length - 1);
                    usedBlocks[allocationHandle] = block;
                }

                newAllocs[i] = new Allocation() { handle = allocationHandle, block = block };
            }

            return newAllocs;
        }
    }
}