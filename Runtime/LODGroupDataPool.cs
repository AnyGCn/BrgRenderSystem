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

namespace BrgRenderSystem
{
    internal struct LODGroupInputData
    {
        public NativeList<int> lodGroupID;
        public NativeList<int> lodOffset;
        public NativeList<int> lodCount;
        public NativeList<LODFadeMode> fadeMode;
        public NativeList<Vector3> worldSpaceReferencePoint;
        public NativeList<float> worldSpaceSize;
        public NativeList<short> renderersCount;
        public NativeList<bool> lastLODIsBillboard;
        public NativeList<int> invalidLODGroupID;
        public NativeList<short> lodRenderersCount;
        public NativeList<float> lodScreenRelativeTransitionHeight;
        public NativeList<float> lodFadeTransitionWidth;

        public void Initialize(int initCapacity)
        {
            lodGroupID = new NativeList<int>(initCapacity, Allocator.Persistent);
            lodOffset = new NativeList<int>(initCapacity, Allocator.Persistent);
            lodCount = new NativeList<int>(initCapacity, Allocator.Persistent);
            fadeMode = new NativeList<LODFadeMode>(initCapacity, Allocator.Persistent);
            worldSpaceReferencePoint = new NativeList<Vector3>(initCapacity, Allocator.Persistent);
            worldSpaceSize = new NativeList<float>(initCapacity, Allocator.Persistent);
            renderersCount = new NativeList<short>(initCapacity, Allocator.Persistent);
            lastLODIsBillboard = new NativeList<bool>(initCapacity, Allocator.Persistent);
            invalidLODGroupID = new NativeList<int>(initCapacity, Allocator.Persistent);
            lodRenderersCount = new NativeList<short>(initCapacity, Allocator.Persistent);
            lodScreenRelativeTransitionHeight = new NativeList<float>(initCapacity, Allocator.Persistent);
            lodFadeTransitionWidth = new NativeList<float>(initCapacity, Allocator.Persistent);
        }

        public void Clear()
        {
            lodGroupID.Clear();
            lodOffset.Clear();
            lodCount.Clear();
            fadeMode.Clear();
            worldSpaceReferencePoint.Clear();
            worldSpaceSize.Clear();
            renderersCount.Clear();
            lastLODIsBillboard.Clear();
            invalidLODGroupID.Clear();
            lodRenderersCount.Clear();
            lodScreenRelativeTransitionHeight.Clear();
            lodFadeTransitionWidth.Clear();
        }
        
        public void Dispose()
        {
            lodGroupID.Dispose();
            lodOffset.Dispose();
            lodCount.Dispose();
            fadeMode.Dispose();
            worldSpaceReferencePoint.Dispose();
            worldSpaceSize.Dispose();
            renderersCount.Dispose();
            lastLODIsBillboard.Dispose();
            invalidLODGroupID.Dispose();
            lodRenderersCount.Dispose();
            lodScreenRelativeTransitionHeight.Dispose();
            lodFadeTransitionWidth.Dispose();
        }

        public ReadOnly AsReadOnly()
        {
            return new ReadOnly(this);
        }
        
        public struct ReadOnly
        {
            public NativeArray<int>.ReadOnly lodGroupID;
            public NativeArray<int>.ReadOnly lodOffset;
            public NativeArray<int>.ReadOnly lodCount;
            public NativeArray<LODFadeMode>.ReadOnly fadeMode;
            public NativeArray<Vector3>.ReadOnly worldSpaceReferencePoint;
            public NativeArray<float>.ReadOnly worldSpaceSize;
            public NativeArray<short>.ReadOnly renderersCount;
            public NativeArray<bool>.ReadOnly lastLODIsBillboard;
            public NativeArray<int>.ReadOnly invalidLODGroupID;
            public NativeArray<short>.ReadOnly lodRenderersCount;
            public NativeArray<float>.ReadOnly lodScreenRelativeTransitionHeight;
            public NativeArray<float>.ReadOnly lodFadeTransitionWidth;

