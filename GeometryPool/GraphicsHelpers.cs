using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnifiedRayTracing.Geometry {
    public static class GraphicsHelpers {
        public static void CopyBuffer(ComputeShader copyShader, CommandBuffer cmd, GraphicsBuffer src, int srcOffsetInDWords, GraphicsBuffer dst, int dstOffsetInDwords, int sizeInDWords) {
            const int groupSize = 256;
            const int elementsPerThread = 8;
            const int maxThreadGroups = 65535;
            const int maxBatchSizeInDWords = groupSize * elementsPerThread * maxThreadGroups;

            int remainingDWords = sizeInDWords;
            cmd.SetComputeBufferParam(copyShader, 0, "_SrcBuffer", src);
            cmd.SetComputeBufferParam(copyShader, 0, "_DstBuffer", dst);

            while (remainingDWords > 0) {
                int batchSize = math.min(remainingDWords, maxBatchSizeInDWords);
                cmd.SetComputeIntParam(copyShader, "_SrcOffset", srcOffsetInDWords);
                cmd.SetComputeIntParam(copyShader, "_DstOffset", dstOffsetInDwords);
                cmd.SetComputeIntParam(copyShader, "_Size", batchSize);
                cmd.DispatchCompute(copyShader, 0, DivUp(batchSize, elementsPerThread * groupSize), 1, 1);
                remainingDWords -= batchSize;
                srcOffsetInDWords += batchSize;
                dstOffsetInDwords += batchSize;
            }
        }

        public static void CopyBuffer(ComputeShader copyShader, GraphicsBuffer src, int srcOffsetInDWords, GraphicsBuffer dst, int dstOffsetInDwords, int sizeInDwords) {
            var cmd = new CommandBuffer();
            CopyBuffer(copyShader, cmd, src, srcOffsetInDWords, dst, dstOffsetInDwords, sizeInDwords);
            Graphics.ExecuteCommandBuffer(cmd);
        }

        public static bool ReallocateBuffer(ComputeShader copyShader, int oldCapacity, int newCapacity, int elementSizeInBytes, ref GraphicsBuffer buffer) {
            int bufferStrideInBytes = buffer.stride;
            var newBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (int)((long)newCapacity * elementSizeInBytes / bufferStrideInBytes), bufferStrideInBytes);
            if (!newBuffer.IsValid())
                return false;

            CopyBuffer(copyShader, buffer, 0, newBuffer, 0, (int)((long)oldCapacity * elementSizeInBytes / 4));
            buffer.Dispose();
            buffer = newBuffer;
            return true;
        }

        public static long MaxGraphicsBufferSizeInBytes => SystemInfo.maxGraphicsBufferSize;
        public static float MaxGraphicsBufferSizeInGigaBytes => MaxGraphicsBufferSizeInBytes / 1024f / 1024f / 1024f;
        public static int DivUp(int x, int y) => (x + y - 1) / y;
        public static int DivUp(int x, uint y) => (x + (int)y - 1) / (int)y;
        public static uint DivUp(uint x, uint y) => (x + y - 1) / y;
        public static uint3 DivUp(uint3 x, uint3 y) => (x + y - 1) / y;

        public static void Flush(CommandBuffer cmd) {
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            GL.Flush();
        }
    }
}