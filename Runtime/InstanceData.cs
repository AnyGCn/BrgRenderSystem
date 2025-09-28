using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework.Constraints;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace BrgRenderSystem
{
    public struct SubMeshDescriptor
    {
        /// <summary>
        ///   <para>Bounding box of vertices in local space.</para>
        /// </summary>
        public Bounds bounds { get; set; }

        /// <summary>
        ///   <para>Face topology of this sub-mesh.</para>
        /// </summary>
        public MeshTopology topology { get; set; }

        /// <summary>
        ///   <para>Starting point inside the whole Mesh index buffer where the face index data is found.</para>
        /// </summary>
        public int indexStart { get; set; }

        /// <summary>
        ///   <para>Index count for this sub-mesh face data.</para>
        /// </summary>
        public int indexCount { get; set; }

        /// <summary>
        ///   <para>Offset that is added to each value in the index buffer, to compute the final vertex index.</para>
        /// </summary>
        public int baseVertex { get; set; }

        /// <summary>
        ///   <para>First vertex in the index buffer for this sub-mesh.</para>
        /// </summary>
        public int firstVertex { get; set; }

        /// <summary>
        ///   <para>Number of vertices used by the index buffer of this sub-mesh.</para>
        /// </summary>
        public int vertexCount { get; set; }

        public SubMeshDescriptor(int indexStart, int indexCount, MeshTopology topology = MeshTopology.Triangles)
        {
            this.bounds = new Bounds();
            this.topology = topology;
            this.indexStart = indexStart;
            this.indexCount = indexCount;
            this.baseVertex = 0;
            this.firstVertex = 0;
            this.vertexCount = 0;
        }

        public override string ToString()
        {
            return $"(topo={this.topology} indices={this.indexStart},{this.indexCount} vertices={this.firstVertex},{this.vertexCount} basevtx={this.baseVertex} bounds={this.bounds})";
        }
    }

    public struct GPUDrivenPackedMaterialData
    {
        private uint data;

        public bool isTransparent => (this.data & 1U) > 0U;

        public bool isMotionVectorsPassEnabled => (this.data & 2U) > 0U;

        public bool isIndirectSupported => (this.data & 4U) > 0U;

        public GPUDrivenPackedMaterialData(
            bool isTransparent,
            bool isMotionVectorsPassEnabled,
            bool isIndirectSupported)
        {
            this.data = isTransparent ? 1U : 0U;
            this.data |= isMotionVectorsPassEnabled ? 2U : 0U;
            this.data |= isIndirectSupported ? 4U : 0U;
        }

        public bool Equals(GPUDrivenPackedMaterialData other)
        {
            return ((int) other.data & 7) == ((int) this.data & 7);
        }

        public static implicit operator uint(GPUDrivenPackedMaterialData other)
        {
            return other.data;
        }
        
        public static implicit operator GPUDrivenPackedMaterialData(uint other)
        {
            return new GPUDrivenPackedMaterialData() { data = other };
        }
    }
    
    public struct GPUDrivenPackedRendererData
    {
        private uint data;

        public bool receiveShadows => (this.data & 1U) > 0U;

        public bool staticShadowCaster => (this.data & 2U) > 0U;

        public byte lodMask => (byte) (this.data >> 2 & (uint) byte.MaxValue);

        public ShadowCastingMode shadowCastingMode => (ShadowCastingMode) ((int) (this.data >> 10) & 3);

        public LightProbeUsage lightProbeUsage => (LightProbeUsage) ((int) (this.data >> 12) & 7);

        public MotionVectorGenerationMode motionVecGenMode
        {
            get => (MotionVectorGenerationMode) ((int) (this.data >> 15) & 3);
        }

        public bool isPartOfStaticBatch => (this.data & 131072U /*0x020000*/) > 0U;

        public bool movedCurrentFrame => (this.data & 262144U /*0x040000*/) > 0U;

        public bool hasTree => (this.data & 524288U /*0x080000*/) > 0U;

        public bool smallMeshCulling => (this.data & 1048576U /*0x100000*/) > 0U;

        public bool supportsIndirect => (this.data & 2097152U /*0x200000*/) > 0U;

        public GPUDrivenPackedRendererData(
            bool receiveShadows,
            bool staticShadowCaster,
            byte lodMask,
            ShadowCastingMode shadowCastingMode,
            LightProbeUsage lightProbeUsage,
            MotionVectorGenerationMode motionVecGenMode,
            bool isPartOfStaticBatch,
            bool movedCurrentFrame,
            bool hasTree,
            bool smallMeshCulling,
            bool supportsIndirect)
        {
            this.data = receiveShadows ? 1U : 0U;
            this.data |= staticShadowCaster ? 2U : 0U;
            this.data |= (uint) lodMask << 2;
            this.data |= (uint) shadowCastingMode << 10;
            this.data |= (uint) lightProbeUsage << 12;
            this.data |= (uint) motionVecGenMode << 15;
            this.data |= isPartOfStaticBatch ? 131072U /*0x020000*/ : 0U;
            this.data |= movedCurrentFrame ? 262144U /*0x040000*/ : 0U;
            this.data |= hasTree ? 524288U /*0x080000*/ : 0U;
            this.data |= smallMeshCulling ? 1048576U /*0x100000*/ : 0U;
            this.data |= supportsIndirect ? 2097152U /*0x200000*/ : 0U;
        }
    }
    
    internal struct RendererGroupInputData
    {
        public NativeList<int> rendererGroupID;
        public NativeList<Bounds> localBounds;
        // public NativeList<Vector4> lightmapScaleOffset;
        public NativeList<short> gameObjectLayer;
        public NativeList<uint> renderingLayerMask;
        public NativeList<int> lodGroupID;
        // public NativeList<int> lightmapIndex;
        public NativeList<GPUDrivenPackedRendererData> packedRendererData;
        // public NativeList<int> rendererPriority;
        public NativeList<int> meshIndex;
        // public NativeList<short> subMeshStartIndex;
        public NativeList<int> materialsOffset;
        public NativeList<short> materialsCount;
        // public NativeList<int> instancesOffset;
        // public NativeList<int> instancesCount;
        // public NativeList<GPUDrivenRendererEditorData> editorData;
        public NativeList<int> invalidRendererGroupID;
        public NativeList<Matrix4x4> localToWorldMatrix;
        public NativeList<Matrix4x4> prevLocalToWorldMatrix;
        // public NativeList<int> rendererGroupIndex;
        public NativeList<int> meshID;
        public NativeList<short> subMeshCount;
        // public NativeList<int> subMeshDescOffset;
        // public NativeList<SubMeshDescriptor> subMeshDesc;
        public NativeList<int> materialIndex;
        public NativeList<int> materialID;
        public NativeList<GPUDrivenPackedMaterialData> packedMaterialData;
        // public NativeList<int> materialFilterFlags;
        
        public void Initialize(int initCapacity)
        {
            this.rendererGroupID = new NativeList<int>(initCapacity, Allocator.Persistent);
            this.localBounds = new NativeList<Bounds>(initCapacity, Allocator.Persistent);
            // this.lightmapScaleOffset = new NativeList<Vector4>(initCapacity, Allocator.Persistent);
            this.gameObjectLayer = new NativeList<short>(initCapacity, Allocator.Persistent);
            this.renderingLayerMask = new NativeList<uint>(initCapacity, Allocator.Persistent);
            this.lodGroupID = new NativeList<int>(initCapacity, Allocator.Persistent);
            // this.lightmapIndex = new NativeList<int>(initCapacity, Allocator.Persistent);
            this.packedRendererData = new NativeList<GPUDrivenPackedRendererData>(initCapacity, Allocator.Persistent);
            // this.rendererPriority = new NativeList<int>(initCapacity, Allocator.Persistent);
            this.meshIndex = new NativeList<int>(initCapacity, Allocator.Persistent);
            // this.subMeshStartIndex = new NativeList<short>(initCapacity, Allocator.Persistent);
            this.materialsOffset = new NativeList<int>(initCapacity, Allocator.Persistent);
            this.materialsCount = new NativeList<short>(initCapacity, Allocator.Persistent);
            // this.instancesOffset = new NativeList<int>(initCapacity, Allocator.Persistent);
            // this.instancesCount = new NativeList<int>(initCapacity, Allocator.Persistent);
            // this.editorData = new NativeList<GPUDrivenRendererEditorData>(initCapacity, Allocator.Persistent);
            this.invalidRendererGroupID = new NativeList<int>(initCapacity, Allocator.Persistent);
            this.localToWorldMatrix = new NativeList<Matrix4x4>(initCapacity, Allocator.Persistent);
            this.prevLocalToWorldMatrix = new NativeList<Matrix4x4>(initCapacity, Allocator.Persistent);
            // this.rendererGroupIndex = new NativeList<int>(initCapacity, Allocator.Persistent);
            this.meshID = new NativeList<int>(initCapacity, Allocator.Persistent);
            this.subMeshCount = new NativeList<short>(initCapacity, Allocator.Persistent);
            // this.subMeshDescOffset = new NativeList<int>(initCapacity, Allocator.Persistent);
            // this.subMeshDesc = new NativeList<SubMeshDescriptor>(initCapacity, Allocator.Persistent);
            this.materialIndex = new NativeList<int>(initCapacity, Allocator.Persistent);
            this.materialID = new NativeList<int>(initCapacity, Allocator.Persistent);
            this.packedMaterialData = new NativeList<GPUDrivenPackedMaterialData>(initCapacity, Allocator.Persistent);
            // this.materialFilterFlags = new NativeList<int>(initCapacity, Allocator.Persistent);
        }

        public void Dispose()
        {
            this.rendererGroupID.Dispose();
            this.localBounds.Dispose();
            // this.lightmapScaleOffset.Dispose();
            this.gameObjectLayer.Dispose();
            this.renderingLayerMask.Dispose();
            this.lodGroupID.Dispose();
            // this.lightmapIndex.Dispose();
            this.packedRendererData.Dispose();
            // this.rendererPriority.Dispose();
            this.meshIndex.Dispose();
            // this.subMeshStartIndex.Dispose();
            this.materialsOffset.Dispose();
            this.materialsCount.Dispose();
            // this.instancesOffset.Dispose();
            // this.instancesCount.Dispose();
            // this.editorData.Dispose();
            this.invalidRendererGroupID.Dispose();
            this.localToWorldMatrix.Dispose();
            this.prevLocalToWorldMatrix.Dispose();
            // this.rendererGroupIndex.Dispose();
            this.meshID.Dispose();
            this.subMeshCount.Dispose();
            // this.subMeshDescOffset.Dispose();
            // this.subMeshDesc.Dispose();
            this.materialIndex.Dispose();
            this.materialID.Dispose();
            this.packedMaterialData.Dispose();
            // this.materialFilterFlags.Dispose();
        }
        
        public void Clear()
        {
            this.rendererGroupID.Clear();
            this.localBounds.Clear();
            // this.lightmapScaleOffset.Clear();
            this.gameObjectLayer.Clear();
            this.renderingLayerMask.Clear();
            this.lodGroupID.Clear();
            // this.lightmapIndex.Clear();
            this.packedRendererData.Clear();
            // this.rendererPriority.Clear();
            this.meshIndex.Clear();
            // this.subMeshStartIndex.Clear();
            this.materialsOffset.Clear();
            this.materialsCount.Clear();
            // this.instancesOffset.Clear();
            // this.instancesCount.Clear();
            // this.editorData.Clear();
            this.invalidRendererGroupID.Clear();
            this.localToWorldMatrix.Clear();
            this.prevLocalToWorldMatrix.Clear();
            // this.rendererGroupIndex.Clear();
            this.meshID.Clear();
            this.subMeshCount.Clear();
            // this.subMeshDescOffset.Clear();
            // this.subMeshDesc.Clear();
            this.materialIndex.Clear();
            this.materialID.Clear();
            this.packedMaterialData.Clear();
            // this.materialFilterFlags.Clear();
        }
        
        public ReadOnly AsReadOnly()
        {
            return new ReadOnly(this);
        }

        public struct ReadOnly 
        {
            public NativeArray<int>.ReadOnly rendererGroupID;
            public NativeArray<Bounds>.ReadOnly localBounds;
            // public NativeArray<Vector4>.ReadOnly lightmapScaleOffset;
            public NativeArray<short>.ReadOnly gameObjectLayer;
            public NativeArray<uint>.ReadOnly renderingLayerMask;
            public NativeArray<int>.ReadOnly lodGroupID;
            // public NativeArray<int>.ReadOnly lightmapIndex;
            public NativeArray<GPUDrivenPackedRendererData>.ReadOnly packedRendererData;
            // public NativeArray<int>.ReadOnly rendererPriority;
            public NativeArray<int>.ReadOnly meshIndex;
            // public NativeArray<short>.ReadOnly subMeshStartIndex;
            public NativeArray<int>.ReadOnly materialsOffset;
            public NativeArray<short>.ReadOnly materialsCount;
            // public NativeArray<int>.ReadOnly instancesOffset;
            // public NativeArray<int>.ReadOnly instancesCount;
            // public NativeArray<GPUDrivenRendererEditorData>.ReadOnly editorData;
            public NativeArray<int>.ReadOnly invalidRendererGroupID;
            public NativeArray<Matrix4x4>.ReadOnly localToWorldMatrix;
            // public NativeArray<Matrix4x4>.ReadOnly prevLocalToWorldMatrix;
            // public NativeArray<int>.ReadOnly rendererGroupIndex;
            public NativeArray<int>.ReadOnly meshID;
            public NativeArray<short>.ReadOnly subMeshCount;
            // public NativeArray<int>.ReadOnly subMeshDescOffset;
            // public NativeArray<SubMeshDescriptor>.ReadOnly subMeshDesc;
            public NativeArray<int>.ReadOnly materialIndex;
            public NativeArray<int>.ReadOnly materialID;
            public NativeArray<GPUDrivenPackedMaterialData>.ReadOnly packedMaterialData;
            // public NativeArray<int>.ReadOnly materialFilterFlags;

            public ReadOnly(RendererGroupInputData inputData)
            {
                rendererGroupID = inputData.rendererGroupID.AsArray().AsReadOnly();
                localBounds = inputData.localBounds.AsArray().AsReadOnly();
                // lightmapScaleOffset = inputData.lightmapScaleOffset.AsArray().AsReadOnly();
                gameObjectLayer = inputData.gameObjectLayer.AsArray().AsReadOnly();
                renderingLayerMask = inputData.renderingLayerMask.AsArray().AsReadOnly();
                lodGroupID = inputData.lodGroupID.AsArray().AsReadOnly();
                // lightmapIndex = inputData.lightmapIndex.AsArray().AsReadOnly();
                packedRendererData = inputData.packedRendererData.AsArray().AsReadOnly();
                // rendererPriority = inputData.rendererPriority.AsArray().AsReadOnly();
                meshIndex = inputData.meshIndex.AsArray().AsReadOnly();
                // subMeshStartIndex = inputData.subMeshStartIndex.AsArray().AsReadOnly();
                materialsOffset = inputData.materialsOffset.AsArray().AsReadOnly();
                materialsCount = inputData.materialsCount.AsArray().AsReadOnly();
                // instancesOffset = inputData.instancesOffset.AsArray().AsReadOnly();
                // instancesCount = inputData.instancesCount.AsArray().AsReadOnly();
                // editorData = inputData.editorData.AsArray().AsReadOnly();
                invalidRendererGroupID = inputData.invalidRendererGroupID.AsArray().AsReadOnly();
                localToWorldMatrix = inputData.localToWorldMatrix.AsArray().AsReadOnly();
                // prevLocalToWorldMatrix = inputData.prevLocalToWorldMatrix.AsArray().AsReadOnly();
                // rendererGroupIndex = inputData.rendererGroupIndex.AsArray().AsReadOnly();
                meshID = inputData.meshID.AsArray().AsReadOnly();
                subMeshCount = inputData.subMeshCount.AsArray().AsReadOnly();
                // subMeshDescOffset = inputData.subMeshDescOffset.AsArray().AsReadOnly();
                // subMeshDesc = inputData.subMeshDesc.AsArray().AsReadOnly();
                materialIndex = inputData.materialIndex.AsArray().AsReadOnly();
                materialID = inputData.materialID.AsArray().AsReadOnly();
                packedMaterialData = inputData.packedMaterialData.AsArray().AsReadOnly();
                // materialFilterFlags = inputData.materialFilterFlags.AsArray().AsReadOnly();
            }
        }
    }
    
    internal struct CPUInstanceData : IDisposable
    {
        private const int k_InvalidIndex = -1;
        private const uint k_InvalidLODGroupAndMask = 0xFFFFFFFF;

        private NativeArray<int> m_StructData;
        private NativeList<int> m_InstanceIndices;

        public NativeArray<InstanceHandle> instances;
        public NativeArray<SharedInstanceHandle> sharedInstances;
        public NativeArray<GPUInstanceIndex> gpuInstances;
        public ParallelBitArray localToWorldIsFlippedBits;
        public NativeArray<uint> lodGroupAndMasks;
        public NativeArray<PackedMatrix> localToWorlds;
        public NativeArray<PackedMatrix> worldToLocals;
        public NativeArray<AABB> worldAABBs;
        // public NativeArray<int> tetrahedronCacheIndices;
        // public ParallelBitArray movedInCurrentFrameBits;
        // public ParallelBitArray movedInPreviousFrameBits;
        // public ParallelBitArray visibleInPreviousFrameBits;
        public EditorInstanceDataArrays editorData;

        public int instancesLength { get => m_StructData[0]; set => m_StructData[0] = value; }
        public int instancesCapacity { get => m_StructData[1]; set => m_StructData[1] = value; }
        public int handlesLength => m_InstanceIndices.Length;

        public void Initialize(int initCapacity)
        {
            m_StructData = new NativeArray<int>(2, Allocator.Persistent);
            instancesCapacity = initCapacity;
            m_InstanceIndices = new NativeList<int>(Allocator.Persistent);
            instances = new NativeArray<InstanceHandle>(instancesCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            instances.FillArray(InstanceHandle.Invalid);
            sharedInstances = new NativeArray<SharedInstanceHandle>(instancesCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            sharedInstances.FillArray(SharedInstanceHandle.Invalid);
            gpuInstances = new NativeArray<GPUInstanceIndex>(instancesCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            gpuInstances.FillArray(GPUInstanceIndex.Invalid);
            localToWorldIsFlippedBits = new ParallelBitArray(instancesCapacity, Allocator.Persistent);
            lodGroupAndMasks = new NativeArray<uint>(instancesCapacity, Allocator.Persistent);
            lodGroupAndMasks.FillArray(k_InvalidLODGroupAndMask);
            localToWorlds = new NativeArray<PackedMatrix>(instancesCapacity, Allocator.Persistent);
            worldToLocals = new NativeArray<PackedMatrix>(instancesCapacity, Allocator.Persistent);
            worldAABBs = new NativeArray<AABB>(instancesCapacity, Allocator.Persistent);
            // tetrahedronCacheIndices = new NativeArray<int>(instancesCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            // tetrahedronCacheIndices.FillArray(k_InvalidIndex);
            // movedInCurrentFrameBits = new ParallelBitArray(instancesCapacity, Allocator.Persistent);
            // movedInPreviousFrameBits = new ParallelBitArray(instancesCapacity, Allocator.Persistent);
            // visibleInPreviousFrameBits = new ParallelBitArray(instancesCapacity, Allocator.Persistent);
            editorData.Initialize(initCapacity);
        }

        public void Dispose()
        {
            m_StructData.Dispose();
            m_InstanceIndices.Dispose();
            instances.Dispose();
            sharedInstances.Dispose();
            gpuInstances.Dispose();
            localToWorldIsFlippedBits.Dispose();
            lodGroupAndMasks.Dispose();
            localToWorlds.Dispose();
            worldToLocals.Dispose();
            worldAABBs.Dispose();
            // tetrahedronCacheIndices.Dispose();
            // movedInCurrentFrameBits.Dispose();
            // movedInPreviousFrameBits.Dispose();
            // visibleInPreviousFrameBits.Dispose();
            editorData.Dispose();
        }

        private void Grow(int newCapacity)
        {
            Assert.IsTrue(newCapacity > instancesCapacity);

            instances.ResizeArray(newCapacity);
            instances.FillArray(InstanceHandle.Invalid, instancesCapacity);
            sharedInstances.ResizeArray(newCapacity);
            sharedInstances.FillArray(SharedInstanceHandle.Invalid, instancesCapacity);
            gpuInstances.ResizeArray(newCapacity);
            gpuInstances.FillArray(GPUInstanceIndex.Invalid, instancesCapacity);
            localToWorldIsFlippedBits.Resize(newCapacity);
            lodGroupAndMasks.ResizeArray(newCapacity);
            lodGroupAndMasks.FillArray(k_InvalidLODGroupAndMask, instancesCapacity);
            localToWorlds.ResizeArray(newCapacity);
            worldToLocals.ResizeArray(newCapacity);
            worldAABBs.ResizeArray(newCapacity);
            // tetrahedronCacheIndices.ResizeArray(newCapacity);
            // tetrahedronCacheIndices.FillArray(k_InvalidIndex, instancesCapacity);
            // movedInCurrentFrameBits.Resize(newCapacity);
            // movedInPreviousFrameBits.Resize(newCapacity);
            // visibleInPreviousFrameBits.Resize(newCapacity);
            editorData.Grow(newCapacity);

            instancesCapacity = newCapacity;
        }

        private void AddUnsafe(InstanceHandle instance)
        {
            if (instance.index >= m_InstanceIndices.Length)
            {
                int prevLength = m_InstanceIndices.Length;
                m_InstanceIndices.ResizeUninitialized(instance.index + 1);

                for (int i = prevLength; i < m_InstanceIndices.Length - 1; ++i)
                    m_InstanceIndices[i] = k_InvalidIndex;
            }

            m_InstanceIndices[instance.index] = instancesLength;
            instances[instancesLength] = instance;

            ++instancesLength;
        }

        public int InstanceToIndex(InstanceHandle instance)
        {
            Assert.IsTrue(IsValidInstance(instance));
            return m_InstanceIndices[instance.index];
        }

        public InstanceHandle IndexToInstance(int index)
        {
            Assert.IsTrue(IsValidIndex(index));
            return instances[index];
        }

        public bool IsValidInstance(InstanceHandle instance)
        {
            if (instance.valid && instance.index < m_InstanceIndices.Length)
            {
                int index = m_InstanceIndices[instance.index];
                return index >= 0 && index < instancesLength && instances[index].Equals(instance);
            }
            return false;
        }

        public bool IsFreeInstanceHandle(InstanceHandle instance)
        {
            return instance.valid && (instance.index >= m_InstanceIndices.Length || m_InstanceIndices[instance.index] == k_InvalidIndex);
        }

        public bool IsValidIndex(int index)
        {
            if (index >= 0 && index < instancesLength)
            {
                InstanceHandle instance = instances[index];
                return index == m_InstanceIndices[instance.index];
            }
            return false;
        }

        public int GetFreeInstancesCount()
        {
            return instancesCapacity - instancesLength;
        }

        public void EnsureFreeInstances(int instancesCount)
        {
            int freeInstancesCount = GetFreeInstancesCount();
            int needInstances = instancesCount - freeInstancesCount;

            if (needInstances > 0)
                Grow(instancesCapacity + needInstances + 256);
        }

        public void AddNoGrow(InstanceHandle instance)
        {
            Assert.IsTrue(instance.valid);
            Assert.IsTrue(IsFreeInstanceHandle(instance));
            Assert.IsTrue(GetFreeInstancesCount() > 0);

            AddUnsafe(instance);
            SetDefault(instance);
        }

        public void Add(InstanceHandle instance)
        {
            EnsureFreeInstances(1);
            AddNoGrow(instance);
        }

        public void Remove(InstanceHandle instance)
        {
            Assert.IsTrue(IsValidInstance(instance));

            int index = InstanceToIndex(instance);
            int lastIndex = instancesLength - 1;

            instances[index] = instances[lastIndex];
            sharedInstances[index] = sharedInstances[lastIndex];
            gpuInstances[index] = gpuInstances[lastIndex];
            localToWorldIsFlippedBits.Set(index, localToWorldIsFlippedBits.Get(lastIndex));
            lodGroupAndMasks[index] = lodGroupAndMasks[lastIndex];
            localToWorlds[index] = localToWorlds[lastIndex];
            worldToLocals[index] = worldToLocals[lastIndex];
            worldAABBs[index] = worldAABBs[lastIndex];
            // tetrahedronCacheIndices[index] = tetrahedronCacheIndices[lastIndex];
            // movedInCurrentFrameBits.Set(index, movedInCurrentFrameBits.Get(lastIndex));
            // movedInPreviousFrameBits.Set(index, movedInPreviousFrameBits.Get(lastIndex));
            // visibleInPreviousFrameBits.Set(index, visibleInPreviousFrameBits.Get(lastIndex));
            editorData.Remove(index, lastIndex);

            m_InstanceIndices[instances[lastIndex].index] = index;
            m_InstanceIndices[instance.index] = k_InvalidIndex;
            instancesLength -= 1;
        }

        public void Set(InstanceHandle instance, SharedInstanceHandle sharedInstance, bool localToWorldIsFlipped, uint lodGroupAndMask, in AABB worldAABB, int tetrahedronCacheIndex,
            bool movedInCurrentFrame, bool movedInPreviousFrame, bool visibleInPreviousFrame)
        {
            int index = InstanceToIndex(instance);
            sharedInstances[index] = sharedInstance;
            localToWorldIsFlippedBits.Set(index, localToWorldIsFlipped);
            lodGroupAndMasks[index] = lodGroupAndMask;
            localToWorlds[index] = default;
            worldToLocals[index] = default;
            worldAABBs[index] = worldAABB;
            // tetrahedronCacheIndices[index] = tetrahedronCacheIndex;
            // movedInCurrentFrameBits.Set(index, movedInCurrentFrame);
            // movedInPreviousFrameBits.Set(index, movedInPreviousFrame);
            // visibleInPreviousFrameBits.Set(index, visibleInPreviousFrame);
            editorData.SetDefault(index);
        }

        public void SetDefault(InstanceHandle instance)
        {
            Set(instance, SharedInstanceHandle.Invalid, false, k_InvalidLODGroupAndMask, new AABB(), k_InvalidIndex, false, false, false);
        }

        // These accessors just for convenience and additional safety.
        // In general prefer converting an instance to an index and access by index.
        public SharedInstanceHandle Get_SharedInstance(InstanceHandle instance) { return sharedInstances[InstanceToIndex(instance)]; }
        public bool Get_LocalToWorldIsFlipped(InstanceHandle instance) { return localToWorldIsFlippedBits.Get(InstanceToIndex(instance)); }
        public uint Get_LODGroupAndMask(InstanceHandle instance) { return lodGroupAndMasks[InstanceToIndex(instance)]; }
        public AABB Get_WorldAABB(InstanceHandle instance) { return worldAABBs[InstanceToIndex(instance)]; }
        // public int Get_TetrahedronCacheIndex(InstanceHandle instance) { return tetrahedronCacheIndices[InstanceToIndex(instance)]; }
        public unsafe ref AABB Get_WorldBounds(InstanceHandle instance) { return ref UnsafeUtility.ArrayElementAsRef<AABB>(worldAABBs.GetUnsafePtr(), InstanceToIndex(instance)); }
        // public bool Get_MovedInCurrentFrame(InstanceHandle instance) { return movedInCurrentFrameBits.Get(InstanceToIndex(instance)); }
        // public bool Get_MovedInPreviousFrame(InstanceHandle instance) { return movedInPreviousFrameBits.Get(InstanceToIndex(instance)); }
        // public bool Get_VisibleInPreviousFrame(InstanceHandle instance) { return visibleInPreviousFrameBits.Get(InstanceToIndex(instance)); }

        public void Set_SharedInstance(InstanceHandle instance, SharedInstanceHandle sharedInstance) { sharedInstances[InstanceToIndex(instance)] = sharedInstance; }
        public void Set_LocalToWorldIsFlipped(InstanceHandle instance, bool isFlipped) { localToWorldIsFlippedBits.Set(InstanceToIndex(instance), isFlipped); }
        public void Set_LODGroupAndMask(InstanceHandle instance, uint lodGroupAndMask) { lodGroupAndMasks[InstanceToIndex(instance)] = lodGroupAndMask; }
        public void Set_WorldAABB(InstanceHandle instance, in AABB worldBounds) { worldAABBs[InstanceToIndex(instance)] = worldBounds; }
        // public void Set_TetrahedronCacheIndex(InstanceHandle instance, int tetrahedronCacheIndex) { tetrahedronCacheIndices[InstanceToIndex(instance)] = tetrahedronCacheIndex; }
        // public void Set_MovedInCurrentFrame(InstanceHandle instance, bool movedInCurrentFrame) { movedInCurrentFrameBits.Set(InstanceToIndex(instance), movedInCurrentFrame); }
        // public void Set_MovedInPreviousFrame(InstanceHandle instance, bool movedInPreviousFrame) { movedInPreviousFrameBits.Set(InstanceToIndex(instance), movedInPreviousFrame); }
        // public void Set_VisibleInPreviousFrame(InstanceHandle instance, bool visibleInPreviousFrame) { visibleInPreviousFrameBits.Set(InstanceToIndex(instance), visibleInPreviousFrame); }

        public ReadOnly AsReadOnly()
        {
            return new ReadOnly(this);
        }

        internal readonly struct ReadOnly
        {
            public readonly NativeArray<int>.ReadOnly instanceIndices;
            public readonly NativeArray<InstanceHandle>.ReadOnly instances;
            public readonly NativeArray<SharedInstanceHandle>.ReadOnly sharedInstances;
            public readonly NativeArray<GPUInstanceIndex>.ReadOnly gpuInstances;
            public readonly ParallelBitArray localToWorldIsFlippedBits;
            public readonly NativeArray<uint>.ReadOnly lodGroupAndMasks;
            public readonly NativeArray<AABB> worldAABBs;
            // public readonly NativeArray<int>.ReadOnly tetrahedronCacheIndices;
            // public readonly ParallelBitArray movedInCurrentFrameBits;
            // public readonly ParallelBitArray movedInPreviousFrameBits;
            // public readonly ParallelBitArray visibleInPreviousFrameBits;
            public readonly EditorInstanceDataArrays.ReadOnly editorData;
            public readonly int handlesLength => instanceIndices.Length;
            public readonly int instancesLength => instances.Length;

            public ReadOnly(in CPUInstanceData instanceData)
            {
                instanceIndices = instanceData.m_InstanceIndices.AsArray().AsReadOnly();
                instances = instanceData.instances.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
                sharedInstances = instanceData.sharedInstances.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
                gpuInstances = instanceData.gpuInstances.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
                localToWorldIsFlippedBits = instanceData.localToWorldIsFlippedBits.GetSubArray(instanceData.instancesLength);
                lodGroupAndMasks = instanceData.lodGroupAndMasks.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
                worldAABBs = instanceData.worldAABBs.GetSubArray(0, instanceData.instancesLength);
                // tetrahedronCacheIndices = instanceData.tetrahedronCacheIndices.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
                // movedInCurrentFrameBits = instanceData.movedInCurrentFrameBits.GetSubArray(instanceData.instancesLength);//.AsReadOnly(); // Implement later.
                // movedInPreviousFrameBits = instanceData.movedInPreviousFrameBits.GetSubArray(instanceData.instancesLength);//.AsReadOnly(); // Implement later.
                // visibleInPreviousFrameBits = instanceData.visibleInPreviousFrameBits.GetSubArray(instanceData.instancesLength);//.AsReadOnly(); // Implement later.
                editorData = new EditorInstanceDataArrays.ReadOnly(instanceData);
            }

            public int InstanceToIndex(InstanceHandle instance)
            {
                Assert.IsTrue(IsValidInstance(instance));
                return instanceIndices[instance.index];
            }

            public InstanceHandle IndexToInstance(int index)
            {
                Assert.IsTrue(IsValidIndex(index));
                return instances[index];
            }

            public bool IsValidInstance(InstanceHandle instance)
            {
                if (instance.valid && instance.index < instanceIndices.Length)
                {
                    int index = instanceIndices[instance.index];
                    return index >= 0 && index < instances.Length && instances[index].Equals(instance);
                }
                return false;
            }

            public bool IsValidIndex(int index)
            {
                if (index >= 0 && index < instances.Length)
                {
                    InstanceHandle instance = instances[index];
                    return index == instanceIndices[instance.index];
                }
                return false;
            }
        }
    }

    internal struct CPUSharedInstanceData : IDisposable
    {
        private const int k_InvalidIndex = -1;
        // private const uint k_InvalidLODGroupAndMask = 0xFFFFFFFF;

        private NativeArray<int> m_StructData;
        private NativeList<int> m_InstanceIndices;

        //@ Need to figure out the way to share the code with CPUInstanceData. Both structures are almost identical.
        public NativeArray<SharedInstanceHandle> instances;
        public NativeArray<int> rendererGroupIDs;

        // For now we just use nested collections since materialIDs are only parsed rarely. E.g. when an unsupported material is detected.
        public NativeArray<SmallIntegerArray> materialIDArrays;
        
        public NativeArray<int> meshIDs;
        public NativeArray<AABB> localAABBs;
        public NativeArray<CPUSharedInstanceFlags> flags;
        // public NativeArray<uint> lodGroupAndMasks;
        public NativeArray<short> gameObjectLayers;
        public NativeArray<uint> renderingLayerMasks;
        public NativeArray<int> refCounts;

        public int instancesLength { get => m_StructData[0]; set => m_StructData[0] = value; }
        public int instancesCapacity { get => m_StructData[1]; set => m_StructData[1] = value; }
        public int handlesLength => m_InstanceIndices.Length;

        public void Initialize(int initCapacity)
        {
            m_StructData = new NativeArray<int>(2, Allocator.Persistent);
            instancesCapacity = initCapacity;
            m_InstanceIndices = new NativeList<int>(Allocator.Persistent);
            instances = new NativeArray<SharedInstanceHandle>(instancesCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            instances.FillArray(SharedInstanceHandle.Invalid);
            rendererGroupIDs = new NativeArray<int>(instancesCapacity, Allocator.Persistent);
            materialIDArrays = new NativeArray<SmallIntegerArray>(instancesCapacity, Allocator.Persistent);
            meshIDs = new NativeArray<int>(instancesCapacity, Allocator.Persistent);
            localAABBs = new NativeArray<AABB>(instancesCapacity, Allocator.Persistent);
            flags = new NativeArray<CPUSharedInstanceFlags>(instancesCapacity, Allocator.Persistent);
            // lodGroupAndMasks = new NativeArray<uint>(instancesCapacity, Allocator.Persistent);
            // lodGroupAndMasks.FillArray(k_InvalidLODGroupAndMask);
            gameObjectLayers = new NativeArray<short>(instancesCapacity, Allocator.Persistent);
            renderingLayerMasks = new NativeArray<uint>(instancesCapacity, Allocator.Persistent);
            refCounts = new NativeArray<int>(instancesCapacity, Allocator.Persistent);
        }

        public void Dispose()
        {
            m_StructData.Dispose();
            m_InstanceIndices.Dispose();
            instances.Dispose();
            rendererGroupIDs.Dispose();
            materialIDArrays.Dispose();

            meshIDs.Dispose();
            localAABBs.Dispose();
            flags.Dispose();
            // lodGroupAndMasks.Dispose();
            gameObjectLayers.Dispose();
            renderingLayerMasks.Dispose();
            refCounts.Dispose();
        }

        private void Grow(int newCapacity)
        {
            Assert.IsTrue(newCapacity > instancesCapacity);

            instances.ResizeArray(newCapacity);
            instances.FillArray(SharedInstanceHandle.Invalid, instancesCapacity);
            rendererGroupIDs.ResizeArray(newCapacity);
            materialIDArrays.ResizeArray(newCapacity);
            materialIDArrays.FillArray(default, instancesCapacity);
            meshIDs.ResizeArray(newCapacity);
            localAABBs.ResizeArray(newCapacity);
            flags.ResizeArray(newCapacity);
            // lodGroupAndMasks.ResizeArray(newCapacity);
            // lodGroupAndMasks.FillArray(k_InvalidLODGroupAndMask, instancesCapacity);
            gameObjectLayers.ResizeArray(newCapacity);
            renderingLayerMasks.ResizeArray(newCapacity);
            refCounts.ResizeArray(newCapacity);

            instancesCapacity = newCapacity;
        }

        private void AddUnsafe(SharedInstanceHandle instance)
        {
            if (instance.index >= m_InstanceIndices.Length)
            {
                int prevLength = m_InstanceIndices.Length;
                m_InstanceIndices.ResizeUninitialized(instance.index + 1);

                for (int i = prevLength; i < m_InstanceIndices.Length - 1; ++i)
                    m_InstanceIndices[i] = k_InvalidIndex;
            }

            m_InstanceIndices[instance.index] = instancesLength;
            instances[instancesLength] = instance;

            ++instancesLength;
        }

        public int SharedInstanceToIndex(SharedInstanceHandle instance)
        {
            Assert.IsTrue(IsValidInstance(instance));
            return m_InstanceIndices[instance.index];
        }

        public SharedInstanceHandle IndexToSharedInstance(int index)
        {
            Assert.IsTrue(IsValidIndex(index));
            return instances[index];
        }

        public int InstanceToIndex(in CPUInstanceData instanceData, InstanceHandle instance)
        {
            int instanceIndex = instanceData.InstanceToIndex(instance);
            SharedInstanceHandle sharedInstance = instanceData.sharedInstances[instanceIndex];
            int sharedInstanceIndex = SharedInstanceToIndex(sharedInstance);
            return sharedInstanceIndex;
        }

        public bool IsValidInstance(SharedInstanceHandle instance)
        {
            if (instance.valid && instance.index < m_InstanceIndices.Length)
            {
                int index = m_InstanceIndices[instance.index];
                return index >= 0 && index < instancesLength && instances[index].Equals(instance);
            }
            return false;
        }

        public bool IsFreeInstanceHandle(SharedInstanceHandle instance)
        {
            return instance.valid && (instance.index >= m_InstanceIndices.Length || m_InstanceIndices[instance.index] == k_InvalidIndex);
        }

        public bool IsValidIndex(int index)
        {
            if (index >= 0 && index < instancesLength)
            {
                SharedInstanceHandle instance = instances[index];
                return index == m_InstanceIndices[instance.index];
            }
            return false;
        }

        public int GetFreeInstancesCount()
        {
            return instancesCapacity - instancesLength;
        }

        public void EnsureFreeInstances(int instancesCount)
        {
            int freeInstancesCount = GetFreeInstancesCount();
            int needInstances = instancesCount - freeInstancesCount;

            if (needInstances > 0)
                Grow(instancesCapacity + needInstances + 256);
        }

        public void AddNoGrow(SharedInstanceHandle instance)
        {
            Assert.IsTrue(instance.valid);
            Assert.IsTrue(IsFreeInstanceHandle(instance));
            Assert.IsTrue(GetFreeInstancesCount() > 0);

            AddUnsafe(instance);
            SetDefault(instance);
        }

        public void Add(SharedInstanceHandle instance)
        {
            EnsureFreeInstances(1);
            AddNoGrow(instance);
        }

        public void Remove(SharedInstanceHandle instance)
        {
            Assert.IsTrue(IsValidInstance(instance));

            int index = SharedInstanceToIndex(instance);
            int lastIndex = instancesLength - 1;

            instances[index] = instances[lastIndex];
            rendererGroupIDs[index] = rendererGroupIDs[lastIndex];

            materialIDArrays[index] = materialIDArrays[lastIndex];
            materialIDArrays[lastIndex] = default;

            meshIDs[index] = meshIDs[lastIndex];
            localAABBs[index] = localAABBs[lastIndex];
            flags[index] = flags[lastIndex];
            // lodGroupAndMasks[index] = lodGroupAndMasks[lastIndex];
            gameObjectLayers[index] = gameObjectLayers[lastIndex];
            renderingLayerMasks[index] = renderingLayerMasks[lastIndex];
            refCounts[index] = refCounts[lastIndex];

            m_InstanceIndices[instances[lastIndex].index] = index;
            m_InstanceIndices[instance.index] = k_InvalidIndex;
            instancesLength -= 1;
        }

        // These accessors just for convenience and additional safety.
        // In general prefer converting an instance to an index and access by index.
        public int Get_RendererGroupID(SharedInstanceHandle instance) { return rendererGroupIDs[SharedInstanceToIndex(instance)]; }
        public int Get_MeshID(SharedInstanceHandle instance) { return meshIDs[SharedInstanceToIndex(instance)]; }
        public unsafe ref AABB Get_LocalAABB(SharedInstanceHandle instance) { return ref UnsafeUtility.ArrayElementAsRef<AABB>(localAABBs.GetUnsafePtr(), SharedInstanceToIndex(instance)); }
        public CPUSharedInstanceFlags Get_Flags(SharedInstanceHandle instance) { return flags[SharedInstanceToIndex(instance)]; }
        // public uint Get_LODGroupAndMask(SharedInstanceHandle instance) { return lodGroupAndMasks[SharedInstanceToIndex(instance)]; }
        public short Get_GameObjectLayer(SharedInstanceHandle instance) { return gameObjectLayers[SharedInstanceToIndex(instance)]; }
        public uint Get_RenderingLayerMask(SharedInstanceHandle instance) { return renderingLayerMasks[SharedInstanceToIndex(instance)]; }
        public int Get_RefCount(SharedInstanceHandle instance) { return refCounts[SharedInstanceToIndex(instance)]; }
        public unsafe ref SmallIntegerArray Get_MaterialIDs(SharedInstanceHandle instance) { return ref UnsafeUtility.ArrayElementAsRef<SmallIntegerArray>(materialIDArrays.GetUnsafePtr(), SharedInstanceToIndex(instance)); }

        public void Set_RendererGroupID(SharedInstanceHandle instance, int rendererGroupID) { rendererGroupIDs[SharedInstanceToIndex(instance)] = rendererGroupID; }
        public void Set_MeshID(SharedInstanceHandle instance, int meshID) { meshIDs[SharedInstanceToIndex(instance)] = meshID; }
        public void Set_LocalAABB(SharedInstanceHandle instance, in AABB localAABB) { localAABBs[SharedInstanceToIndex(instance)] = localAABB; }
        public void Set_Flags(SharedInstanceHandle instance, CPUSharedInstanceFlags instanceFlags) { flags[SharedInstanceToIndex(instance)] = instanceFlags; }
        // public void Set_LODGroupAndMask(SharedInstanceHandle instance, uint lodGroupAndMask) { lodGroupAndMasks[SharedInstanceToIndex(instance)] = lodGroupAndMask; }
        public void Set_GameObjectLayer(SharedInstanceHandle instance, short gameObjectLayer) { gameObjectLayers[SharedInstanceToIndex(instance)] = gameObjectLayer; }
        public void Set_RenderingLayerMask(SharedInstanceHandle instance, uint renderingLayerMask) { renderingLayerMasks[SharedInstanceToIndex(instance)] = renderingLayerMask; }
        public void Set_RefCount(SharedInstanceHandle instance, int refCount) { refCounts[SharedInstanceToIndex(instance)] = refCount; }
        public void Set_MaterialIDs(SharedInstanceHandle instance, in SmallIntegerArray materialIDs)
        {
            int index = SharedInstanceToIndex(instance);
            materialIDArrays[index] = materialIDs;
        }

        public void Set(SharedInstanceHandle instance, int rendererGroupID, in SmallIntegerArray materialIDs, int meshID, in AABB localAABB, TransformUpdateFlags transformUpdateFlags,
            InstanceFlags instanceFlags, short gameObjectLayer, uint renderingLayerMask, int refCount)
        {
            int index = SharedInstanceToIndex(instance);

            rendererGroupIDs[index] = rendererGroupID;
            materialIDArrays[index] = materialIDs;
            meshIDs[index] = meshID;
            localAABBs[index] = localAABB;
            flags[index] = new CPUSharedInstanceFlags { instanceFlags = instanceFlags };
            // lodGroupAndMasks[index] = lodGroupAndMask;
            gameObjectLayers[index] = gameObjectLayer;
            renderingLayerMasks[index] = renderingLayerMask;
            refCounts[index] = refCount;
        }

        public void SetDefault(SharedInstanceHandle instance)
        {
            Set(instance, 0, default, 0, new AABB(), TransformUpdateFlags.None, default, 0, 0, 0);
        }

        public ReadOnly AsReadOnly()
        {
            return new ReadOnly(this);
        }

        internal readonly struct ReadOnly
        {
            public readonly NativeArray<int>.ReadOnly instanceIndices;
            public readonly NativeArray<SharedInstanceHandle>.ReadOnly instances;
            public readonly NativeArray<int>.ReadOnly rendererGroupIDs;
            public readonly NativeArray<SmallIntegerArray>.ReadOnly materialIDArrays;
            public readonly NativeArray<int>.ReadOnly meshIDs;
            public readonly NativeArray<AABB>.ReadOnly localAABBs;
            public readonly NativeArray<CPUSharedInstanceFlags>.ReadOnly flags;
            // public readonly NativeArray<uint>.ReadOnly lodGroupAndMasks;
            public readonly NativeArray<short>.ReadOnly gameObjectLayers;
            public readonly NativeArray<uint>.ReadOnly renderingLayerMasks;
            public readonly NativeArray<int>.ReadOnly refCounts;
            public readonly int handlesLength => instanceIndices.Length;
            public readonly int instancesLength => instances.Length;

            public ReadOnly(in CPUSharedInstanceData instanceData)
            {
                instanceIndices = instanceData.m_InstanceIndices.AsArray().AsReadOnly();
                instances = instanceData.instances.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
                rendererGroupIDs = instanceData.rendererGroupIDs.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
                materialIDArrays = instanceData.materialIDArrays.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
                meshIDs = instanceData.meshIDs.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
                localAABBs = instanceData.localAABBs.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
                flags = instanceData.flags.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
                // lodGroupAndMasks = instanceData.lodGroupAndMasks.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
                gameObjectLayers = instanceData.gameObjectLayers.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
                renderingLayerMasks = instanceData.renderingLayerMasks.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
                refCounts = instanceData.refCounts.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
            }

            public int SharedInstanceToIndex(SharedInstanceHandle instance)
            {
                Assert.IsTrue(IsValidSharedInstance(instance));
                return instanceIndices[instance.index];
            }

            public SharedInstanceHandle IndexToSharedInstance(int index)
            {
                Assert.IsTrue(IsValidIndex(index));
                return instances[index];
            }

            public bool IsValidSharedInstance(SharedInstanceHandle instance)
            {
                if (instance.valid && instance.index < instanceIndices.Length)
                {
                    int index = instanceIndices[instance.index];
                    return index >= 0 && index < instances.Length && instances[index].Equals(instance);
                }
                return false;
            }

            public bool IsValidIndex(int index)
            {
                if (index >= 0 && index < instances.Length)
                {
                    SharedInstanceHandle instance = instances[index];
                    return index == instanceIndices[instance.index];
                }
                return false;
            }

            public int InstanceToIndex(in CPUInstanceData.ReadOnly instanceData, InstanceHandle instance)
            {
                int instanceIndex = instanceData.InstanceToIndex(instance);
                SharedInstanceHandle sharedInstance = instanceData.sharedInstances[instanceIndex];
                int sharedInstanceIndex = SharedInstanceToIndex(sharedInstance);
                return sharedInstanceIndex;
            }
        }
    }

    internal unsafe struct SmallIntegerArray : IEquatable<SmallIntegerArray>
    {
        public const int k_Capacity = 8;
        private fixed int m_FixedArray[k_Capacity];
        public readonly int Length;

        public SmallIntegerArray(int length)
        {
            Assert.IsTrue(length < k_Capacity);
            Length = length;
        }

        public int this[int index]
        {
            get
            {
                Assert.IsTrue(index < Length);
                return m_FixedArray[index];
            }
            set
            {
                Assert.IsTrue(index < Length);
                m_FixedArray[index] = value;
            }
        }

        public bool Equals(SmallIntegerArray other)
        {
            fixed (int* pBuffer = m_FixedArray)
            {
                return Length == other.Length && UnsafeUtility.MemCmp(pBuffer, other.m_FixedArray, k_Capacity * sizeof(int)) == 0;
            }
        }

        public override bool Equals(object obj)
        {
            return obj is SmallIntegerArray other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(m_FixedArray[0], m_FixedArray[1], m_FixedArray[2], m_FixedArray[3], m_FixedArray[4], m_FixedArray[5], m_FixedArray[6], m_FixedArray[7]);
        }
    }

    internal interface IDataArrays
    {
        void Initialize(int initCapacity);
        void Dispose();
        void Grow(int newCapacity);
        void Remove(int index, int lastIndex);
        void SetDefault(int index);
    }

    internal struct EditorInstanceDataArrays : IDataArrays
    {
#if UNITY_EDITOR
        public NativeArray<ulong> sceneCullingMasks;
        public ParallelBitArray selectedBits;

        public void Initialize(int initCapacity)
        {
            sceneCullingMasks = new NativeArray<ulong>(initCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            sceneCullingMasks.FillArray(ulong.MaxValue);
            selectedBits = new ParallelBitArray(initCapacity, Allocator.Persistent);
        }

        public void Dispose()
        {
            sceneCullingMasks.Dispose();
            selectedBits.Dispose();
        }

        public void Grow(int newCapacity)
        {
            sceneCullingMasks.ResizeArray(newCapacity);
            selectedBits.Resize(newCapacity);
        }

        public void Remove(int index, int lastIndex)
        {
            sceneCullingMasks[index] = sceneCullingMasks[lastIndex];
            selectedBits.Set(index, selectedBits.Get(lastIndex));
        }

        public void SetDefault(int index)
        {
            sceneCullingMasks[index] = ulong.MaxValue;
            selectedBits.Set(index, false);
        }

        internal readonly struct ReadOnly
        {
            public readonly NativeArray<ulong>.ReadOnly sceneCullingMasks;
            public readonly ParallelBitArray selectedBits;

            public ReadOnly(in CPUInstanceData instanceData)
            {
                sceneCullingMasks = instanceData.editorData.sceneCullingMasks.GetSubArray(0, instanceData.instancesLength).AsReadOnly();
                selectedBits = instanceData.editorData.selectedBits.GetSubArray(instanceData.instancesLength);
            }
        }
#else
        public void Initialize(int initCapacity) { }
        public void Dispose() { }
        public void Grow(int newCapacity) { }
        public void Remove(int index, int lastIndex) { }
        public void SetDefault(int index) { }
        internal readonly struct ReadOnly { public ReadOnly(in CPUInstanceData instanceData) { } }
#endif
    }

    [Flags]
    internal enum TransformUpdateFlags : byte
    {
        None = 0,
        HasLightProbeCombined = 1 << 0,
        IsPartOfStaticBatch = 1 << 1
    }

    internal struct InstanceFlags
    {
        private byte data;

        public bool staticShadowCaster => (this.data & 0x1) > 0;
        public ShadowCastingMode shadowCastingMode => (ShadowCastingMode) ((int) (this.data >> 1) & 0x3);
        public bool smallMeshCulling => (this.data & 0x7) > 0U;
        public bool affectsLightmaps => false;

        public InstanceFlags(bool staticShadowCaster, ShadowCastingMode shadowCastingMode, bool smallMeshCulling)
        {
            this.data = (byte)(staticShadowCaster ? 0x1 : 0);
            this.data |= (byte)((uint)shadowCastingMode << 1);
            this.data |= (byte)(smallMeshCulling ? 0x7 : 0U);
        }
        
        public InstanceFlags(GPUDrivenPackedRendererData packedRendererData) : this(packedRendererData.staticShadowCaster, packedRendererData.shadowCastingMode, packedRendererData.smallMeshCulling)
        {
            
        }
    }

    internal struct CPUSharedInstanceFlags
    {
        // public TransformUpdateFlags transformUpdateFlags;
        public InstanceFlags instanceFlags;
    }

    internal struct PackedMatrix
    {
        /*  mat4x3 packed like this:
                p1.x, p1.w, p2.z, p3.y,
                p1.y, p2.x, p2.w, p3.z,
                p1.z, p2.y, p3.x, p3.w,
                0.0,  0.0,  0.0,  1.0
        */

        public float4 packed0;
        public float4 packed1;
        public float4 packed2;

        public static PackedMatrix FromMatrix4x4(in Matrix4x4 m)
        {
            return new PackedMatrix
            {
                packed0 = new float4(m.m00, m.m10, m.m20, m.m01),
                packed1 = new float4(m.m11, m.m21, m.m02, m.m12),
                packed2 = new float4(m.m22, m.m03, m.m13, m.m23)
            };
        }

        public static PackedMatrix FromFloat4x4(in float4x4 m)
        {
            return new PackedMatrix
            {
                packed0 = new float4(m.c0.x, m.c0.y, m.c0.z, m.c1.x),
                packed1 = new float4(m.c1.y, m.c1.z, m.c2.x, m.c2.y),
                packed2 = new float4(m.c2.z, m.c3.x, m.c3.y, m.c3.z)
            };
        }
    }
}

//                                                                  +-------------+
//                                                                  |  Instance   |
//                                                                  |  Handle 2   |
//                                                                  +------^------+
//                                                       +-------------+   |      +-------------+
//                                                       |  Instance   |   |      |  Instance   |
//                                                       |  Handle 0   |   |      |  Handle 3   |
//                                                       +------^------+   |      +---^---------+
//                                                              |          |         /
//                                                              |          |        /
//                          +-----------------------------------------------------------------------------------------------+
//                          |                                   |          |      /                                         |
//                          |                                 +-v-- ----+--v - +---v---+----+----+----+                     |
//                          |                 InstanceIndices | 0  |free| 1  | 2  |free|free|free|free|...                  |
//                          |                                 +--^-+----+--^-+--^-+----+----+----+----+                     |
//                          |                                    |        /    /                                            |
//                          |                                    |       /    / +-------------------------                  |
//                          |                                    |      /    /  |    +-----------------------               |
//                          |                                    |     /    /   |    |                                      |
//                          |                                 +--v-+--v-+--v-+--v-+--v-+----+                               |
//                          |                       Instances |  0 |  2 |  3 |    |    |... |                               |
//                          |                                 +----+----+----+----+----+----+                               |
//         CPUInstanceData  |            LocalToWorldMatrices |    |    |    |    |    |... |                               |
//                          |                                 +----+----+----+----+----+----+                               |
//                          |                   WorldBoundses |    |    |    |    |    |... |                               |
//                          |                                 +----+----+----+----+----+----+                               |
//                          |           SharedInstanceHandles |    |    |    |    |    |... |                               |
//                          |                                 +----+----+----+----+----+----+                               |
//                          |         MovedInCurrentFrameBits |    |    |    |    |    | ...|                               |
//                          |                                 +----+----+----+----+----+----+                               |
//                          |                 SharedInstances |  1 | 1  | 1  | 2  | 3  | ...|                               |
//                          |                                 +-\--+--|-+--/-+--/-+--/-+----+                               |
//                          |                                    |    |   /    /    /                                       |
//                          +-----------------------------------------------------------------------------------------------+
//                                                                \   / |    |    |
//                                                                 \ |  /    /    /
//                          +-----------------------------------------------------------------------------------------------+
//                          |                                       \|/    /    /                                           |
//                          |                                 +----+-v--+-v--+-v--+----+----+----+----+                     |
//                          |           SharedInstanceIndices |free| 0  | 1  | 2  |free|free|free|free|...                  |
//                          |                                 +----+-|--+--|-+--|-+----+----+----+----+                     |
//                          |                                       /     /    /                                            |
//                          |                                      /     /    /                                             |
//                          |                                     /     /    /                                              |
//                          |                                     |     |    |                                              |
//                          |                                    /     /    /                                               |
//                          |                                 +-v--+--v-+--v-+----+----+----+                               |
//   CPUSharedInstanceData  |                         MeshIDs |    |    |    |... |... |... |                               |
//                          |                                 +----+----+----+----+----+----+                               |
//                          |                   LocalBoundses |    |    |    |... |... | ...|                               |
//                          |                                 +----+----+----+----+----+----+                               |
//                          |                RendererGroupIDs |    |    |    | ...| ...| ...|                               |
//                          |                                 +----+----+----+----+----+----+                               |
//                          |                 GameObjectLayer |    |    |    |... |... | ...|                               |
//                          |                                 +----+----+----+----+----+----+                               |
//                          |                           Flags |    |    |    |... |... | ...|                               |
//                          |                                 +----+----+----+----+----+----+                               |
//                          |                       RefCounts | 3  | 1  |  1 |... |... | ...|                               |
//                          |                                 +----+----+----+----+----+----+                               |
//                          +-----------------------------------------------------------------------------------------------+