            public ReadOnly(LODGroupInputData inputData)
            {
                lodGroupID = inputData.lodGroupID.AsArray().AsReadOnly();
                lodOffset = inputData.lodOffset.AsArray().AsReadOnly();
                lodCount = inputData.lodCount.AsArray().AsReadOnly();
                fadeMode = inputData.fadeMode.AsArray().AsReadOnly();
                worldSpaceReferencePoint = inputData.worldSpaceReferencePoint.AsArray().AsReadOnly();
                worldSpaceSize = inputData.worldSpaceSize.AsArray().AsReadOnly();
                renderersCount = inputData.renderersCount.AsArray().AsReadOnly();
                lastLODIsBillboard = inputData.lastLODIsBillboard.AsArray().AsReadOnly();
                invalidLODGroupID = inputData.invalidLODGroupID.AsArray().AsReadOnly();
                lodRenderersCount = inputData.lodRenderersCount.AsArray().AsReadOnly();
                lodScreenRelativeTransitionHeight = inputData.lodScreenRelativeTransitionHeight.AsArray().AsReadOnly();
                lodFadeTransitionWidth = inputData.lodFadeTransitionWidth.AsArray().AsReadOnly();
            }
        }
    }
    
    internal unsafe struct LODGroupData
    {
        public const int k_MaxLODLevelsCount = 8;

        public bool valid;
        public int lodCount;
        public int rendererCount;
        public fixed float screenRelativeTransitionHeights[k_MaxLODLevelsCount];
        public fixed float fadeTransitionWidth[k_MaxLODLevelsCount];
    }

    internal unsafe struct LODGroupCullingData
    {
        public float3 worldSpaceReferencePoint;
        public int lodCount;
        public fixed float sqrDistances[LODGroupData.k_MaxLODLevelsCount]; // we use square distance to get rid of a sqrt in gpu culling..
        public fixed float transitionDistances[LODGroupData.k_MaxLODLevelsCount]; // todo - make this a separate data struct (CPUOnly, as we do not support dithering on GPU..)
        public float worldSpaceSize;// SpeedTree crossfade.
        public fixed bool percentageFlags[LODGroupData.k_MaxLODLevelsCount];// SpeedTree crossfade.
    }

