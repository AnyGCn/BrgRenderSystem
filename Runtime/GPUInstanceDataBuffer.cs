using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace BrgRenderSystem
{
    internal struct GPUInstanceComponentDesc
    {
        public int propertyID;
        public int byteSize;
        public bool isOverriden;
        public bool isPerInstance;

        public GPUInstanceComponentDesc(int inPropertyID, int inByteSize, bool inIsOverriden, bool inPerInstance)
        {
            propertyID = inPropertyID;
            byteSize = inByteSize;
            isOverriden = inIsOverriden;
            isPerInstance = inPerInstance;
        }
    }

    internal class GPUInstanceDataBuffer : IDisposable
    {
        public static readonly bool IsUBO = BatchRendererGroup.BufferTarget == BatchBufferTarget.ConstantBuffer;
        public static readonly int byteSizePerWindow = IsUBO ? BatchRendererGroup.GetConstantBufferMaxWindowSize() : 0;

        private static int s_NextLayoutVersion = 0;
        public static int NextVersion() { return ++s_NextLayoutVersion; }
        
        public int instanceCount;
        public int byteSize;
        public int windowCount;
        public int instancePerWindow;
        public int perInstanceComponentCount;
        public int version;
        public int layoutVersion;
        
        public NativeArray<float4> nativeBuffer;
        public GraphicsBuffer gpuBuffer;
        public NativeArray<GPUInstanceComponentDesc> descriptions;
        public NativeArray<MetadataValue> defaultMetadata;
        public NativeArray<int> gpuBufferComponentAddress;
        public NativeParallelHashMap<int, int> nameToMetadataMap;
        
        public bool valid => descriptions.IsCreated;

        public static GPUInstanceIndex IndexToInstance(int instancePerWindow, int index)
        {
            return new GPUInstanceIndex(index % instancePerWindow, index / instancePerWindow);
        }
        
        public static unsafe void WriteInstanceData<T>(void* outBufferPtr, int gpuAddressOffset, ref T instanceData, GPUInstanceIndex instanceIndex) where T : struct
        {
            UnsafeUtility.CopyStructureToPtr(ref instanceData,
                (byte*)outBufferPtr + instanceIndex.index * UnsafeUtility.SizeOf<T>() + gpuAddressOffset
                + instanceIndex.windowIndex * byteSizePerWindow);
        }
        
        internal GPUInstanceIndex IndexToInstance(int index)
        {
            return IndexToInstance(instancePerWindow, index);
        }
        
        public unsafe void WriteInstanceData<T>(int gpuAddressOffset, T instanceData, GPUInstanceIndex instanceIndex) where T : struct
        {
            WriteInstanceData(nativeBuffer.GetUnsafePtr(), gpuAddressOffset, ref instanceData, instanceIndex);
        }
        
        public int GetPropertyIndex(int propertyID, bool assertOnFail = true)
        {
            if (nameToMetadataMap.TryGetValue(propertyID, out int componentIndex))
            {
                return componentIndex;
            }

            if (assertOnFail)
                Assert.IsTrue(false, "Count not find gpu address for parameter specified: " + propertyID);
            return -1;
        }
        
        public int GetGpuAddress(string strName, bool assertOnFail = true)
        {
            int componentIndex = GetPropertyIndex(Shader.PropertyToID(strName), false);
            if (assertOnFail && componentIndex == -1)
                Assert.IsTrue(false, "Count not find gpu address for parameter specified: " + strName);

            return componentIndex != -1 ? gpuBufferComponentAddress[componentIndex] : -1;
        }

        public int GetGpuAddress(int propertyID, bool assertOnFail = true)
        {
            int componentIndex = GetPropertyIndex(propertyID, assertOnFail);
            return componentIndex != -1 ? gpuBufferComponentAddress[componentIndex] : -1;
        }

        public void SubmitToGpu()
        {
            gpuBuffer.SetData(nativeBuffer);
        }
        
        public void Dispose()
        {
            if (nativeBuffer.IsCreated)
                nativeBuffer.Dispose();
            
            if (descriptions.IsCreated)
                descriptions.Dispose();

            if (defaultMetadata.IsCreated)
                defaultMetadata.Dispose();

            if (gpuBufferComponentAddress.IsCreated)
                gpuBufferComponentAddress.Dispose();

            if (nameToMetadataMap.IsCreated)
                nameToMetadataMap.Dispose();

            if (gpuBuffer != null)
                gpuBuffer.Release();
        }
    }
}
