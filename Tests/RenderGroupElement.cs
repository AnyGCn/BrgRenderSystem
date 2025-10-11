using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrgRenderSystem.Tests
{
    /// <summary>
    /// Minimum render element, attached to a LODGroup or a MeshRenderer
    /// </summary>
    public class RenderGroupElement : MonoBehaviour
    {
        class RendererGroupItemWrapper
        {
            public RendererGroupItem value;

            public RendererGroupItemWrapper(RendererGroupItem item)
            {
                value = item;
            }
        }

        [SerializeField, ContextMenuItem("Switch State", "SwitchState")]
        private bool useBRG = true;

        private LODGroup lodGroup;
        private LODGroupItem lodItem = new LODGroupItem();

        private Dictionary<MeshRenderer, RendererGroupItemWrapper> rendererItems =
            new Dictionary<MeshRenderer, RendererGroupItemWrapper>();

        public bool UseBRG
        {
            get => useBRG;
            set
            {
                useBRG = value;
                SwitchState();
            }
        }

        public void Awake()
        {
            if (TryGetComponent<LODGroup>(out lodGroup))
            {
                GetBRGItemInfo(lodGroup, out lodItem, ref rendererItems);
            }
            else if (TryGetComponent<MeshRenderer>(out var meshRenderer))
            {
                if (GetBRGItemInfo(meshRenderer, out var rendererItem))
                {
                    rendererItems.Add(meshRenderer, new RendererGroupItemWrapper(rendererItem));
                }
            }

            if (rendererItems.Count == 0)
                UnityEngine.Object.DestroyImmediate(this);
        }

        void SwitchState()
        {
            RenderGroupSystem instance = RenderGroupSystem.instance;
            if (useBRG)
            {
                int lodGroupId = 0;
                if (lodGroup)
                {
                    instance.RegisterLODGroup(lodGroup, ref lodItem);
                    lodGroupId = lodItem.lodGroupID;
                }

                foreach (var rendererItem in rendererItems)
                {
                    rendererItem.Key.enabled = false;
                    rendererItem.Value.value.lodGroupID = lodGroupId;
                    instance.RegisterMeshRenderer(rendererItem.Key, ref rendererItem.Value.value);
                }
            }
            else
            {
                if (lodGroup)
                {
                    instance.UnregisterLODGroup(lodGroup);
                    lodItem.lodGroupID = -1;
                }

                foreach (var rendererItem in rendererItems)
                {
                    rendererItem.Key.enabled = true;
                    instance.UnregisterMeshRenderer(rendererItem.Key);
                    rendererItem.Value.value.rendererGroupID = -1;
                    rendererItem.Value.value.lodGroupID = -1;
                }
            }
        }

        void OnEnable()
        {
            SwitchState();
        }

        void OnDisable()
        {
            // Avoid render group system destroy before this object destroyed.
            if (!RenderGroupSystem.isValid)
                return;
            
            RenderGroupSystem instance = RenderGroupSystem.instance;
            if (lodGroup)
            {
                instance.UnregisterLODGroup(lodGroup);
                lodItem.lodGroupID = -1;
            }

            foreach (var rendererItem in rendererItems)
            {
                rendererItem.Key.enabled = false;
                instance.UnregisterMeshRenderer(rendererItem.Key);
                rendererItem.Value.value.rendererGroupID = -1;
                rendererItem.Value.value.lodGroupID = -1;
            }
        }

        static void GetBRGItemInfo(LODGroup lodGroup, out LODGroupItem lodItem,
            ref Dictionary<MeshRenderer, RendererGroupItemWrapper> rendererItems)
        {
            lodItem = new LODGroupItem(lodGroup.lodCount)
            {
                lodGroupID = -1,
                lastLODIsBillboard = false,
                fadeMode = lodGroup.fadeMode,
                worldSpaceSize = lodGroup.size,
                worldSpaceReferencePoint = lodGroup.transform.position
            };

            int renderersCount = 0;
            var ds = lodGroup.GetLODs();
            for (var index = 0; index < ds.Length; index++)
            {
                var lod = ds[index];
                int lodRenderersCount = 0;
                foreach (var render in lod.renderers)
                {
                    if (render is not MeshRenderer meshRenderer)
                        continue;

                    if (!rendererItems.TryGetValue(meshRenderer, out var rendererItemWrapper))
                    {
                        if (!GetBRGItemInfo(meshRenderer, out var rendererItem))
                            continue;

                        rendererItemWrapper = new RendererGroupItemWrapper(rendererItem);
                        rendererItems[meshRenderer] = rendererItemWrapper;
                    }

                    rendererItemWrapper.value.lodMask |= (byte)(1 << index);
                    lodRenderersCount++;
                }

                lodItem.SetLodRenderersCount(index, lodRenderersCount);
                lodItem.SetFadeTransitionWidth(index, lod.fadeTransitionWidth);
                lodItem.SetScreenRelativeTransitionHeight(index, lod.screenRelativeTransitionHeight);
                renderersCount += lodRenderersCount;
            }

            lodItem.renderersCount = (short)renderersCount;
        }

        static bool GetBRGItemInfo(MeshRenderer meshRenderer, out RendererGroupItem item)
        {
            item = default;
            if (!meshRenderer.TryGetComponent<MeshFilter>(out var meshFilter) || meshFilter.sharedMesh == null)
                return false;

            Mesh mesh = meshFilter.sharedMesh;
            Material[] materials = meshRenderer.sharedMaterials;
            if (mesh.subMeshCount != materials.Length)
                return false;

            item = new RendererGroupItem(materials.Length)
            {
                lodGroupID = -1,
                localBounds = meshRenderer.localBounds,
                mesh = mesh,
                gameObjectLayer = (short)meshRenderer.gameObject.layer,
                renderingLayerMask = meshRenderer.renderingLayerMask,
                // lightmapIndex = meshRenderer.lightmapIndex,
                // lightmapScaleOffset = meshRenderer.lightmapScaleOffset,
                packedRendererData = new GPUDrivenPackedRendererData(meshRenderer.receiveShadows,
                    meshRenderer.staticShadowCaster, 0, meshRenderer.shadowCastingMode, LightProbeUsage.Off,
                    MotionVectorGenerationMode.ForceNoMotion, false, false, false, false, false),
                localToWorldMatrix = meshRenderer.localToWorldMatrix,
                // item.prevLocalToWorldMatrix = meshRenderer.localToWorldMatrix,
            };

            for (var index = 0; index < materials.Length; index++)
                item.SetMaterial(index, materials[index]);

            return true;
        }
    }
}
