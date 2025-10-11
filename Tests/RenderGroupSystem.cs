using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Assertions;

namespace BrgRenderSystem.Tests
{
    public class RenderGroupSystem : MonoBehaviour
    {
        static RenderGroupSystem s_Instance = null;
        
        public static bool isValid => s_Instance;
        
        /// <summary>
        /// Instance of the required component type.
        /// </summary>
        public static RenderGroupSystem instance
        {
            get
            {
                if (!s_Instance)
                {
                    GameObject go = new GameObject("Default " + nameof(RenderGroupSystem), typeof(RenderGroupSystem));
                }

                return s_Instance;
            }
        }
        
        private ResidentDrawer residentDrawer;

        private Dictionary<LODGroup, int> lodGroupIDMap = new Dictionary<LODGroup, int>();

        private Dictionary<MeshRenderer, int> meshRendererIDMap = new Dictionary<MeshRenderer, int>();

        // Start is called before the first frame update
        void Awake()
        {
            if (s_Instance != null)
            {
                GameObject.Destroy(this);
                return;
            }
            
            s_Instance = this;
            ResidentDrawer.ReinitializeIfNeeded();
            residentDrawer = ResidentDrawer.instance;
        }

        public void RegisterLODGroup(LODGroup lodGroup, ref LODGroupItem item)
        {
            if (lodGroupIDMap.ContainsKey(lodGroup))
                return;

            int lodGroupId = lodGroupIDMap.Count;
            item.lodGroupID = lodGroupId;
            lodGroupIDMap.Add(lodGroup, lodGroupId);
            residentDrawer.RegisterLodGroup(ref item);
        }

        public void RegisterMeshRenderer(MeshRenderer meshRenderer, ref RendererGroupItem item)
        {
            if (meshRendererIDMap.ContainsKey(meshRenderer))
                return;

            int rendererGroupId = meshRendererIDMap.Count;
            item.rendererGroupID = rendererGroupId;
            meshRendererIDMap.Add(meshRenderer, rendererGroupId);
            residentDrawer.RegisterRendererGroup(ref item);
        }

        public void UnregisterLODGroup(LODGroup lodGroup)
        {
            Assert.IsNotNull(lodGroup);
            if (lodGroupIDMap.ContainsKey(lodGroup))
            {
                int lodGroupId = lodGroupIDMap[lodGroup];
                lodGroupIDMap.Remove(lodGroup);
                residentDrawer.UnregisterLodGroup(lodGroupId);
            }
        }

        public void UnregisterMeshRenderer(MeshRenderer meshRenderer)
        {
            Assert.IsNotNull(meshRenderer);
            if (meshRendererIDMap.ContainsKey(meshRenderer))
            {
                int rendererGroupId = meshRendererIDMap[meshRenderer];
                meshRendererIDMap.Remove(meshRenderer);
                residentDrawer.UnregisterRendererGroup(rendererGroupId);
            }
        }

        private void OnDestroy()
        {
            if (s_Instance == this)
            {
                ResidentDrawer.CleanUp();
                s_Instance = null;
            }
        }
    }
}
