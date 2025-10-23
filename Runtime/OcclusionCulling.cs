using System.Collections;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace BrgRenderSystem
{
    [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct OcclusionCullingProcessor
    {
        [NativeDisableContainerSafetyRestriction]
        private NativeArray<float> _OccluderDepthPyramid;
        
        private FixedList128Bytes<int4> _OccluderMipBounds;
        private float4x4 viewProjMatrix; // [6];
        private float3 viewOriginWorldSpace;
        private float3 facingDirWorldSpace; // [6];
        private float3 radialDirWorldSpace;
        
        float4 _DepthSizeInOccluderPixels;
        float4 _OccluderDepthPyramidSize;
        
        private bool isReversedZ;
        public bool valid => _OccluderDepthPyramid.IsCreated;
        
        public static OcclusionCullingProcessor Create(int cameraID)
        {
#if _URP_ANYG_EXTENSIONS
            if (!HZBOcclusionData.OcclusionData.TryGetValue(cameraID, out var hzbOcclusion))
                return default;
            
            HZBOcclusionFrameData frameData = hzbOcclusion.GetLatestFrameData();
            if (frameData == null)
                return default;
            
            OcclusionCullingProcessor result = new OcclusionCullingProcessor()
            {
                _OccluderDepthPyramid = frameData.hzBuffer,
                viewProjMatrix = frameData.viewProjMatrix,
                viewOriginWorldSpace = frameData.viewOriginWorldSpace,
                facingDirWorldSpace = frameData.facingDirWorldSpace,
                radialDirWorldSpace = frameData.radialDirWorldSpace,
                _DepthSizeInOccluderPixels = new float4(hzbOcclusion.topMipSize.x, hzbOcclusion.topMipSize.y, 1.0f / hzbOcclusion.topMipSize.x, 1.0f / hzbOcclusion.topMipSize.y),
                _OccluderDepthPyramidSize = new float4(hzbOcclusion.totalSize.x, hzbOcclusion.totalSize.y, 1.0f / hzbOcclusion.totalSize.x, 1.0f / hzbOcclusion.totalSize.y),
            };
            
            result._OccluderMipBounds.Length = hzbOcclusion.depthMips;
            for (int i = 0; i < hzbOcclusion.depthMips; ++i)
            {
                result._OccluderMipBounds[i] = hzbOcclusion.occluderMipBounds[i];
            }
            
            result.isReversedZ = SystemInfo.usesReversedZBuffer;
            return result;
#else
            return default;
#endif
        }
        
        float FarthestDepth(float depthA, float depthB)
        {
            return isReversedZ ? math.min(depthA, depthB) : math.max(depthA, depthB);
        }
        
        float FarthestDepth(float4 depths)
        {
            return isReversedZ ? math.min(depths.x, math.min(depths.y, math.min(depths.z, depths.w))) : math.max(depths.x, math.max(depths.y, math.max(depths.z, depths.w)));
        }
        
        bool IsVisibleAfterOcclusion(float occluderDepth, float queryClosestDepth)
        {
            return isReversedZ ? queryClosestDepth > occluderDepth : queryClosestDepth < occluderDepth;
        }
        
        struct BoundingObjectData
        {
            public float3 frontCenterPosRWS;
            public float2 centerPosNDC;
            public float2 radialPosNDC;
        };
        
        BoundingObjectData CalculateBoundingObjectData(float3 center, float radius)
        {
            float3 centerPosRWS = center - viewOriginWorldSpace.xyz;

            float3 radialVec = radius * radialDirWorldSpace.xyz;
            float3 facingVec = radius * facingDirWorldSpace.xyz;

            BoundingObjectData data;
            data.centerPosNDC = ComputeNormalizedDeviceCoordinatesWithZ(centerPosRWS, viewProjMatrix).xy;
            data.radialPosNDC = ComputeNormalizedDeviceCoordinatesWithZ(centerPosRWS + radialVec, viewProjMatrix).xy;
            data.frontCenterPosRWS = centerPosRWS + facingVec;
            return data;
        }
        
        BoundingObjectData CalculateBoundingObjectData(in AABB aabb)
        {
            float3 centerWS = aabb.center;
            float3 halfSize = aabb.extents; // hx, hy, hz
            float3 centerPosRWS = centerWS - viewOriginWorldSpace;

            float3 radialVec = math.dot(halfSize, math.abs(radialDirWorldSpace)) * radialDirWorldSpace;
            float3 facingVec = math.dot(halfSize, math.abs(facingDirWorldSpace)) * facingDirWorldSpace;

            BoundingObjectData data;
            data.centerPosNDC      = ComputeNormalizedDeviceCoordinatesWithZ(centerPosRWS, viewProjMatrix).xy;
            data.radialPosNDC      = ComputeNormalizedDeviceCoordinatesWithZ(centerPosRWS + radialVec, viewProjMatrix).xy;
            data.frontCenterPosRWS = centerPosRWS + facingVec;
            return data;
        }
        
        public bool IsOcclusionVisible(in AABB aabb)
        {
            return IsOcclusionVisible(CalculateBoundingObjectData(aabb));
        }
        
        bool IsOcclusionVisible(float3 center, float radius)
        {
            return IsOcclusionVisible(CalculateBoundingObjectData(center,radius));
        }
        
        bool IsOcclusionVisible(BoundingObjectData data)
        {
            return IsOcclusionVisible(data.frontCenterPosRWS, data.centerPosNDC, data.radialPosNDC);
        }
        
        bool IsOcclusionVisible(float3 frontCenterPosRWS, float2 centerPosNDC, float2 radialPosNDC)
        {
            if (math.dot(frontCenterPosRWS, facingDirWorldSpace.xyz) >= 0)
                return true;
            
            float2 centerCoordInTopMip = centerPosNDC * _DepthSizeInOccluderPixels.xy;
            float radiusInPixels = math.length((radialPosNDC - centerPosNDC) * _DepthSizeInOccluderPixels.xy);
            int mipLevel = math.clamp(Mathf.CeilToInt(math.log2(radiusInPixels)), 0, _OccluderMipBounds.Length - 1);
            float2 centerCoordInChosenMip = centerCoordInTopMip * math.exp2(-mipLevel);
            float4 gatherDepths = GatherTexture2D(centerCoordInChosenMip, _OccluderMipBounds[mipLevel]);
            float occluderDepth = FarthestDepth(gatherDepths);
            float queryClosestDepth = ComputeNormalizedDeviceCoordinatesWithZ(frontCenterPosRWS, viewProjMatrix).z;
            return IsVisibleAfterOcclusion(occluderDepth, queryClosestDepth);
        }
        
        float4 GatherTexture2D(float2 centerCoordInChosenMip, int4 mipBounds)
        {
            int2 coordLeftBottom = (int2)math.floor(new float2(mipBounds.xy) +
                                                    math.clamp(centerCoordInChosenMip, .5f,
                                                        new float2(mipBounds.zw) - .5f));
            int2 mipMax = mipBounds.xy + mipBounds.zw;
            return new float4(
                LoadDepthFromTexture(math.min(coordLeftBottom + new int2(0, 0), mipMax)),
                LoadDepthFromTexture(math.min(coordLeftBottom + new int2(0, 1), mipMax)),
                LoadDepthFromTexture(math.min(coordLeftBottom + new int2(1, 1), mipMax)),
                LoadDepthFromTexture(math.min(coordLeftBottom + new int2(1, 0), mipMax)));
        }
        
        float LoadDepthFromTexture(int2 coord)
        {
            return _OccluderDepthPyramid[CoordToIndex(coord)];
        }
        
        int CoordToIndex(int2 coord)
        {
            return (int)_OccluderDepthPyramidSize.x * coord.y + coord.x;
        }
        
        // The returned Z value is the depth buffer value (and NOT linear view space Z value).
        // Use case examples:
        // (position = positionCS) => (clipSpaceTransform = use default)
        // (position = positionVS) => (clipSpaceTransform = UNITY_MATRIX_P)
        // (position = positionWS) => (clipSpaceTransform = UNITY_MATRIX_VP)
        float3 ComputeNormalizedDeviceCoordinatesWithZ(float3 position, float4x4 clipSpaceTransform)
        {
            float4 positionCS = math.mul(clipSpaceTransform, new float4(position, 1.0f));

            // Our world space, view space, screen space and NDC space are Y-up.
            // Our clip space is flipped upside-down due to poor legacy Unity design.
            // The flip is baked into the projection matrix, so we only have to flip
            // manually when going from CS to NDC and back.
            positionCS.y = isReversedZ ? -positionCS.y : positionCS.y;

            positionCS *= math.rcp(positionCS.w);
            positionCS.xy = positionCS.xy * 0.5f + 0.5f;

            return positionCS.xyz;
        }
    }
}
