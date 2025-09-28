using Unity.Burst;
using Unity.Collections;
using UnityEngine.Assertions;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrgRenderSystem
{
    internal struct GPUInstanceDataBufferBuilder : IDisposable
    {
        private NativeList<GPUInstanceComponentDesc> m_Components;

        private MetadataValue CreateMetadataValue(int nameID, int gpuAddress, bool isOverridden)
        {
            const uint kIsOverriddenBit = 0x80000000;
            return new MetadataValue
            {
                NameID = nameID,
                Value = (uint)gpuAddress | (isOverridden ? kIsOverriddenBit : 0),
            };
        }

        public void AddComponent<T>(int propertyID, bool isOverriden, bool isPerInstance) where T : unmanaged
        {
            AddComponent(propertyID, isOverriden, UnsafeUtility.SizeOf<T>(), isPerInstance);
        }

        public void AddComponent(int propertyID, bool isOverriden, int byteSize, bool isPerInstance)
        {
            if (!m_Components.IsCreated)
                m_Components = new NativeList<GPUInstanceComponentDesc>(8, Allocator.Temp);

            m_Components.Add(new GPUInstanceComponentDesc(propertyID, byteSize, isOverriden, isPerInstance));
        }

        public unsafe GPUInstanceDataBuffer Build(int maxInstanceCount)
        {
            int perInstanceComponentCounts = 0;
            var perInstanceComponentIndices = new NativeArray<int>(m_Components.Length, Allocator.Temp);
            var componentAddresses = new NativeArray<int>(m_Components.Length, Allocator.Temp);
            var componentByteSizes = new NativeArray<int>(m_Components.Length, Allocator.Temp);
            var componentInstanceIndexRanges = new NativeArray<Vector2Int>(m_Components.Length, Allocator.Temp);

            GPUInstanceDataBuffer newBuffer = new GPUInstanceDataBuffer();
            newBuffer.instanceCount = maxInstanceCount;
            newBuffer.instancePerWindow = maxInstanceCount;
            newBuffer.windowCount = 1;
            newBuffer.layoutVersion = GPUInstanceDataBuffer.NextVersion();
            newBuffer.version = 0;
            newBuffer.defaultMetadata = new NativeArray<MetadataValue>(m_Components.Length, Allocator.Persistent);
            newBuffer.descriptions = new NativeArray<GPUInstanceComponentDesc>(m_Components.Length, Allocator.Persistent);
            newBuffer.nameToMetadataMap = new NativeParallelHashMap<int, int>(m_Components.Length, Allocator.Persistent);
            newBuffer.gpuBufferComponentAddress = new NativeArray<int>(m_Components.Length, Allocator.Persistent);


            if (GPUInstanceDataBuffer.IsUBO)
            {
                int byteSizePerInstance = 0;
                foreach (var componentDesc in m_Components)
                    byteSizePerInstance += componentDesc.byteSize;
                
                Assert.IsTrue(byteSizePerInstance != 0);
                newBuffer.instancePerWindow = GPUInstanceDataBuffer.byteSizePerWindow / byteSizePerInstance;
                newBuffer.windowCount = (newBuffer.instanceCount + newBuffer.instancePerWindow - 1) / newBuffer.instancePerWindow;
            }

            //Initial offset, must be 0, 0, 0, 0. Why?
            // int vec4Size = UnsafeUtility.SizeOf<Vector4>();
            int byteOffset = 0; // 4 * vec4Size;
            for (int c = 0; c < m_Components.Length; ++c)
            {
                var componentDesc = m_Components[c];
                newBuffer.descriptions[c] = componentDesc;

                int instancesBegin = 0;
                int instancesEnd = instancesBegin + newBuffer.instancePerWindow;
                int instancesNum = componentDesc.isPerInstance ? instancesEnd - instancesBegin : 1;
                Assert.IsTrue(instancesNum >= 0);
                Assert.IsTrue(componentDesc.isPerInstance);

                componentInstanceIndexRanges[c] = new Vector2Int(instancesBegin, instancesBegin + instancesNum);

                int componentGPUAddress = byteOffset - instancesBegin * componentDesc.byteSize;
                Assert.IsTrue(componentGPUAddress >= 0, "GPUInstanceDataBufferBuilder: GPU address is negative. This is not supported for now. See kIsOverriddenBit." +
                    "In general, if there is only one root InstanceType (MeshRenderer in our case) with a component that is larger or equal in size than any component in a derived InstanceType." +
                    "And the number of parent gpu instances are always larger or equal to the number of derived type gpu instances. Than GPU address cannot become negative.");

                newBuffer.gpuBufferComponentAddress[c] = componentGPUAddress;
                newBuffer.defaultMetadata[c] = CreateMetadataValue(componentDesc.propertyID, componentGPUAddress, componentDesc.isOverriden);

                componentAddresses[c] = componentGPUAddress;
                componentByteSizes[c] = componentDesc.byteSize;

                int componentByteSize = componentDesc.byteSize * instancesNum;
                byteOffset += componentByteSize;

                bool addedToMap = newBuffer.nameToMetadataMap.TryAdd(componentDesc.propertyID, c); 
                Assert.IsTrue(addedToMap, "Repetitive metadata element added to object.");

                if (componentDesc.isPerInstance)
                {
                    perInstanceComponentIndices[perInstanceComponentCounts] = c;
                    perInstanceComponentCounts++;
                }
            }

            int stride = UnsafeUtility.SizeOf<float4>();
            newBuffer.byteSize = GPUInstanceDataBuffer.IsUBO ? GPUInstanceDataBuffer.byteSizePerWindow * newBuffer.windowCount : byteOffset;
            newBuffer.nativeBuffer = new NativeArray<float4>((newBuffer.byteSize + stride - 1)/ stride, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            newBuffer.nativeBuffer[0] = float4.zero;
            newBuffer.gpuBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (newBuffer.byteSize + stride - 1)/ stride, stride);
            newBuffer.perInstanceComponentCount = perInstanceComponentCounts;

            perInstanceComponentIndices.Dispose();
            componentAddresses.Dispose();
            componentByteSizes.Dispose();

            return newBuffer;
        }

        public void Dispose()
        {
            if (m_Components.IsCreated)
                m_Components.Dispose();
        }
    }

    internal struct GPUInstanceDataBufferGrower : IDisposable
    {
        private GPUInstanceDataBuffer m_SrcBuffer;
        private GPUInstanceDataBuffer m_DstBuffer;

        //@ We should implement buffer shrinker too, otherwise lots of instances can be allocated for trees for example
        //@ while there are no trees in scenes that are in use at all.
        public unsafe GPUInstanceDataBufferGrower(GPUInstanceDataBuffer sourceBuffer, int instanceCount)
        {
            m_SrcBuffer = sourceBuffer;
            m_DstBuffer = null;

            bool needToGrow = false;

            Assert.IsTrue(instanceCount >= sourceBuffer.instanceCount, "Shrinking GPU instance buffer is not supported yet.");

            if (instanceCount > sourceBuffer.instanceCount)
                needToGrow = true;

            if (!needToGrow)
                return;

            GPUInstanceDataBufferBuilder builder = new GPUInstanceDataBufferBuilder();

            foreach (GPUInstanceComponentDesc descriptor in sourceBuffer.descriptions)
                builder.AddComponent(descriptor.propertyID, descriptor.isOverriden, descriptor.byteSize, descriptor.isPerInstance);

            m_DstBuffer = builder.Build(instanceCount);
            builder.Dispose();
        }

        /// <summary>
        /// CPU Copy
        /// </summary>
        /// <returns></returns>
        public GPUInstanceDataBuffer SubmitToCpu()
        {
            if (m_DstBuffer == null)
                return m_SrcBuffer;

            int instanceCount = m_SrcBuffer.instanceCount;

            if(instanceCount == 0)
                return m_DstBuffer;

            Assert.IsTrue(m_SrcBuffer.perInstanceComponentCount == m_DstBuffer.perInstanceComponentCount);
            
            m_DstBuffer.nativeBuffer.GetSubArray(0, m_SrcBuffer.nativeBuffer.Length).CopyFrom(m_SrcBuffer.nativeBuffer);
            return m_DstBuffer;
        }

        public void Dispose()
        {
        }
    }
}
