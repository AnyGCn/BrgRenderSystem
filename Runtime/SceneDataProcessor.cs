using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Assertions;

namespace BrgRenderSystem
{
    public unsafe struct LODGroupItem
    {
        public const int k_MaxLODLevelsCount = 8;
        public int lodGroupID;
        public short renderersCount;
        public bool lastLODIsBillboard;
        public LODFadeMode fadeMode;
        public Vector3 worldSpaceReferencePoint;
        public float worldSpaceSize;
        public int lodCount;
        internal fixed short lodRenderersCount[k_MaxLODLevelsCount];
        internal fixed float lodScreenRelativeTransitionHeight[k_MaxLODLevelsCount];
        internal fixed float lodFadeTransitionWidth[k_MaxLODLevelsCount];

        public bool isValid => lodGroupID != -1;
        
        public LODGroupItem(int lodCount)
        {
            lodGroupID = -1;
            renderersCount = 0;
            lastLODIsBillboard = false;
            fadeMode = LODFadeMode.None;
            worldSpaceReferencePoint = Vector3.zero;
            worldSpaceSize = 0;
            this.lodCount = lodCount;
        }
        
        public void SetLodRenderersCount(int lodIndex, int count)
        {
            Assert.IsTrue(lodIndex < k_MaxLODLevelsCount);
            lodRenderersCount[lodIndex] = (short)count;
        }
        
        public void SetScreenRelativeTransitionHeight(int lodIndex, float screenRelativeTransitionHeight)
        {
            Assert.IsTrue(lodIndex < k_MaxLODLevelsCount);
            lodScreenRelativeTransitionHeight[lodIndex] = screenRelativeTransitionHeight;
        }
        
        public void SetFadeTransitionWidth(int lodIndex, float fadeTransitionWidth)
        {
            Assert.IsTrue(lodIndex < k_MaxLODLevelsCount);
            lodFadeTransitionWidth[lodIndex] = fadeTransitionWidth;
        }
    }
    
    public unsafe struct RendererGroupItem
    {
        public const int k_MaxMaterialsCount = 8;

        // shared instance data
        public Mesh mesh;
        public Bounds localBounds;
        public GPUDrivenPackedRendererData packedRendererData;
        public short gameObjectLayer;
        public uint renderingLayerMask;
        public int materialsCount;
        internal fixed int materials[k_MaxMaterialsCount];

        // 
        public int rendererGroupID;
        public int lodGroupID;
        public Matrix4x4 localToWorldMatrix;

        // support lightmap?
        // public int lightmapIndex;
        // public Vector4 lightmapScaleOffset;

        // submesh properties (for indirect draw? we do not support indirect draw now.)
        // public short subMeshStartIndex;        // for static batch render?
        // public readonly List<SubMeshDescriptor> subMeshDesc;
        
        // we only support implicit Instance Indices Mode Now.
        // public Matrix4x4 prevLocalToWorldMatrix;   // support motion vector?
        // public NativeArray<int> instancesOffset;
        // public NativeArray<int> instancesCount;
        // public NativeArray<int> rendererGroupIndex;

        // unsupported for editor scene culling mask
        // public NativeArray<GPUDrivenRendererEditorData> editorData;
        
        // unity6 property
        // int rendererPriority;
        // public NativeArray<int> materialFilterFlags;
        
        public bool isValid => rendererGroupID != -1;
        public byte lodMask { get => packedRendererData.lodMask; set => packedRendererData.lodMask = value; }

        public RendererGroupItem(int materialsCount)
        {
            mesh = null;
            rendererGroupID = -1;
            packedRendererData = new GPUDrivenPackedRendererData();
            lodGroupID = -1;
            gameObjectLayer = 0;
            localBounds = new Bounds();
            // lightmapIndex = -1;
            // lightmapScaleOffset = Vector4.zero;
            renderingLayerMask = 0;
            localToWorldMatrix = Matrix4x4.identity;
            // prevLocalToWorldMatrix = Matrix4x4.identity;
            this.materialsCount = materialsCount;
        }