    [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct UpdateLODGroupTransformJob : IJobParallelFor
    {
        public const int k_BatchSize = 256;

        [ReadOnly] public NativeParallelHashMap<int, InstanceHandle> lodGroupDataHash;
        [ReadOnly] public NativeArray<int>.ReadOnly lodGroupIDs;
        [ReadOnly] public NativeArray<Vector3>.ReadOnly worldSpaceReferencePoints;
        [ReadOnly] public NativeArray<float>.ReadOnly worldSpaceSizes;
        [ReadOnly] public bool requiresGPUUpload;
        [ReadOnly] public bool supportDitheringCrossFade;

        [NativeDisableContainerSafetyRestriction, NoAlias, ReadOnly] public NativeList<LODGroupData> lodGroupData;

        [NativeDisableContainerSafetyRestriction, NoAlias, WriteOnly] public NativeList<LODGroupCullingData> lodGroupCullingData;

        [NativeDisableUnsafePtrRestriction] public UnsafeAtomicCounter32 atomicUpdateCount;

        public unsafe void Execute(int index)
        {
            int lodGroupID = lodGroupIDs[index];

            if (lodGroupDataHash.TryGetValue(lodGroupID, out var lodGroupInstance))
            {
                var worldSpaceSize = worldSpaceSizes[index];

                LODGroupData* lodGroup = (LODGroupData*)lodGroupData.GetUnsafePtr() + lodGroupInstance.index;
                LODGroupCullingData* lodGroupTransformResult = (LODGroupCullingData*)lodGroupCullingData.GetUnsafePtr() + lodGroupInstance.index;
                lodGroupTransformResult->worldSpaceSize = worldSpaceSize;
                lodGroupTransformResult->worldSpaceReferencePoint = worldSpaceReferencePoints[index];

                for (int i = 0; i < lodGroup->lodCount; ++i)
                {
                    float lodHeight = lodGroup->screenRelativeTransitionHeights[i];

                    var lodDist = LODGroupRenderingUtils.CalculateLODDistance(lodHeight, worldSpaceSize);
                    lodGroupTransformResult->sqrDistances[i] = lodDist * lodDist;

                    if (supportDitheringCrossFade && !lodGroupTransformResult->percentageFlags[i])
                    {
                        float prevLODHeight = i != 0 ? lodGroup->screenRelativeTransitionHeights[i - 1] : 1.0f;
                        float transitionHeight = lodHeight + lodGroup->fadeTransitionWidth[i] * (prevLODHeight - lodHeight);
                        var transitionDistance = lodDist - LODGroupRenderingUtils.CalculateLODDistance(transitionHeight, worldSpaceSize);
                        transitionDistance = Mathf.Max(0.0f, transitionDistance);
                        lodGroupTransformResult->transitionDistances[i] = transitionDistance;
                    }
                    else
                    {
                        lodGroupTransformResult->transitionDistances[i] = 0f;
                    }

                }
            }
        }
    }

    [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    internal unsafe struct AllocateOrGetLODGroupDataInstancesJob : IJob
    {
        [ReadOnly] public NativeArray<int>.ReadOnly lodGroupsID;

        public NativeList<LODGroupData> lodGroupsData;
        public NativeList<LODGroupCullingData> lodGroupCullingData;
        public NativeParallelHashMap<int, InstanceHandle> lodGroupDataHash;
        public NativeList<InstanceHandle> freeLODGroupDataHandles;

        [WriteOnly] public NativeArray<InstanceHandle> lodGroupInstances;

        [NativeDisableUnsafePtrRestriction] public int* previousRendererCount;

        public void Execute()
        {
            int freeHandlesCount = freeLODGroupDataHandles.Length;
            int lodDataLength = lodGroupsData.Length;

            for (int i = 0; i < lodGroupsID.Length; ++i)
            {
                int lodGroupID = lodGroupsID[i];

                if (!lodGroupDataHash.TryGetValue(lodGroupID, out var lodGroupInstance))
                {
                    if (freeHandlesCount == 0)
                        lodGroupInstance = InstanceHandle.Create(lodDataLength++);
                    else
                        lodGroupInstance = freeLODGroupDataHandles[--freeHandlesCount];

                    lodGroupDataHash.TryAdd(lodGroupID, lodGroupInstance);
                }
                else
                {
                    *previousRendererCount += lodGroupsData.ElementAt(lodGroupInstance.index).rendererCount;
                }

                lodGroupInstances[i] = lodGroupInstance;
            }

            freeLODGroupDataHandles.ResizeUninitialized(freeHandlesCount);
            lodGroupsData.ResizeUninitialized(lodDataLength);
            lodGroupCullingData.ResizeUninitialized(lodDataLength);
        }
    }

    [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    internal unsafe struct UpdateLODGroupDataJob : IJobParallelFor
    {
        public const int k_BatchSize = 256;

        [ReadOnly] public NativeArray<InstanceHandle> lodGroupInstances;
        [ReadOnly] public LODGroupInputData.ReadOnly inputData;
        [ReadOnly] public bool supportDitheringCrossFade;

        public NativeArray<LODGroupData> lodGroupsData;
        public NativeArray<LODGroupCullingData> lodGroupsCullingData;

        [NativeDisableUnsafePtrRestriction] public UnsafeAtomicCounter32 rendererCount;

        public void Execute(int index)
        {
            var lodGroupInstance = lodGroupInstances[index];
            var fadeMode = inputData.fadeMode[index];
            var lodOffset = inputData.lodOffset[index];
            var lodCount = inputData.lodCount[index];
            var renderersCount = inputData.renderersCount[index];
            var worldReferencePoint = inputData.worldSpaceReferencePoint[index];
            var worldSpaceSize = inputData.worldSpaceSize[index];
            var lastLODIsBillboard = inputData.lastLODIsBillboard[index];
            var useDitheringCrossFade = fadeMode != LODFadeMode.None && supportDitheringCrossFade;
            var useSpeedTreeCrossFade = fadeMode == LODFadeMode.SpeedTree;

            LODGroupData* lodGroupData = (LODGroupData*)lodGroupsData.GetUnsafePtr() + lodGroupInstance.index;
            LODGroupCullingData* lodGroupCullingData = (LODGroupCullingData*)lodGroupsCullingData.GetUnsafePtr() + lodGroupInstance.index;

            lodGroupData->valid = true;
            lodGroupData->lodCount = lodCount;
            lodGroupData->rendererCount = useDitheringCrossFade ? renderersCount : 0;
            lodGroupCullingData->worldSpaceSize = worldSpaceSize;
            lodGroupCullingData->worldSpaceReferencePoint = worldReferencePoint;
            lodGroupCullingData->lodCount = lodCount;

            rendererCount.Add(lodGroupData->rendererCount);

            var crossFadeLODBegin = 0;

            if (useSpeedTreeCrossFade)
            {
                var lastLODIndex = lodOffset + (lodCount - 1);
                var hasBillboardLOD = lodCount > 0 && inputData.lodRenderersCount[lastLODIndex] == 1 && lastLODIsBillboard;
            
                if (lodCount == 0)
                    crossFadeLODBegin = 0;
                else if (hasBillboardLOD)
                    crossFadeLODBegin = Math.Max(lodCount, 2) - 2;
                else
                    crossFadeLODBegin = lodCount - 1;
            }

            for (int i = 0; i < lodCount; ++i)
            {
                var lodIndex = lodOffset + i;
                var lodHeight = inputData.lodScreenRelativeTransitionHeight[lodIndex];
                var lodDist = LODGroupRenderingUtils.CalculateLODDistance(lodHeight, worldSpaceSize);

                lodGroupData->screenRelativeTransitionHeights[i] = lodHeight;
                lodGroupData->fadeTransitionWidth[i] = 0.0f;
                lodGroupCullingData->sqrDistances[i] = lodDist * lodDist;
                lodGroupCullingData->percentageFlags[i] = false;
                lodGroupCullingData->transitionDistances[i] = 0.0f;

                if (useSpeedTreeCrossFade && i < crossFadeLODBegin)
                {
                    lodGroupCullingData->percentageFlags[i] = true;
                }
                else if (useDitheringCrossFade && i >= crossFadeLODBegin)
                {
                    var fadeTransitionWidth = inputData.lodFadeTransitionWidth[lodIndex];
                    var prevLODHeight = i != 0 ? inputData.lodScreenRelativeTransitionHeight[lodIndex - 1] : 1.0f;
                    var transitionHeight = lodHeight + fadeTransitionWidth * (prevLODHeight - lodHeight);
                    var transitionDistance = lodDist - LODGroupRenderingUtils.CalculateLODDistance(transitionHeight, worldSpaceSize);
                    transitionDistance = Mathf.Max(0.0f, transitionDistance);

                    lodGroupData->fadeTransitionWidth[i] = fadeTransitionWidth;
                    lodGroupCullingData->transitionDistances[i] = transitionDistance;
                }
            }
        }
    }

    [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    internal unsafe struct FreeLODGroupDataJob : IJob
    {
        [ReadOnly] public NativeArray<int>.ReadOnly destroyedLODGroupsID;

        public NativeList<LODGroupData> lodGroupsData;
        public NativeParallelHashMap<int, InstanceHandle> lodGroupDataHash;
        public NativeList<InstanceHandle> freeLODGroupDataHandles;

        [NativeDisableUnsafePtrRestriction] public int* removedRendererCount;

        public void Execute()
        {
            foreach (int lodGroupID in destroyedLODGroupsID)
            {
                if (lodGroupDataHash.TryGetValue(lodGroupID, out var lodGroupInstance))
                {
                    Assert.IsTrue(lodGroupInstance.valid);

                    lodGroupDataHash.Remove(lodGroupID);
                    freeLODGroupDataHandles.Add(lodGroupInstance);

                    ref LODGroupData lodGroupData = ref lodGroupsData.ElementAt(lodGroupInstance.index);
                    Assert.IsTrue(lodGroupData.valid);

                    *removedRendererCount += lodGroupData.rendererCount;
                    lodGroupData.valid = false;
                }
            }
        }
    }

    internal class LODGroupDataPool : IDisposable
    {
        private NativeList<LODGroupData> m_LODGroupData;
        private NativeParallelHashMap<int, InstanceHandle> m_LODGroupDataHash;
        public NativeParallelHashMap<int, InstanceHandle> lodGroupDataHash => m_LODGroupDataHash;

        private NativeList<LODGroupCullingData> m_LODGroupCullingData;
        private NativeList<InstanceHandle> m_FreeLODGroupDataHandles;

        private int m_CrossfadedRendererCount;
        private bool m_SupportDitheringCrossFade;

        public NativeList<LODGroupCullingData> lodGroupCullingData => m_LODGroupCullingData;
        public int crossfadedRendererCount => m_CrossfadedRendererCount;

        public int activeLodGroupCount => m_LODGroupData.Length;

        private static class LodGroupShaderIDs
        {
            public static readonly int _SupportDitheringCrossFade = Shader.PropertyToID("_SupportDitheringCrossFade");
            public static readonly int _LodGroupCullingDataGPUByteSize = Shader.PropertyToID("_LodGroupCullingDataGPUByteSize");
            public static readonly int _LodGroupCullingDataStartOffset = Shader.PropertyToID("_LodGroupCullingDataStartOffset");
            public static readonly int _LodCullingDataQueueCount = Shader.PropertyToID("_LodCullingDataQueueCount");
            public static readonly int _InputLodCullingDataIndices = Shader.PropertyToID("_InputLodCullingDataIndices");
            public static readonly int _InputLodCullingDataBuffer = Shader.PropertyToID("_InputLodCullingDataBuffer");
            public static readonly int _LodGroupCullingData = Shader.PropertyToID("_LodGroupCullingData");
        }

        public LODGroupDataPool(int initialInstanceCount, bool supportDitheringCrossFade)
        {
            m_LODGroupData = new NativeList<LODGroupData>(Allocator.Persistent);
            m_LODGroupDataHash = new NativeParallelHashMap<int, InstanceHandle>(64, Allocator.Persistent);

            m_LODGroupCullingData = new NativeList<LODGroupCullingData>(Allocator.Persistent);
            m_FreeLODGroupDataHandles = new NativeList<InstanceHandle>(Allocator.Persistent);

            m_SupportDitheringCrossFade = supportDitheringCrossFade;
        }

        public void Dispose()
        {
            m_LODGroupData.Dispose();
            m_LODGroupDataHash.Dispose();

            m_LODGroupCullingData.Dispose();
            m_FreeLODGroupDataHandles.Dispose();
        }

        public unsafe void UpdateLODGroupTransformData(in LODGroupInputData.ReadOnly inputData)
        {
            var lodGroupCount = inputData.lodGroupID.Length;

            var updateCount = 0;

            var jobData = new UpdateLODGroupTransformJob()
            {
                lodGroupDataHash = m_LODGroupDataHash,
                lodGroupIDs = inputData.lodGroupID,
                worldSpaceReferencePoints = inputData.worldSpaceReferencePoint,
                worldSpaceSizes = inputData.worldSpaceSize,
                lodGroupData = m_LODGroupData,
                lodGroupCullingData = m_LODGroupCullingData,
                supportDitheringCrossFade = m_SupportDitheringCrossFade,
                atomicUpdateCount = new UnsafeAtomicCounter32(&updateCount),
            };

            if (lodGroupCount >= UpdateLODGroupTransformJob.k_BatchSize)
                jobData.Schedule(lodGroupCount, UpdateLODGroupTransformJob.k_BatchSize).Complete();
            else
                jobData.Run(lodGroupCount);
        }

        public unsafe void UpdateLODGroupData(in LODGroupInputData.ReadOnly inputData)
        {
            FreeLODGroupData(inputData.invalidLODGroupID);
            if (inputData.lodGroupID.Length == 0)
                return;

            var lodGroupInstances = new NativeArray<InstanceHandle>(inputData.lodGroupID.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            int previousRendererCount = 0;

            new AllocateOrGetLODGroupDataInstancesJob
            {
                lodGroupsID = inputData.lodGroupID,
                lodGroupsData = m_LODGroupData,
                lodGroupCullingData = m_LODGroupCullingData,
                lodGroupDataHash = m_LODGroupDataHash,
                freeLODGroupDataHandles = m_FreeLODGroupDataHandles,
                lodGroupInstances = lodGroupInstances,
                previousRendererCount = &previousRendererCount
            }.Run();

            m_CrossfadedRendererCount -= previousRendererCount;
            Assert.IsTrue(m_CrossfadedRendererCount >= 0);

            int rendererCount = 0;

            var updateLODGroupDataJobData = new UpdateLODGroupDataJob
            {
                lodGroupInstances = lodGroupInstances,
                inputData = inputData,
                supportDitheringCrossFade = m_SupportDitheringCrossFade,
                lodGroupsData = m_LODGroupData.AsArray(),
                lodGroupsCullingData = m_LODGroupCullingData.AsArray(),
                rendererCount = new UnsafeAtomicCounter32(&rendererCount),
            };

            if (lodGroupInstances.Length >= UpdateLODGroupTransformJob.k_BatchSize)
                updateLODGroupDataJobData.Schedule(lodGroupInstances.Length, UpdateLODGroupTransformJob.k_BatchSize).Complete();
            else
                updateLODGroupDataJobData.Run(lodGroupInstances.Length);

            m_CrossfadedRendererCount += rendererCount;

            lodGroupInstances.Dispose();
        }

        public unsafe void FreeLODGroupData(NativeArray<int>.ReadOnly destroyedLODGroupsID)
        {
            if (destroyedLODGroupsID.Length == 0)
                return;

            int removedRendererCount = 0;

            new FreeLODGroupDataJob
            {
                destroyedLODGroupsID = destroyedLODGroupsID,
                lodGroupsData = m_LODGroupData,
                lodGroupDataHash = m_LODGroupDataHash,
                freeLODGroupDataHandles = m_FreeLODGroupDataHandles,
                removedRendererCount = &removedRendererCount
            }.Run();

            m_CrossfadedRendererCount -= removedRendererCount;
            Assert.IsTrue(m_CrossfadedRendererCount >= 0);
        }
    }
}
