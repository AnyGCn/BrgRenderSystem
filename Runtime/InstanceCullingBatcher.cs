﻿using System;
using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace BrgRenderSystem
{
    internal delegate void OnCullingCompleteCallback(JobHandle jobHandle, in BatchCullingContext cullingContext, in BatchCullingOutput cullingOutput);

    internal struct InstanceCullingBatcherDesc
    {
        public OnCullingCompleteCallback onCompleteCallback;

#if UNITY_EDITOR
        public Shader brgPicking;
        public Shader brgLoading;
        public Shader brgError;
#endif

        public static InstanceCullingBatcherDesc NewDefault()
        {
            return new InstanceCullingBatcherDesc()
            {
                onCompleteCallback = null
#if UNITY_EDITOR
                ,brgPicking = null
                ,brgLoading = null
                ,brgError = null
#endif
            };
        }
    }

    internal struct MeshProceduralInfo
    {
        public MeshTopology topology;
        public uint baseVertex;
        public uint firstIndex;
        public uint indexCount;
    }
    
    [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct PrefixSumDrawInstancesJob : IJob
    {
        [ReadOnly] public NativeParallelHashMap<RangeKey, int> rangeHash;

        public NativeList<DrawRange> drawRanges;
        public NativeList<DrawBatch> drawBatches;
        public NativeArray<int> drawBatchIndices;

        public void Execute()
        {
            Assert.AreEqual(rangeHash.Count(), drawRanges.Length);
            Assert.AreEqual(drawBatchIndices.Length, drawBatches.Length);

            // Prefix sum to calculate draw offsets for each DrawRange
            int drawPrefixSum = 0;

            for (int i = 0; i < drawRanges.Length; ++i)
            {
                ref DrawRange drawRange = ref drawRanges.ElementAt(i);
                drawRange.drawOffset = drawPrefixSum;
                drawPrefixSum += drawRange.drawCount;
            }

            // Generate DrawBatch index ranges for each DrawRange
            var internalRangeIndex = new NativeArray<int>(drawRanges.Length, Allocator.Temp);

            for (int i = 0; i < drawBatches.Length; ++i)
            {
                ref DrawBatch drawBatch = ref drawBatches.ElementAt(i);
                Assert.IsTrue(drawBatch.instanceCount > 0);

                if (rangeHash.TryGetValue(drawBatch.key.range, out int drawRangeIndex))
                {
                    ref DrawRange drawRange = ref drawRanges.ElementAt(drawRangeIndex);
                    drawBatchIndices[drawRange.drawOffset + internalRangeIndex[drawRangeIndex]] = i;
                    internalRangeIndex[drawRangeIndex]++;
                }
            }

            // Prefix sum to calculate instance offsets for each DrawCommand
            int drawInstancesPrefixSum = 0;

            for (int i = 0; i < drawBatchIndices.Length; ++i)
            {
                // DrawIndices remap to get DrawCommands ordered by DrawRange
                var drawBatchIndex = drawBatchIndices[i];
                ref DrawBatch drawBatch = ref drawBatches.ElementAt(drawBatchIndex);
                drawBatch.instanceOffset = drawInstancesPrefixSum;
                drawInstancesPrefixSum += drawBatch.instanceCount;
            }

            internalRangeIndex.Dispose();
        }
    }

    [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    internal unsafe struct BuildDrawListsJob : IJobParallelFor
    {
        public const int k_BatchSize = 128;
        public const int k_IntsPerCacheLine = JobsUtility.CacheLineSize / sizeof(int);

        [ReadOnly] public NativeParallelHashMap<DrawKey, int> batchHash;
        [NativeDisableContainerSafetyRestriction, NoAlias] [ReadOnly] public NativeList<DrawInstance> drawInstances;
        [NativeDisableContainerSafetyRestriction, NoAlias] [ReadOnly] public NativeList<DrawBatch> drawBatches;

        [NativeDisableContainerSafetyRestriction, NoAlias] [WriteOnly] public NativeArray<int> internalDrawIndex;
        [NativeDisableContainerSafetyRestriction, NoAlias] [WriteOnly] public NativeArray<int> drawInstanceIndices;

        private unsafe static int IncrementCounter(int* counter)
        {
            return Interlocked.Increment(ref UnsafeUtility.AsRef<int>(counter)) - 1;
        }

        public void Execute(int index)
        {
            // Generate instance index ranges for each DrawCommand
            ref DrawInstance drawInstance = ref drawInstances.ElementAt(index);
            int drawBatchIndex = batchHash[drawInstance.key];

            ref DrawBatch drawBatch = ref drawBatches.ElementAt(drawBatchIndex);
            var offset = IncrementCounter((int*)internalDrawIndex.GetUnsafePtr() + drawBatchIndex * k_IntsPerCacheLine);
            var writeIndex = drawBatch.instanceOffset + offset;
            drawInstanceIndices[writeIndex] = drawInstance.instanceIndex;
        }
    }

    [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    internal unsafe struct FindDrawInstancesJob : IJobParallelForBatch
    {
        public const int k_BatchSize = 128;

        [ReadOnly] public NativeArray<InstanceHandle> instancesSorted;
        [NativeDisableContainerSafetyRestriction, NoAlias] [ReadOnly] public NativeList<DrawInstance> drawInstances;

        [WriteOnly] public NativeList<int>.ParallelWriter outDrawInstanceIndicesWriter;

        public void Execute(int startIndex, int count)
        {
            int* instancesToRemove = stackalloc int[k_BatchSize];
            int length = 0;

            for (int i = startIndex; i < startIndex + count; ++i)
            {
                ref DrawInstance drawInstance = ref drawInstances.ElementAt(i);

                if (instancesSorted.BinarySearch(InstanceHandle.FromInt(drawInstance.instanceIndex)) >= 0)
                    instancesToRemove[length++] = i;
            }

            outDrawInstanceIndicesWriter.AddRangeNoResize(instancesToRemove, length);
        }
    }

    [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    internal unsafe struct FindMaterialDrawInstancesJob : IJobParallelForBatch
    {
        public const int k_BatchSize = 128;

        [ReadOnly] public NativeArray<uint> materialsSorted;
        [NativeDisableContainerSafetyRestriction, NoAlias] [ReadOnly] public NativeList<DrawInstance> drawInstances;

        [WriteOnly] public NativeList<int>.ParallelWriter outDrawInstanceIndicesWriter;

        public void Execute(int startIndex, int count)
        {
            int* instancesToRemove = stackalloc int[k_BatchSize];
            int length = 0;

            for (int i = startIndex; i < startIndex + count; ++i)
            {
                ref DrawInstance drawInstance = ref drawInstances.ElementAt(i);

                if (materialsSorted.BinarySearch(drawInstance.key.materialID.value) >= 0)
                    instancesToRemove[length++] = i;
            }

            outDrawInstanceIndicesWriter.AddRangeNoResize(instancesToRemove, length);
        }
    }

    [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct FindNonRegisteredInstancesJob<T> : IJobParallelForBatch where T : unmanaged
    {
        public const int k_BatchSize = 128;

        [ReadOnly] public NativeArray<int>.ReadOnly instanceIDs;
        [ReadOnly] public NativeParallelHashMap<int, T> hashMap;

        [WriteOnly] public NativeList<int>.ParallelWriter outInstancesWriter;

        public unsafe void Execute(int startIndex, int count)
        {
            int* notFoundinstanceIDs = stackalloc int[k_BatchSize];
            int length = 0;

            for (int i = startIndex; i < startIndex + count; ++i)
            {
                int instanceID = instanceIDs[i];

                if (!hashMap.ContainsKey(instanceID))
                    notFoundinstanceIDs[length++] = instanceID;
            }

            outInstancesWriter.AddRangeNoResize(notFoundinstanceIDs, length);
        }
    }

    [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct RegisterNewInstancesJob<T> : IJobParallelFor where T : unmanaged
    {
        public const int k_BatchSize = 128;

        [ReadOnly] public NativeArray<int> instanceIDs;
        [ReadOnly] public NativeArray<T> batchIDs;

        [WriteOnly] public NativeParallelHashMap<int, T>.ParallelWriter hashMap;

        public unsafe void Execute(int index)
        {
            hashMap.TryAdd(instanceIDs[index], batchIDs[index]);
        }
    }

    [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct RemoveDrawInstanceIndicesJob : IJob
    {
        [NativeDisableContainerSafetyRestriction, NoAlias] [ReadOnly] public NativeArray<int> drawInstanceIndices;

        public NativeList<DrawInstance> drawInstances;
        public NativeParallelHashMap<RangeKey, int> rangeHash;
        public NativeParallelHashMap<DrawKey, int> batchHash;
        public NativeList<DrawRange> drawRanges;
        public NativeList<DrawBatch> drawBatches;

        public void RemoveDrawRange(in RangeKey key)
        {
            int drawRangeIndex = rangeHash[key];

            ref DrawRange lastDrawRange = ref drawRanges.ElementAt(drawRanges.Length - 1);
            rangeHash[lastDrawRange.key] = drawRangeIndex;

            rangeHash.Remove(key);
            drawRanges.RemoveAtSwapBack(drawRangeIndex);
        }

        public void RemoveDrawBatch(in DrawKey key)
        {
            int drawBatchIndex = batchHash[key];

            ref DrawBatch drawBatch = ref drawBatches.ElementAt(drawBatchIndex);

            int drawRangeIndex = rangeHash[key.range];
            ref DrawRange drawRange = ref drawRanges.ElementAt(drawRangeIndex);

            Assert.IsTrue(drawRange.drawCount > 0);

            if (--drawRange.drawCount == 0)
                RemoveDrawRange(drawRange.key);

            ref DrawBatch lastDrawBatch = ref drawBatches.ElementAt(drawBatches.Length - 1);
            batchHash[lastDrawBatch.key] = drawBatchIndex;

            batchHash.Remove(key);
            drawBatches.RemoveAtSwapBack(drawBatchIndex);
        }

        public unsafe void Execute()
        {
            var drawInstancesPtr = (DrawInstance*)drawInstances.GetUnsafePtr();
            var drawInstancesNewBack = drawInstances.Length - 1;

            for (int indexRev = drawInstanceIndices.Length - 1; indexRev >= 0; --indexRev)
            {
                int indexToRemove = drawInstanceIndices[indexRev];
                DrawInstance* drawInstance = drawInstancesPtr + indexToRemove;

                int drawBatchIndex = batchHash[drawInstance->key];
                ref DrawBatch drawBatch = ref drawBatches.ElementAt(drawBatchIndex);

                Assert.IsTrue(drawBatch.instanceCount > 0);

                if (--drawBatch.instanceCount == 0)
                    RemoveDrawBatch(drawBatch.key);

                UnsafeUtility.MemCpy(drawInstance, drawInstancesPtr + drawInstancesNewBack--, sizeof(DrawInstance));
            }

            drawInstances.ResizeUninitialized(drawInstancesNewBack + 1);
        }
    }

    
    [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct UpdatePackedMaterialDataCacheJob : IJob
    {
        [ReadOnly] public NativeArray<int>.ReadOnly materialIDs;
        [ReadOnly] public NativeArray<GPUDrivenPackedMaterialData>.ReadOnly packedMaterialDatas;

        public NativeParallelHashMap<int, GPUDrivenPackedMaterialData> packedMaterialHash;

        private void ProcessMaterial(int i)
        {
            var materialID = materialIDs[i];
            var packedMaterialData = packedMaterialDatas[i];

            if (materialID == 0)
                return;

            // Cache the packed material so we can detect a change in material that would need to update the renderer data.
            packedMaterialHash[materialID] = packedMaterialData;
        }

        public void Execute()
        {
            for (int i = 0; i < materialIDs.Length; ++i)
                ProcessMaterial(i);
        }
    }

    [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct CreateDrawBatchesJobAll : IJob
    {
        [ReadOnly] public NativeParallelHashMap<int, BatchID>.ReadOnly batchIdHash;
        [ReadOnly] public NativeParallelHashMap<int, BatchMeshID>.ReadOnly batchMeshHash;
        [ReadOnly] public NativeParallelHashMap<int, BatchMaterialID>.ReadOnly batchMaterialHash;
        [ReadOnly] public NativeParallelHashMap<int, GPUDrivenPackedMaterialData>.ReadOnly packedMaterialDataHash;
        
        public NativeParallelHashMap<RangeKey, int> rangeHash;
        public NativeList<DrawRange> drawRanges;
        public NativeParallelHashMap<DrawKey, int> batchHash;
        public NativeList<DrawBatch> drawBatches;
        
        public void Execute()
        {
            throw new NotImplementedException();
        }
    }
    
    [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct CreateDrawBatchesJob : IJob
    {
        // [ReadOnly] public bool implicitInstanceIndices;
        [ReadOnly] public bool isUBOMode;
        [ReadOnly] public NativeArray<InstanceHandle> instances;
        [ReadOnly] public NativeParallelHashMap<int, BatchMeshID>.ReadOnly batchMeshHash;
        [ReadOnly] public NativeParallelHashMap<int, BatchMaterialID>.ReadOnly batchMaterialHash;
        [ReadOnly] public NativeParallelHashMap<int, GPUDrivenPackedMaterialData>.ReadOnly packedMaterialDataHash;
        [ReadOnly] public CPUInstanceData.ReadOnly instanceData;
        [ReadOnly] public CPUSharedInstanceData.ReadOnly sharedInstanceData;
        
        public NativeParallelHashMap<RangeKey, int> rangeHash;
        public NativeList<DrawRange> drawRanges;
        public NativeParallelHashMap<DrawKey, int> batchHash;
        public NativeList<DrawBatch> drawBatches;

        [WriteOnly] public NativeList<DrawInstance> drawInstances;

        private ref DrawRange EditDrawRange(in RangeKey key)
        {
            int drawRangeIndex;

            if (!rangeHash.TryGetValue(key, out drawRangeIndex))
            {
                var drawRange = new DrawRange { key = key, drawCount = 0, drawOffset = 0 };
                drawRangeIndex = drawRanges.Length;
                rangeHash.Add(key, drawRangeIndex);
                drawRanges.Add(drawRange);
            }

            ref DrawRange data = ref drawRanges.ElementAt(drawRangeIndex);
            Assert.IsTrue(data.key.Equals(key));

            return ref data;
        }

        private ref DrawBatch EditDrawBatch(in DrawKey key)
        {
            // var procInfo = new MeshProceduralInfo();
            // procInfo.topology = subMeshDescriptor.topology;
            // procInfo.baseVertex = (uint)subMeshDescriptor.baseVertex;
            // procInfo.firstIndex = (uint)subMeshDescriptor.indexStart;
            // procInfo.indexCount = (uint)subMeshDescriptor.indexCount;

            int drawBatchIndex;

            if (!batchHash.TryGetValue(key, out drawBatchIndex))
            {
                var drawBatch = new DrawBatch() { key = key, instanceCount = 0, instanceOffset = 0 };
                drawBatchIndex = drawBatches.Length;
                batchHash.Add(key, drawBatchIndex);
                drawBatches.Add(drawBatch);
            }

            ref DrawBatch data = ref drawBatches.ElementAt(drawBatchIndex);
            Assert.IsTrue(data.key.Equals(key));

            return ref data;
        }

        public void ProcessInstance(InstanceHandle instance)
        {
            int index = instanceData.instanceIndices[instance.index];
            int sharedIndex = sharedInstanceData.SharedInstanceToIndex(instanceData.sharedInstances[index]);
            var meshID = sharedInstanceData.meshIDs[sharedIndex];
            // var submeshCount = rendererData.subMeshCount[meshIndex];
            // var subMeshDescOffset = rendererData.subMeshDescOffset[meshIndex];
            var batchMeshID = batchMeshHash[meshID];
            var rendererGroupID = sharedInstanceData.rendererGroupIDs[sharedIndex];
            // var startSubMesh = rendererData.subMeshStartIndex[i];
            var gameObjectLayer = sharedInstanceData.gameObjectLayers[sharedIndex];
            var renderingLayerMask = sharedInstanceData.renderingLayerMasks[sharedIndex];
            var materialIDArray = sharedInstanceData.materialIDArrays[sharedIndex];
            var instanceFlags = sharedInstanceData.flags[sharedIndex].instanceFlags;
            var gpuInstanceIndex = instanceData.gpuInstances[index];
            // var materialsCount = rendererData.materialsCount[index];
            // var lightmapIndex = rendererData.lightmapIndex[i];
            // var packedRendererData = rendererData.packedRendererData[index];
            
            var rangeKey = new RangeKey
            {
                layer = (byte)gameObjectLayer,
                renderingLayerMask = renderingLayerMask,
                // motionMode = packedRendererData.motionVecGenMode,
                shadowCastingMode = instanceFlags.shadowCastingMode,
                staticShadowCaster = instanceFlags.staticShadowCaster,
                // rendererPriority = rendererPriority,
                // supportsIndirect = supportsIndirect
            };
            
            ref DrawRange drawRange = ref EditDrawRange(rangeKey);
            for (int matIndex = 0; matIndex < materialIDArray.Length; ++matIndex)
            {
                // if (matIndex >= submeshCount)
                // {
                //     Debug.LogWarning("Material count in the shared material list is higher than sub mesh count for the mesh. Object may be corrupted.");
                //     continue;
                // }

                var materialID = materialIDArray[matIndex];
                bool isFound = packedMaterialDataHash.TryGetValue(materialID, out var packedMaterialData);
                Assert.IsTrue(isFound);
                
                if (materialID == 0)
                {
                    Debug.LogWarning("Material in the shared materials list is null. Object will be partially rendered.");
                    continue;
                }

                batchMaterialHash.TryGetValue(materialID, out BatchMaterialID batchMaterialID);

                var flags = BatchDrawCommandFlags.None;

                // We always provide crossfade value packed in instance index. We don't use None even if there is no LOD to not split the batch.
                // flags = BatchDrawCommandFlags.LODCrossFadeValuePacked;

                // Let the engine know if we've opted out of lightmap texture arrays
                // flags |= BatchDrawCommandFlags.UseLegacyLightmapsKeyword;

                // assume that a custom motion vectors pass contains deformation motion, so should always output motion vectors
                // (otherwise this flag is set dynamically during culling only when the transform is changing)
                if (packedMaterialData.isMotionVectorsPassEnabled)
                    flags |= BatchDrawCommandFlags.HasMotion;

                if (packedMaterialData.isTransparent)
                    flags |= BatchDrawCommandFlags.HasSortingPosition;

                {
                    var submeshIndex = matIndex;
                    // var subMeshDesc = rendererData.subMeshDesc[subMeshDescOffset + submeshIndex];

                    var drawKey = new DrawKey
                    {
                        windowID = gpuInstanceIndex.windowIndex,
                        materialID = batchMaterialID,
                        meshID = batchMeshID,
                        submeshIndex = submeshIndex,
                        flags = flags,
                        transparentInstanceId = packedMaterialData.isTransparent ? rendererGroupID : 0,
                        range = rangeKey,
                        // overridenComponents = (uint)InstanceComponentGroup.Default,
                        // When we've opted out of lightmap texture arrays, we
                        // need to pass in a valid lightmap index. The engine
                        // uses this index for sorting and for breaking the
                        // batch when lightmaps change across draw calls, and
                        // for binding the correct light map.
                        // lightmapIndex = lightmapIndex
                    };

                    ref DrawBatch drawBatch = ref EditDrawBatch(drawKey);

                    if (drawBatch.instanceCount == 0)
                        ++drawRange.drawCount;

                    drawBatch.instanceCount++;
                    drawInstances.Add(new DrawInstance { key = drawKey, instanceIndex = instance.index });
                }
            }

        }
        
        public void Execute()
        {
            if (isUBOMode)
            {
                for (int i = 0; i < instanceData.instancesLength; ++i)
                    ProcessInstance(instanceData.instances[i]);
            }
            else
            {
                for (int i = 0; i < instances.Length; ++i)
                    ProcessInstance(instances[i]);
            }
        }
    }
    
    internal class CPUDrawInstanceData
    {
        public NativeList<DrawInstance> drawInstances => m_DrawInstances;
        public NativeParallelHashMap<DrawKey, int> batchHash => m_BatchHash;
        public NativeList<DrawBatch> drawBatches => m_DrawBatches;
        public NativeParallelHashMap<RangeKey, int> rangeHash => m_RangeHash;
        public NativeList<DrawRange> drawRanges => m_DrawRanges;
        public NativeArray<int> drawBatchIndices => m_DrawBatchIndices.AsArray();
        public NativeArray<int> drawInstanceIndices => m_DrawInstanceIndices.AsArray();

        private NativeParallelHashMap<RangeKey, int> m_RangeHash;       // index in m_DrawRanges, hashes by range state
        private NativeList<DrawRange> m_DrawRanges;
        private NativeParallelHashMap<DrawKey, int> m_BatchHash;        // index in m_DrawBatches, hashed by draw state
        private NativeList<DrawBatch> m_DrawBatches;
        private NativeList<DrawInstance> m_DrawInstances;
        private NativeList<int> m_DrawInstanceIndices;          // DOTS instance index, arranged in contiguous blocks in m_DrawBatches order (see DrawBatch.instanceOffset, DrawBatch.instanceCount)
        private NativeList<int> m_DrawBatchIndices;             // index in m_DrawBatches, arranged in contiguous blocks in m_DrawRanges order (see DrawRange.drawOffset, DrawRange.drawCount)

        private bool m_NeedsRebuild;

        public bool valid => m_DrawInstances.IsCreated;

        public void Initialize()
        {
            Assert.IsTrue(!valid);
            m_RangeHash = new NativeParallelHashMap<RangeKey, int>(1024, Allocator.Persistent);
            m_DrawRanges = new NativeList<DrawRange>(Allocator.Persistent);
            m_BatchHash = new NativeParallelHashMap<DrawKey, int>(1024, Allocator.Persistent);
            m_DrawBatches = new NativeList<DrawBatch>(Allocator.Persistent);
            m_DrawInstances = new NativeList<DrawInstance>(1024, Allocator.Persistent);
            m_DrawInstanceIndices = new NativeList<int>(1024, Allocator.Persistent);
            m_DrawBatchIndices = new NativeList<int>(1024, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (m_DrawBatchIndices.IsCreated)
                m_DrawBatchIndices.Dispose();

            if (m_DrawInstanceIndices.IsCreated)
                m_DrawInstanceIndices.Dispose();

            if (m_DrawInstances.IsCreated)
                m_DrawInstances.Dispose();

            if (m_DrawBatches.IsCreated)
                m_DrawBatches.Dispose();

            if (m_BatchHash.IsCreated)
                m_BatchHash.Dispose();

            if (m_DrawRanges.IsCreated)
                m_DrawRanges.Dispose();

            if (m_RangeHash.IsCreated)
                m_RangeHash.Dispose();
        }

        public void RebuildDrawListsIfNeeded()
        {
            if (!m_NeedsRebuild)
                return;

            m_NeedsRebuild = false;

            Assert.IsTrue(m_RangeHash.Count() == m_DrawRanges.Length);
            Assert.IsTrue(m_BatchHash.Count() == m_DrawBatches.Length);

            m_DrawInstanceIndices.ResizeUninitialized(m_DrawInstances.Length);
            m_DrawBatchIndices.ResizeUninitialized(m_DrawBatches.Length);

            var internalDrawIndex = new NativeArray<int>(drawBatches.Length * BuildDrawListsJob.k_IntsPerCacheLine, Allocator.TempJob, NativeArrayOptions.ClearMemory);

            var prefixSumDrawInstancesJob = new PrefixSumDrawInstancesJob()
            {
                rangeHash = m_RangeHash,
                drawRanges = m_DrawRanges,
                drawBatches = m_DrawBatches,
                drawBatchIndices = m_DrawBatchIndices.AsArray()
            };

            var prefixSumJobHandle = prefixSumDrawInstancesJob.Schedule();

            var buildDrawListsJob = new BuildDrawListsJob()
            {
                drawInstances = m_DrawInstances,
                batchHash = m_BatchHash,
                drawBatches = m_DrawBatches,
                internalDrawIndex = internalDrawIndex,
                drawInstanceIndices = m_DrawInstanceIndices.AsArray(),
            };

            buildDrawListsJob.Schedule(m_DrawInstances.Length, BuildDrawListsJob.k_BatchSize, prefixSumJobHandle).Complete();

            internalDrawIndex.Dispose();
        }

        public unsafe void DestroyDrawInstanceIndices(NativeArray<int> drawInstanceIndicesToDestroy)
        {
            Profiler.BeginSample("DestroyDrawInstanceIndices.ParallelSort");
            drawInstanceIndicesToDestroy.ParallelSort().Complete();
            Profiler.EndSample();

            var removeDrawInstanceIndicesJob = new RemoveDrawInstanceIndicesJob
            {
                drawInstanceIndices = drawInstanceIndicesToDestroy,
                drawInstances = m_DrawInstances,
                drawBatches = m_DrawBatches,
                drawRanges = m_DrawRanges,
                batchHash = m_BatchHash,
                rangeHash = m_RangeHash
            };

            removeDrawInstanceIndicesJob.Run();
        }

        public unsafe void DestroyDrawInstances(NativeArray<InstanceHandle> destroyedInstances)
        {
            if (m_DrawInstances.IsEmpty || destroyedInstances.Length == 0)
                return;

            NeedsRebuild();

            var destroyedInstancesSorted = new NativeArray<InstanceHandle>(destroyedInstances, Allocator.TempJob);
            Assert.AreEqual(UnsafeUtility.SizeOf<InstanceHandle>(), UnsafeUtility.SizeOf<int>());

            Profiler.BeginSample("DestroyDrawInstances.ParallelSort");
            destroyedInstancesSorted.Reinterpret<int>().ParallelSort().Complete();
            Profiler.EndSample();

            var drawInstanceIndicesToDestroy = new NativeList<int>(m_DrawInstances.Length, Allocator.TempJob);

            var findDrawInstancesJobHandle = new FindDrawInstancesJob()
            {
                instancesSorted = destroyedInstancesSorted,
                drawInstances = m_DrawInstances,
                outDrawInstanceIndicesWriter = drawInstanceIndicesToDestroy.AsParallelWriter()
            };

            findDrawInstancesJobHandle.ScheduleBatch(m_DrawInstances.Length, FindDrawInstancesJob.k_BatchSize).Complete();

            DestroyDrawInstanceIndices(drawInstanceIndicesToDestroy.AsArray());

            destroyedInstancesSorted.Dispose();
            drawInstanceIndicesToDestroy.Dispose();
        }

        public unsafe void DestroyMaterialDrawInstances(NativeArray<uint> destroyedBatchMaterials)
        {
            if (m_DrawInstances.IsEmpty || destroyedBatchMaterials.Length == 0)
                return;

            NeedsRebuild();

            var destroyedBatchMaterialsSorted = new NativeArray<uint>(destroyedBatchMaterials, Allocator.TempJob);

            Profiler.BeginSample("DestroyedBatchMaterials.ParallelSort");
            destroyedBatchMaterialsSorted.Reinterpret<int>().ParallelSort().Complete();
            Profiler.EndSample();

            var drawInstanceIndicesToDestroy = new NativeList<int>(m_DrawInstances.Length, Allocator.TempJob);

            var findDrawInstancesJobHandle = new FindMaterialDrawInstancesJob()
            {
                materialsSorted = destroyedBatchMaterialsSorted,
                drawInstances = m_DrawInstances,
                outDrawInstanceIndicesWriter = drawInstanceIndicesToDestroy.AsParallelWriter()
            };

            findDrawInstancesJobHandle.ScheduleBatch(m_DrawInstances.Length, FindMaterialDrawInstancesJob.k_BatchSize).Complete();

            DestroyDrawInstanceIndices(drawInstanceIndicesToDestroy.AsArray());

            destroyedBatchMaterialsSorted.Dispose();
            drawInstanceIndicesToDestroy.Dispose();
        }

        public void NeedsRebuild()
        {
            m_NeedsRebuild = true;
        }

        public void Clear()
        {
            m_DrawInstances.Clear();
            m_DrawBatches.Clear();
            m_DrawRanges.Clear();
            m_BatchHash.Clear();
            m_RangeHash.Clear();
            m_DrawInstanceIndices.Clear();
            m_DrawBatchIndices.Clear();
        }
    }
    
    internal class InstanceCullingBatcher : IDisposable
    {
        private RenderersBatchersContext m_BatchersContext;
        private CPUDrawInstanceData m_DrawInstanceData;
        private BatchRendererGroup m_BRG;
        private NativeArray<BatchID> m_GlobalBatchIDs;
        private InstanceCuller m_Culler;
        private NativeParallelHashMap<int, BatchMaterialID> m_BatchMaterialHash;
        private NativeParallelHashMap<int, GPUDrivenPackedMaterialData> m_PackedMaterialHash;
        private NativeParallelHashMap<int, BatchMeshID> m_BatchMeshHash;

        private int m_CachedInstanceDataBufferLayoutVersion;

        private OnCullingCompleteCallback m_OnCompleteCallback;

        public NativeParallelHashMap<int, BatchMaterialID> batchMaterialHash => m_BatchMaterialHash;
        public NativeParallelHashMap<int, GPUDrivenPackedMaterialData> packedMaterialHash => m_PackedMaterialHash;

        public InstanceCullingBatcher(RenderersBatchersContext batcherContext, InstanceCullingBatcherDesc desc)
        {
            m_BatchersContext = batcherContext;
            m_DrawInstanceData = new CPUDrawInstanceData();
            m_DrawInstanceData.Initialize();

            m_BRG = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);

#if UNITY_EDITOR
            if (desc.brgPicking != null)
            {
                var mat = new Material(desc.brgPicking);
                mat.hideFlags = HideFlags.HideAndDontSave;
                m_BRG.SetPickingMaterial(mat);
            }
            if (desc.brgLoading != null)
            {
                var mat = new Material(desc.brgLoading);
                mat.hideFlags = HideFlags.HideAndDontSave;
                m_BRG.SetLoadingMaterial(mat);
            }
            if (desc.brgError != null)
            {
                var mat = new Material(desc.brgError);
                mat.hideFlags = HideFlags.HideAndDontSave;
                m_BRG.SetErrorMaterial(mat);
            }
            var viewTypes = new BatchCullingViewType[] {
                BatchCullingViewType.Light,
                BatchCullingViewType.Camera,
                BatchCullingViewType.Picking,
                BatchCullingViewType.SelectionOutline,
            };
            m_BRG.SetEnabledViewTypes(viewTypes);
#endif

            m_Culler = new InstanceCuller();
            m_Culler.Init(batcherContext.debugStats);

            m_CachedInstanceDataBufferLayoutVersion = -1;
            // m_OnCompleteCallback = desc.onCompleteCallback;
            m_BatchMaterialHash = new NativeParallelHashMap<int, BatchMaterialID>(64, Allocator.Persistent);
            m_PackedMaterialHash = new NativeParallelHashMap<int, GPUDrivenPackedMaterialData>(64, Allocator.Persistent);
            m_BatchMeshHash = new NativeParallelHashMap<int, BatchMeshID>(64, Allocator.Persistent);

            GetBatchID();
            // m_GlobalBatchIDs = new NativeParallelHashMap<uint, BatchID>(1, Allocator.Persistent);
            // m_GlobalBatchIDs.Add((uint)InstanceComponentGroup.Default, GetBatchID(InstanceComponentGroup.Default));
            // m_GlobalBatchIDs.Add((uint)InstanceComponentGroup.DefaultWind, GetBatchID(InstanceComponentGroup.DefaultWind));
            // m_GlobalBatchIDs.Add((uint)InstanceComponentGroup.DefaultLightProbe, GetBatchID(InstanceComponentGroup.DefaultLightProbe));
            // m_GlobalBatchIDs.Add((uint)InstanceComponentGroup.DefaultLightmap, GetBatchID(InstanceComponentGroup.DefaultLightmap));
            // m_GlobalBatchIDs.Add((uint)InstanceComponentGroup.DefaultWindLightProbe, GetBatchID(InstanceComponentGroup.DefaultWindLightProbe));
            // m_GlobalBatchIDs.Add((uint)InstanceComponentGroup.DefaultWindLightmap, GetBatchID(InstanceComponentGroup.DefaultWindLightmap));
        }

        internal ref InstanceCuller culler => ref m_Culler;

        public void Dispose()
        {
            m_OnCompleteCallback = null;
            m_Culler.Dispose();

            foreach (var batchID in m_GlobalBatchIDs)
                if (!batchID.Equals(BatchID.Null))
                    m_BRG.RemoveBatch(batchID);
            m_GlobalBatchIDs.Dispose();

            if (m_BRG != null)
                m_BRG.Dispose();

            m_DrawInstanceData.Dispose();
            m_DrawInstanceData = null;

            m_BatchMaterialHash.Dispose();
            m_PackedMaterialHash.Dispose();
            m_BatchMeshHash.Dispose();
        }

        private void GetBatchID()
        {
            if (m_CachedInstanceDataBufferLayoutVersion != m_BatchersContext.instanceDataBufferLayoutVersion)
                return;

            Assert.IsTrue(m_BatchersContext.defaultDescriptions.Length == m_BatchersContext.defaultMetadata.Length);

            // const uint kClearIsOverriddenBit = 0x4FFFFFFF;
            var tempMetadata = new NativeList<MetadataValue>(m_BatchersContext.defaultMetadata.Length, Allocator.Temp);

            for(int i = 0; i < m_BatchersContext.defaultDescriptions.Length; ++i)
            {
                // var componentGroup = m_BatchersContext.defaultDescriptions[i].componentGroup;
                var metadata = m_BatchersContext.defaultMetadata[i];
                var value = metadata.Value;

                // if instances in this batch do not override the component, clear the override bit
                // if ((componentsOverriden & componentGroup) == 0)
                //     value &= kClearIsOverriddenBit;

                tempMetadata.Add(new MetadataValue
                {
                    NameID = metadata.NameID,
                    Value = value
                });
            }

            GPUInstanceDataBuffer instanceDataBuffer = m_BatchersContext.m_InstanceDataBuffer;
            int windowCount = instanceDataBuffer.windowCount;
            m_GlobalBatchIDs = new NativeArray<BatchID>(windowCount, Allocator.Persistent);
            for (int i = 0; i < windowCount; ++i)
            {
                if (GPUInstanceDataBuffer.IsUBO)
                    m_GlobalBatchIDs[i] = m_BRG.AddBatch(tempMetadata.AsArray(), m_BatchersContext.gpuInstanceDataBuffer.bufferHandle, (uint)(i * GPUInstanceDataBuffer.byteSizePerWindow), (uint)GPUInstanceDataBuffer.byteSizePerWindow);
                else
                    m_GlobalBatchIDs[i] = m_BRG.AddBatch(tempMetadata.AsArray(), m_BatchersContext.gpuInstanceDataBuffer.bufferHandle);
            }
        }

        private void UpdateInstanceDataBufferLayoutVersion()
        {
            if (m_CachedInstanceDataBufferLayoutVersion != m_BatchersContext.instanceDataBufferLayoutVersion)
            {
                m_CachedInstanceDataBufferLayoutVersion = m_BatchersContext.instanceDataBufferLayoutVersion;
                foreach (var batchID in m_GlobalBatchIDs)
                    if (!batchID.Equals(BatchID.Null))
                        m_BRG.RemoveBatch(batchID);
                m_GlobalBatchIDs.Dispose();
                GetBatchID();
            }
        }

        public CPUDrawInstanceData GetDrawInstanceData()
        {
            return m_DrawInstanceData;
        }

        public unsafe JobHandle OnPerformCulling(
            BatchRendererGroup rendererGroup,
            BatchCullingContext cc,
            BatchCullingOutput cullingOutput,
            IntPtr userContext)
        {
            if (!m_BatchersContext.enabled)
                return new JobHandle();
            
            if (!m_GlobalBatchIDs.IsCreated)
                return new JobHandle();
            
            foreach (var batchID in m_GlobalBatchIDs)
            {
                if (batchID.Equals(BatchID.Null))
                    return new JobHandle();
            }

            m_DrawInstanceData.RebuildDrawListsIfNeeded();

            bool allowOcclusionCulling = m_BatchersContext.hasBoundingSpheres;
            JobHandle jobHandle = m_Culler.CreateCullJobTree(
                cc,
                cullingOutput,
                m_BatchersContext.instanceData,
                m_BatchersContext.sharedInstanceData,
                m_BatchersContext.lodGroupCullingData,
                m_DrawInstanceData,
                m_GlobalBatchIDs,
                m_BatchersContext.crossfadedRendererCount,
                m_BatchersContext.smallMeshScreenPercentage,
                null);

            if (m_OnCompleteCallback != null)
                m_OnCompleteCallback(jobHandle, cc, cullingOutput);

            return jobHandle;
        }

        public void OnFinishedCulling(IntPtr customCullingResult)
        {
            int viewInstanceID = (int)customCullingResult;
            // m_Culler.EnsureValidOcclusionTestResults(viewInstanceID);
        }

        public void DestroyDrawInstances(NativeArray<InstanceHandle> instances)
        {
            if (instances.Length == 0)
                return;

            Profiler.BeginSample("DestroyDrawInstances");

            m_DrawInstanceData.DestroyDrawInstances(instances);

            Profiler.EndSample();
        }

        public void DestroyMaterials(NativeArray<int> destroyedMaterials)
        {
            if (destroyedMaterials.Length == 0)
                return;

            Profiler.BeginSample("DestroyMaterials");

            var destroyedBatchMaterials = new NativeList<uint>(destroyedMaterials.Length, Allocator.TempJob);

            foreach (int destroyedMaterial in destroyedMaterials)
            {
                if (m_BatchMaterialHash.TryGetValue(destroyedMaterial, out var destroyedBatchMaterial))
                {
                    destroyedBatchMaterials.Add(destroyedBatchMaterial.value);
                    m_BatchMaterialHash.Remove(destroyedMaterial);
                    m_PackedMaterialHash.Remove(destroyedMaterial);
                    m_BRG.UnregisterMaterial(destroyedBatchMaterial);
                }
            }

            m_DrawInstanceData.DestroyMaterialDrawInstances(destroyedBatchMaterials.AsArray());

            destroyedBatchMaterials.Dispose();

            Profiler.EndSample();
        }

        public void DestroyMeshes(NativeArray<int> destroyedMeshes)
        {
            if (destroyedMeshes.Length == 0)
                return;

            Profiler.BeginSample("DestroyMeshes");

            foreach (int destroyedMesh in destroyedMeshes)
            {
                if (m_BatchMeshHash.TryGetValue(destroyedMesh, out var destroyedBatchMesh))
                {
                    m_BatchMeshHash.Remove(destroyedMesh);
                    m_BRG.UnregisterMesh(destroyedBatchMesh);
                }
            }

            Profiler.EndSample();
        }

        // public void PostCullBeginCameraRendering(RenderRequestBatcherContext context)
        // {
        // }

        private void RegisterBatchMeshes(NativeArray<int>.ReadOnly meshIDs)
        {
            var newMeshIDs = new NativeList<int>(meshIDs.Length, Allocator.TempJob);
            new FindNonRegisteredInstancesJob<BatchMeshID>
            {
                instanceIDs = meshIDs,
                hashMap = m_BatchMeshHash,
                outInstancesWriter = newMeshIDs.AsParallelWriter()
            }
            .ScheduleBatch(meshIDs.Length, FindNonRegisteredInstancesJob<BatchMeshID>.k_BatchSize).Complete();
            var newBatchMeshIDs = new NativeArray<BatchMeshID>(newMeshIDs.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            m_BRG.RegisterMeshes(newMeshIDs.AsArray(), newBatchMeshIDs);

            int totalMeshesNum = m_BatchMeshHash.Count() + newBatchMeshIDs.Length;
            m_BatchMeshHash.Capacity = Math.Max(m_BatchMeshHash.Capacity, Mathf.CeilToInt(totalMeshesNum / 1023.0f) * 1024);

            new RegisterNewInstancesJob<BatchMeshID>
            {
                instanceIDs = newMeshIDs.AsArray(),
                batchIDs = newBatchMeshIDs,
                hashMap = m_BatchMeshHash.AsParallelWriter()
            }
            .Schedule(newMeshIDs.Length, RegisterNewInstancesJob<BatchMeshID>.k_BatchSize).Complete();

            newMeshIDs.Dispose();
            newBatchMeshIDs.Dispose();
        }

        private void RegisterBatchMaterials(in NativeArray<int>.ReadOnly usedMaterialIDs)
        {
            var newMaterialIDs = new NativeList<int>(usedMaterialIDs.Length, Allocator.TempJob);
            new FindNonRegisteredInstancesJob<BatchMaterialID>
            {
                instanceIDs = usedMaterialIDs,
                hashMap = m_BatchMaterialHash,
                outInstancesWriter = newMaterialIDs.AsParallelWriter()
            }
            .ScheduleBatch(usedMaterialIDs.Length, FindNonRegisteredInstancesJob<BatchMaterialID>.k_BatchSize).Complete();

            var newBatchMaterialIDs = new NativeArray<BatchMaterialID>(newMaterialIDs.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            m_BRG.RegisterMaterials(newMaterialIDs.AsArray(), newBatchMaterialIDs);

            int totalMaterialsNum = m_BatchMaterialHash.Count() + newMaterialIDs.Length;
            m_BatchMaterialHash.Capacity = Math.Max(m_BatchMaterialHash.Capacity, Mathf.CeilToInt(totalMaterialsNum / 1023.0f) * 1024);
            m_PackedMaterialHash.Capacity = m_BatchMaterialHash.Capacity;

            new RegisterNewInstancesJob<BatchMaterialID>
            {
                instanceIDs = newMaterialIDs.AsArray(),
                batchIDs = newBatchMaterialIDs,
                hashMap = m_BatchMaterialHash.AsParallelWriter()
            }
            .Schedule(newMaterialIDs.Length, RegisterNewInstancesJob<BatchMaterialID>.k_BatchSize).Complete();

            newMaterialIDs.Dispose();
            newBatchMaterialIDs.Dispose();
        }

        public void SchedulePackedMaterialCacheUpdate(NativeArray<int>.ReadOnly materialIDs, NativeArray<GPUDrivenPackedMaterialData>.ReadOnly packedMaterialDatas)
        {
            new UpdatePackedMaterialDataCacheJob
            {
                materialIDs = materialIDs,
                packedMaterialDatas = packedMaterialDatas,
                packedMaterialHash = m_PackedMaterialHash
            }.Run();
        }

        public void BuildBatch(
            NativeArray<InstanceHandle> instances,
            NativeArray<int>.ReadOnly usedMaterialIDs,
            NativeArray<int>.ReadOnly usedMeshIDs,
            in RendererGroupInputData.ReadOnly rendererData,
            bool registerMaterialsAndMeshes)
        {
            if (registerMaterialsAndMeshes)
            {
                RegisterBatchMaterials(usedMaterialIDs);
                RegisterBatchMeshes(usedMeshIDs);
                SchedulePackedMaterialCacheUpdate(usedMaterialIDs, rendererData.packedMaterialData);
            }

            if (GPUInstanceDataBuffer.IsUBO)
                m_DrawInstanceData.Clear();
            
            new CreateDrawBatchesJob
            {
                // implicitInstanceIndices = rendererData.instancesCount.Length == 0,
                isUBOMode = GPUInstanceDataBuffer.IsUBO,
                instances = instances,
                instanceData = m_BatchersContext.instanceData,
                sharedInstanceData = m_BatchersContext.sharedInstanceData,
                batchMeshHash = m_BatchMeshHash.AsReadOnly(),
                batchMaterialHash = m_BatchMaterialHash.AsReadOnly(),
                packedMaterialDataHash = m_PackedMaterialHash.AsReadOnly(),
                rangeHash = m_DrawInstanceData.rangeHash,
                drawRanges = m_DrawInstanceData.drawRanges,
                batchHash = m_DrawInstanceData.batchHash,
                drawBatches = m_DrawInstanceData.drawBatches,
                drawInstances = m_DrawInstanceData.drawInstances
            }.Run();

            m_DrawInstanceData.NeedsRebuild();
            UpdateInstanceDataBufferLayoutVersion();
        }

        // public void InstanceOccludersUpdated(int viewInstanceID, int subviewMask)
        // {
        //     m_Culler.InstanceOccludersUpdated(viewInstanceID, subviewMask, m_BatchersContext);
        // }

        public void UpdateFrame()
        {
            m_Culler.UpdateFrame();
        }

        public ParallelBitArray GetCompactedVisibilityMasks(bool syncCullingJobs)
        {
            return m_Culler.GetCompactedVisibilityMasks(syncCullingJobs);
        }

        public void OnEndContextRendering()
        {
            ParallelBitArray compactedVisibilityMasks = GetCompactedVisibilityMasks(syncCullingJobs: true);

            if(compactedVisibilityMasks.IsCreated)
                m_BatchersContext.UpdatePerFrameInstanceVisibility(compactedVisibilityMasks);
        }

        public void OnBeginCameraRendering(Camera camera)
        {
            m_Culler.OnBeginCameraRendering(camera);
        }

        public void OnEndCameraRendering(Camera camera)
        {
            m_Culler.OnEndCameraRendering(camera);
        }

        public void UpdateSelectedRenderers(NativeArray<int> rendererIDs)
        {
            // var instances = new NativeArray<InstanceHandle>(renderersID.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            // m_BatchersContext.ScheduleQueryRendererGroupInstancesJob(renderersID, instances).Complete();
            // m_BatchersContext.UpdateSelectedInstances(instances);
            // instances.Dispose();
        }

        public void FreeRendererGroupInstances(NativeArray<int>.ReadOnly rendererGroupIDs)
        {
            if (rendererGroupIDs.Length == 0)
                return;

            var instances = new NativeList<InstanceHandle>(rendererGroupIDs.Length, Allocator.TempJob);
            m_BatchersContext.ScheduleQueryRendererGroupInstancesJob(rendererGroupIDs, instances).Complete();
            DestroyDrawInstances(instances.AsArray());
            instances.Dispose();

            m_BatchersContext.FreeRendererGroupInstances(rendererGroupIDs);
        }

        public void UpdateRenderers(RendererGroupInputData.ReadOnly rendererData, bool materialUpdateOnly = false)
        {
            FreeRendererGroupInstances(rendererData.invalidRendererGroupID);
            
            if (rendererData.rendererGroupID.Length == 0)
                return;
            
            Profiler.BeginSample("GPUResidentInstanceBatcher.UpdateRendererInstancesAndBatches");
            {
                // --------------------------------------------------------------------------------------------------------------------------------------
                // Allocate and Update CPU instance data
                // --------------------------------------------------------------------------------------------------------------------------------------
                var instances = new NativeArray<InstanceHandle>(rendererData.localToWorldMatrix.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                Profiler.BeginSample("AllocateInstanceData");
                {
                    m_BatchersContext.ReallocateAndGetInstances(rendererData, instances);
                    var updateInstanceDataJob = m_BatchersContext.ScheduleUpdateInstanceDataJob(instances, rendererData);

                    // --------------------------------------------------------------------------------------------------------------------------------------
                    // Update and Upload Transform data to GPU
                    // ----------------------------------------------------------------------------------------------------------------------------------
                    updateInstanceDataJob.Complete();

                    m_BatchersContext.UpdateGpuInstanceBufferData(instances);
                    // m_BatchersContext.InitializeInstanceTransforms(instances, rendererData.localToWorldMatrix, rendererData.localToWorldMatrix);
                }
                Profiler.EndSample();

                // --------------------------------------------------------------------------------------------------------------------------------------
                // Instance culling batcher
                // --------------------------------------------------------------------------------------------------------------------------------------

                Profiler.BeginSample("InstanceCullingBatcher.BuildBatch");
                {
                    BuildBatch(
                        instances,
                        rendererData.materialID,
                        rendererData.meshID,
                        rendererData, true);

                }
                Profiler.EndSample();

                instances.Dispose();
            }
            Profiler.EndSample();
        }
    }
}