        public void SetMaterial(int index, Material material)
        {
            Assert.IsTrue(index < k_MaxMaterialsCount);
            materials[index] = material.GetInstanceID();
        }
    }
    
    // Do not add and remove in one frame.
    internal class SceneDataProcessor : IDisposable
    {
        // private InstanceAllocator lodGroupInstanceAllocator;
        // private InstanceAllocator rendererInstanceAllocator;
        //
        // private ParallelBitArray lodGroupItemsMask;
        // private ParallelBitArray rendererGroupItemsMask;

        public LODGroupInputData lodGroupInputData;
        public RendererGroupInputData rendererInputData;

        Dictionary<Mesh, int> meshToIndexMap = new Dictionary<Mesh, int>();
        Dictionary<int, int> materialToIndexMap = new Dictionary<int, int>();
        
        public SceneDataProcessor()
        {
            // lodGroupInstanceAllocator.Initialize();
            // rendererInstanceAllocator.Initialize();
            //
            // lodGroupItemsMask = new ParallelBitArray(1024, Allocator.Persistent);
            // rendererGroupItemsMask = new ParallelBitArray(1024, Allocator.Persistent);
            
            lodGroupInputData.Initialize(1024);
            rendererInputData.Initialize(1024);
        }

        // TODO: implement remove function?
        // return materialIndex
        public int RegisterMaterial(int materialID)
        {
            if (materialToIndexMap.TryGetValue(materialID, out var index))
                return index;

            index = materialToIndexMap.Count;
            materialToIndexMap.Add(materialID, index);
            rendererInputData.materialID.Add(materialID);
            rendererInputData.packedMaterialData.Add(new GPUDrivenPackedMaterialData());
            return index;
        }
        
        // TODO: implement remove function?
        // return meshIndex
        public int RegisterMesh(Mesh mesh)
        {
            if (meshToIndexMap.TryGetValue(mesh, out var index))
                return index;

            index = meshToIndexMap.Count;
            meshToIndexMap.Add(mesh, index);
            rendererInputData.meshID.Add(mesh.GetInstanceID());
            int subMeshCount = mesh.subMeshCount;
            rendererInputData.subMeshCount.Add((short)subMeshCount);
            // rendererInputData.subMeshDescOffset.Add(rendererInputData.subMeshDesc.Length);
            // for (int i = 0; i < subMeshCount; ++i)
            //     rendererInputData.subMeshDesc.Add(new SubMeshDescriptor());
            
            return index;
        }
        
        public unsafe void RegisterLodGroup(ref LODGroupItem lodGroup)
        {
            // Assert.IsTrue(lodGroupItemsMask.Length > lodGroupID && lodGroupItemsMask.Get(lodGroupID));
            lodGroupInputData.lodGroupID.Add(lodGroup.lodGroupID);
            lodGroupInputData.fadeMode.Add(lodGroup.fadeMode);
            lodGroupInputData.worldSpaceReferencePoint.Add(lodGroup.worldSpaceReferencePoint);
            lodGroupInputData.worldSpaceSize.Add(lodGroup.worldSpaceSize);
            lodGroupInputData.lastLODIsBillboard.Add(lodGroup.lastLODIsBillboard);
            int lodCount = lodGroup.lodCount;
            int lodOffset = lodGroupInputData.lodRenderersCount.Length;
            lodGroupInputData.lodCount.Add(lodCount);
            lodGroupInputData.lodOffset.Add(lodOffset);
            lodGroupInputData.renderersCount.Add(lodGroup.renderersCount);
            for (int i = 0; i < lodCount; ++i)
            {
                lodGroupInputData.lodRenderersCount.Add(lodGroup.lodRenderersCount[i]);
                lodGroupInputData.lodScreenRelativeTransitionHeight.Add(lodGroup.lodScreenRelativeTransitionHeight[i]);
                lodGroupInputData.lodFadeTransitionWidth.Add(lodGroup.lodFadeTransitionWidth[i]);
            }
        }
        
