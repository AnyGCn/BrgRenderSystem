﻿using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace BrgRenderSystem
{
    internal partial class InstanceDataSystem : IDisposable
    {
        private InstanceAllocators m_InstanceAllocators;
        private CPUSharedInstanceData m_SharedInstanceData;
        private CPUInstanceData m_InstanceData;

        //@ We may want something a bit faster instead of multi hash map. Remove and search performance for multiple instances per renderer group is not great.
        private NativeParallelMultiHashMap<int, InstanceHandle> m_RendererGroupInstanceMultiHash;

        private ComputeBuffer m_UpdateIndexQueueBuffer;

        private ComputeBuffer m_ProbeUpdateDataQueueBuffer;
        private ComputeBuffer m_ProbeOcclusionUpdateDataQueueBuffer;

        private ComputeBuffer m_TransformUpdateDataQueueBuffer;
        private ComputeBuffer m_BoundingSpheresUpdateDataQueueBuffer;

        private bool m_EnableBoundingSpheres;

        public bool hasBoundingSpheres
        {
            get { return m_EnableBoundingSpheres; }
        }

        public CPUInstanceData.ReadOnly instanceData
        {
            get { return m_InstanceData.AsReadOnly(); }
        }

        public CPUSharedInstanceData.ReadOnly sharedInstanceData
        {
            get { return m_SharedInstanceData.AsReadOnly(); }
        }

        public NativeArray<InstanceHandle> aliveInstances
        {
            get { return m_InstanceData.instances.GetSubArray(0, m_InstanceData.instancesLength); }
        }
        public InstanceDataSystem(int maxInstances, bool enableBoundingSpheres)
        {
            m_InstanceAllocators = new InstanceAllocators();
            m_SharedInstanceData = new CPUSharedInstanceData();
            m_InstanceData = new CPUInstanceData();

            m_InstanceAllocators.Initialize();
            m_SharedInstanceData.Initialize(maxInstances);
            m_InstanceData.Initialize(maxInstances);

            m_RendererGroupInstanceMultiHash = new NativeParallelMultiHashMap<int, InstanceHandle>(maxInstances, Allocator.Persistent);

            m_EnableBoundingSpheres = enableBoundingSpheres;
        }

        public void Dispose()
        {
            m_InstanceAllocators.Dispose();
            m_SharedInstanceData.Dispose();
            m_InstanceData.Dispose();

            m_RendererGroupInstanceMultiHash.Dispose();

            m_UpdateIndexQueueBuffer?.Dispose();
            m_ProbeUpdateDataQueueBuffer?.Dispose();
            m_ProbeOcclusionUpdateDataQueueBuffer?.Dispose();
            m_TransformUpdateDataQueueBuffer?.Dispose();
            m_BoundingSpheresUpdateDataQueueBuffer?.Dispose();
        }
        
        public void UpdatePerFrameInstanceVisibility(in ParallelBitArray compactedVisibilityMasks)
        {
            Assert.AreEqual(m_InstanceData.handlesLength, compactedVisibilityMasks.Length);

            var updateCompactedInstanceVisibilityJob = new UpdateCompactedInstanceVisibilityJob
            {
                instanceData = m_InstanceData,
                compactedVisibilityMasks = compactedVisibilityMasks
            };

            updateCompactedInstanceVisibilityJob.ScheduleBatch(m_InstanceData.instancesLength, UpdateCompactedInstanceVisibilityJob.k_BatchSize).Complete();
        }

        // Simple, Main thread api.
        // public void AddDirect(RendererGroupItem item, out InstanceHandle instanceHandle, out SharedInstanceHandle sharedInstanceHandle)
        // {
        //     instanceHandle = m_InstanceAllocators.AllocateInstance();
        //     sharedInstanceHandle = m_InstanceAllocators.AllocateSharedInstance();
        //     m_InstanceData.Add(instanceHandle);
        //     m_SharedInstanceData.Add(sharedInstanceHandle);
        //     UpdateDirect(item, instanceHandle, sharedInstanceHandle);
        // }
        //
        // public void UpdateDirect(RendererGroupItem item, in InstanceHandle instanceHandle, in SharedInstanceHandle sharedInstanceHandle)
        // {
        //     Assert.IsTrue(m_InstanceData.IsValidInstance(instanceHandle) && m_SharedInstanceData.IsValidInstance(sharedInstanceHandle));
        //     var materialsID = new SmallIntegerArray(item.materials.Count, Allocator.Persistent);
        //     for (int i = 0; i < item.materials.Count; ++i)
        //         materialsID[i] = item.materials[i];
        //     m_SharedInstanceData.Set(sharedInstanceHandle, sharedInstanceHandle.index, new SmallIntegerArray(),
        //         item.mesh, item.localBounds.ToAABB(), TransformUpdateFlags.IsPartOfStaticBatch, InstanceFlags.None,
        //         0xFFFFFFFF, item.gameObjectLayer, 1);
        //     m_InstanceData.Set(instanceHandle, sharedInstanceHandle, false,
        //         AABB.Transform(item.localToWorldMatrix, m_SharedInstanceData.Get_LocalAABB(sharedInstanceHandle)), 0,
        //         false, false, false);
        // }

        public void RemoveDirect(in InstanceHandle instanceHandle, in SharedInstanceHandle sharedInstanceHandle)
        {
            Assert.IsTrue(m_InstanceData.IsValidInstance(instanceHandle) && m_SharedInstanceData.IsValidInstance(sharedInstanceHandle));
            m_InstanceAllocators.FreeInstance(instanceHandle);
            m_InstanceAllocators.FreeSharedInstance(sharedInstanceHandle);
            m_InstanceData.Remove(instanceHandle);
            m_SharedInstanceData.Remove(sharedInstanceHandle);
        }

        public JobHandle ScheduleQueryRendererGroupInstancesJob(NativeArray<int>.ReadOnly rendererGroupIDs, NativeArray<InstanceHandle> instances)
        {
            Assert.AreEqual(rendererGroupIDs.Length, instances.Length);

            if (instances.Length == 0)
                return default;

            var queryJob = new QueryRendererGroupInstancesJob()
            {
                rendererGroupInstanceMultiHash = m_RendererGroupInstanceMultiHash,
                rendererGroupIDs = rendererGroupIDs,
                instances = instances
            };

            return queryJob.ScheduleBatch(rendererGroupIDs.Length, QueryRendererGroupInstancesJob.k_BatchSize);
        }
        
        public JobHandle ScheduleQueryRendererGroupInstancesJob(NativeArray<int>.ReadOnly rendererGroupIDs, NativeList<InstanceHandle> instances)
        {
            if (rendererGroupIDs.Length == 0)
                return default;

            var instancesOffset = new NativeArray<int>(rendererGroupIDs.Length, Allocator.TempJob);
            var instancesCount = new NativeArray<int>(rendererGroupIDs.Length, Allocator.TempJob);

            var jobHandle = ScheduleQueryRendererGroupInstancesJob(rendererGroupIDs, instancesOffset, instancesCount, instances);

            instancesOffset.Dispose(jobHandle);
            instancesCount.Dispose(jobHandle);

            return jobHandle;
        }
        
        public JobHandle ScheduleQueryRendererGroupInstancesJob(NativeArray<int>.ReadOnly rendererGroupIDs, NativeArray<int> instancesOffset, NativeArray<int> instancesCount, NativeList<InstanceHandle> instances)
        {
            Assert.AreEqual(rendererGroupIDs.Length, instancesOffset.Length);
            Assert.AreEqual(rendererGroupIDs.Length, instancesCount.Length);

            if (rendererGroupIDs.Length == 0)
                return default;

            var queryCountJobHandle = new QueryRendererGroupInstancesCountJob
            {
                instanceData = m_InstanceData,
                sharedInstanceData = m_SharedInstanceData,
                rendererGroupInstanceMultiHash = m_RendererGroupInstanceMultiHash,
                rendererGroupIDs = rendererGroupIDs,
                instancesCount = instancesCount,
            }.ScheduleBatch(rendererGroupIDs.Length, QueryRendererGroupInstancesCountJob.k_BatchSize);

            var computeOffsetsAndResizeArrayJobHandle = new ComputeInstancesOffsetAndResizeInstancesArrayJob
            {
                instancesCount = instancesCount,
                instancesOffset = instancesOffset,
                instances = instances
            }.Schedule(queryCountJobHandle);

            return new QueryRendererGroupInstancesMultiJob()
            {
                rendererGroupInstanceMultiHash = m_RendererGroupInstanceMultiHash,
                rendererGroupIDs = rendererGroupIDs,
                instancesOffsets = instancesOffset.AsReadOnly(),
                instancesCounts = instancesCount.AsReadOnly(),
                instances = instances.AsDeferredJobArray()
            }.ScheduleBatch(rendererGroupIDs.Length, QueryRendererGroupInstancesMultiJob.k_BatchSize, computeOffsetsAndResizeArrayJobHandle);
        }

        public void FreeRendererGroupInstances(NativeArray<int>.ReadOnly rendererGroupsID)
        {
            new FreeRendererGroupInstancesJob { rendererGroupInstanceMultiHash = m_RendererGroupInstanceMultiHash,
                instanceAllocators = m_InstanceAllocators, sharedInstanceData = m_SharedInstanceData, instanceData = m_InstanceData,
                rendererGroupsID = rendererGroupsID }.Run();
        }

        public int GetMaxInstancesOfType()
        {
            return m_InstanceAllocators.GetInstanceHandlesLength();
        }

        public JobHandle ScheduleUpdateInstanceDataJob(NativeArray<InstanceHandle> instances, RendererGroupInputData.ReadOnly rendererData, NativeParallelHashMap<int, InstanceHandle> lodGroupDataMap)
        {
            // bool implicitInstanceIndices = rendererData.instancesCount.Length == 0;

            // if(implicitInstanceIndices)
            {
                Assert.AreEqual(instances.Length, rendererData.rendererGroupID.Length);
            }
            // else
            // {
            //     Assert.AreEqual(rendererData.instancesCount.Length, rendererData.rendererGroupID.Length);
            //     Assert.AreEqual(rendererData.instancesOffset.Length, rendererData.rendererGroupID.Length);
            // }

            Assert.AreEqual(instances.Length, rendererData.localToWorldMatrix.Length);

            return new UpdateRendererInstancesJob
            {
                // implicitInstanceIndices = implicitInstanceIndices,
                instances = instances,
                rendererData = rendererData,
                lodGroupDataMap = lodGroupDataMap,
                instanceData = m_InstanceData,
                sharedInstanceData = m_SharedInstanceData
            }.Schedule(rendererData.rendererGroupID.Length, UpdateRendererInstancesJob.k_BatchSize);
        }

        public unsafe void ReallocateAndGetInstances(RendererGroupInputData.ReadOnly rendererData, NativeArray<InstanceHandle> instances)
        {
            Assert.AreEqual(rendererData.localToWorldMatrix.Length, instances.Length);

            int newSharedInstancesCount = 0;
            int newInstancesCount = 0;

            // bool implicitInstanceIndices = rendererData.instancesCount.Length == 0;

            // if (implicitInstanceIndices)
            {
                var queryJob = new QueryRendererGroupInstancesJob()
                {
                    rendererGroupInstanceMultiHash = m_RendererGroupInstanceMultiHash,
                    rendererGroupIDs = rendererData.rendererGroupID,
                    instances = instances,
                    atomicNonFoundInstancesCount = new UnsafeAtomicCounter32(&newInstancesCount)
                };

                queryJob.ScheduleBatch(rendererData.rendererGroupID.Length, QueryRendererGroupInstancesJob.k_BatchSize).Complete();

                newSharedInstancesCount = newInstancesCount;
            }
            // else
            // {
            //     var queryJob = new QueryRendererGroupInstancesMultiJob()
            //     {
            //         rendererGroupInstanceMultiHash = m_RendererGroupInstanceMultiHash,
            //         rendererGroupIDs = rendererData.rendererGroupID,
            //         instancesOffsets = rendererData.instancesOffset,
            //         instancesCounts = rendererData.instancesCount,
            //         instances = instances,
            //         atomicNonFoundSharedInstancesCount = new UnsafeAtomicCounter32(&newSharedInstancesCount),
            //         atomicNonFoundInstancesCount = new UnsafeAtomicCounter32(&newInstancesCount)
            //     };
            //
            //     queryJob.ScheduleBatch(rendererData.rendererGroupID.Length, QueryRendererGroupInstancesMultiJob.k_BatchSize).Complete();
            // }

            m_InstanceData.EnsureFreeInstances(newInstancesCount);
            m_SharedInstanceData.EnsureFreeInstances(newSharedInstancesCount);

            new ReallocateInstancesJob { rendererGroupInstanceMultiHash = m_RendererGroupInstanceMultiHash,
                instanceAllocators = m_InstanceAllocators, sharedInstanceData = m_SharedInstanceData, instanceData = m_InstanceData,
                rendererGroupIDs = rendererData.rendererGroupID, packedRendererData = rendererData.packedRendererData, instances = instances }.Run();
        }

        public void InitializeInstanceTransforms(NativeArray<InstanceHandle> instances, NativeArray<Matrix4x4>.ReadOnly localToWorldMatrices,
            NativeArray<Matrix4x4>.ReadOnly prevLocalToWorldMatrices)
        {
            if (instances.Length == 0)
                return;

            UpdateInstanceTransformsData(true, instances, localToWorldMatrices, prevLocalToWorldMatrices);
        }

        private unsafe void UpdateInstanceTransformsData(bool initialize, NativeArray<InstanceHandle> instances, NativeArray<Matrix4x4>.ReadOnly localToWorldMatrices, NativeArray<Matrix4x4>.ReadOnly prevLocalToWorldMatrices)
        {
            Assert.AreEqual(instances.Length, localToWorldMatrices.Length);
            Assert.AreEqual(instances.Length, prevLocalToWorldMatrices.Length);

            new TransformUpdateJob()
            {
                instances = instances,
                localToWorldMatrices = localToWorldMatrices,
                sharedInstanceData = m_SharedInstanceData,
                instanceData = m_InstanceData,
            }.Schedule(instances.Length, TransformUpdateJob.k_BatchSize).Complete();
        }

        public void UpdateGpuInstanceBufferData(NativeArray<InstanceHandle> instances, GPUInstanceDataBuffer instanceDataBuffer, in RenderersParameters renderersParameters)
        {
            if (GPUInstanceDataBuffer.IsUBO)
            {
                NativeList<int> rangesList = new NativeList<int>(128, Allocator.TempJob);
                new PrepareRebuildGPUInstanceIndexJob()
                {
                    rangesList = rangesList,
                    instanceData = m_InstanceData,
                    sharedInstanceData = m_SharedInstanceData,
                }.Run();
                
                new UpdateGPUInstanceIndexAndData()
                {
                    instanceData = m_InstanceData,
                    rangesList = rangesList,
                    instancePerWindow = instanceDataBuffer.instancePerWindow,
                    byteSizePerWindow = GPUInstanceDataBuffer.byteSizePerWindow,
                    localToWorldMatrixOffset = renderersParameters.localToWorld.gpuAddress,
                    worldToLocalMatrixOffset = renderersParameters.worldToLocal.gpuAddress,
                    outputBuffer = instanceDataBuffer.nativeBuffer,
                }.Schedule(m_InstanceData.instancesLength, UpdateGPUInstanceData.k_BatchSize).Complete();
                
                rangesList.Dispose();
            }
            else
            {
                new UpdateGPUInstanceData()
                {
                    instanceData = m_InstanceData,
                    instances = instances,
                    localToWorldMatrixOffset = renderersParameters.localToWorld.gpuAddress,
                    worldToLocalMatrixOffset = renderersParameters.worldToLocal.gpuAddress,
                    outputBuffer = instanceDataBuffer.nativeBuffer,
                }.Schedule(instances.Length, UpdateGPUInstanceData.k_BatchSize).Complete();
            }
            
            instanceDataBuffer.SubmitToGpu();
        }
    }
}