        public unsafe void RegisterRendererGroup(ref RendererGroupItem renderer)
        {
            // Assert.IsTrue(rendererGroupItemsMask.Length > rendererGroupID && rendererGroupItemsMask.Get(rendererGroupID));
            // Assert.IsTrue(renderer.lodGroupID < 0 || lodGroupItemsMask.Get(renderer.lodGroupID));
            rendererInputData.rendererGroupID.Add(renderer.rendererGroupID);
            rendererInputData.gameObjectLayer.Add(renderer.gameObjectLayer);
            rendererInputData.localBounds.Add(renderer.localBounds);
            // rendererInputData.lightmapIndex.Add(renderer.lightmapIndex);
            // rendererInputData.lightmapScaleOffset.Add(renderer.lightmapScaleOffset);
            rendererInputData.lodGroupID.Add(renderer.lodGroupID);
            rendererInputData.renderingLayerMask.Add(renderer.renderingLayerMask);
            rendererInputData.localToWorldMatrix.Add(renderer.localToWorldMatrix);
            // rendererInputData.prevLocalToWorldMatrix.Add(renderer.prevLocalToWorldMatrix);
            rendererInputData.meshIndex.Add(RegisterMesh(renderer.mesh));
            rendererInputData.packedRendererData.Add(renderer.packedRendererData);
            // rendererInputData.subMeshStartIndex.Add(0);
            // rendererInputData.rendererPriority.Add(0);
            int materialsCount = renderer.materialsCount;
            rendererInputData.materialsCount.Add((short)materialsCount);
            rendererInputData.materialsOffset.Add(rendererInputData.materialIndex.Length);
            for (int i = 0; i < materialsCount; ++i)
                rendererInputData.materialIndex.Add(RegisterMaterial(renderer.materials[i]));
        }
        
        public void UnregisterLodGroup(int lodGroupID)
        {
            // Assert.IsTrue(lodGroupItemsMask.Length > lodGroupID && lodGroupItemsMask.Get(lodGroupID));
            lodGroupInputData.invalidLODGroupID.Add(lodGroupID);
            // lodGroupInstanceAllocator.FreeInstance(lodGroupID);
            // lodGroupItemsMask.Set(lodGroupID, false);
        }
        
        public void UnregisterRendererGroup(int rendererGroupID)
        {
            // Assert.IsTrue(rendererGroupItemsMask.Length > rendererGroupID && rendererGroupItemsMask.Get(rendererGroupID));
            rendererInputData.invalidRendererGroupID.Add(rendererGroupID);
            // rendererInstanceAllocator.FreeInstance(rendererGroupID);
            // rendererGroupItemsMask.Set(rendererGroupID, false);
        }

        public void FrameBegin()
        {
            rendererInputData.meshID.Resize(meshToIndexMap.Count, NativeArrayOptions.UninitializedMemory);
            foreach (var kv in meshToIndexMap)
                rendererInputData.meshID[kv.Value] = kv.Key.GetInstanceID();
            
            rendererInputData.materialID.Resize(materialToIndexMap.Count, NativeArrayOptions.UninitializedMemory);
            foreach (var kv in materialToIndexMap)
                rendererInputData.materialID[kv.Value] = kv.Key;
        }
        
        public void FrameEnd()
        {
            meshToIndexMap.Clear();
            materialToIndexMap.Clear();
            lodGroupInputData.Clear();
            rendererInputData.Clear();
        }
        
        public void Dispose()
        {
            // lodGroupInstanceAllocator.Dispose();
            // rendererInstanceAllocator.Dispose();
            // lodGroupItemsMask.Dispose();
            // rendererGroupItemsMask.Dispose();
            lodGroupInputData.Dispose();
            rendererInputData.Dispose();
        }
    }
}
