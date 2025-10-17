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
        
        private FixedList128Bytes<uint4> _OccluderMipBounds;
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
                result._OccluderMipBounds[i] = (uint4)hzbOcclusion.occluderMipBounds[i];
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
        
        public bool IsOcclusionVisible(float3 center, float radius)
        {
            return IsOcclusionVisible(CalculateBoundingObjectData(center,radius));
        }
        
        bool IsOcclusionVisible(BoundingObjectData data)
        {
            return IsOcclusionVisible(data.frontCenterPosRWS, data.centerPosNDC, data.radialPosNDC);
        }
        
        bool IsOcclusionVisible(float3 frontCenterPosRWS, float2 centerPosNDC, float2 radialPosNDC)
        {
            bool isVisible = true;
            float queryClosestDepth = ComputeNormalizedDeviceCoordinatesWithZ(frontCenterPosRWS, viewProjMatrix).z;
            bool isBehindCamera = math.dot(frontCenterPosRWS, facingDirWorldSpace.xyz) >= 0;

            float2 centerCoordInTopMip = centerPosNDC * _DepthSizeInOccluderPixels.xy;
            float radiusInPixels = math.length((radialPosNDC - centerPosNDC) * _DepthSizeInOccluderPixels.xy);

            // log2 of the radius in pixels for the gather4 mip level
            frexp(radiusInPixels, out var mipLevel);
            mipLevel = math.max(mipLevel + 1, 0);
            if (mipLevel < _OccluderMipBounds.Length && !isBehindCamera)
            {
                // scale our coordinate to this mip
                float2 centerCoordInChosenMip = centerCoordInTopMip * math.exp2(-mipLevel);
                int4 mipBounds = (int4)_OccluderMipBounds[mipLevel];

                // if ((_OcclusionTestDebugFlags & OCCLUSIONTESTDEBUGFLAG_ALWAYS_PASS) == 0)
                {
                    // gather4 occluder depths to cover this radius
                    // float2 gatherUv = (new float2(mipBounds.xy) + math.clamp(centerCoordInChosenMip, .5f, new float2(mipBounds.zw) - .5f)) * _OccluderDepthPyramidSize.zw;
                    float4 gatherDepths = GatherTexture2D(new float2(mipBounds.xy) + math.clamp(centerCoordInChosenMip, .5f, new float2(mipBounds.zw) - .5f));
                    float occluderDepth = FarthestDepth(gatherDepths);
                    isVisible = IsVisibleAfterOcclusion(occluderDepth, queryClosestDepth);
                }
            }

            return isVisible;
        }
        
        float4 GatherTexture2D(float2 coord)
        {
            int2 coordLeftBottom = (int2)math.floor(coord);
            return new float4(
                LoadDepthFromTexture(coordLeftBottom + new int2(0, 0)),
                LoadDepthFromTexture(coordLeftBottom + new int2(1, 0)),
                LoadDepthFromTexture(coordLeftBottom + new int2(1, 1)),
                LoadDepthFromTexture(coordLeftBottom + new int2(0, 1)));
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
        
        static float frexp(float number, out int exponent)
        {
            int bits = System.BitConverter.SingleToInt32Bits(number);
            int exp = (int)((bits & FLT_EXP_MASK) >> FLT_MANT_BITS);
            exponent = 0;

            if (exp == 0xff || number == 0F)
                number += number;
            else
            {
                // Not zero and finite.
                exponent = exp - 126;
                if (exp == 0)
                {
                    // Subnormal, scale number so that it is in [1, 2).
                    number *= System.BitConverter.Int32BitsToSingle(0x4c000000); // 2^25
                    bits = System.BitConverter.SingleToInt32Bits(number);
                    exp = (int)((bits & FLT_EXP_MASK) >> FLT_MANT_BITS);
                    exponent = exp - 126 - 25;
                }
                // Set exponent to -1 so that number is in [0.5, 1).
                number = System.BitConverter.Int32BitsToSingle((bits & FLT_EXP_CLR_MASK) | 0x3f000000);
            }

            return number;
        }

        #region "Properties of floating-point types."

        /// <summary>
        /// The exponent bias of a <see cref="double"/>, i.e. value to subtract from the stored exponent to get the real exponent (<c>1023</c>).
        /// </summary>
        public const int DBL_EXP_BIAS = 1023;

        /// <summary>
        /// The number of bits in the exponent of a <see cref="double"/> (<c>11</c>).
        /// </summary>
        public const int DBL_EXP_BITS = 11;

        /// <summary>
        /// The maximum (unbiased) exponent of a <see cref="double"/> (<c>1023</c>).
        /// </summary>
        public const int DBL_EXP_MAX = 1023;

        /// <summary>
        /// The minimum (unbiased) exponent of a <see cref="double"/> (<c>-1022</c>).
        /// </summary>
        public const int DBL_EXP_MIN = -1022;

        /// <summary>
        /// Bit-mask used for clearing the exponent bits of a <see cref="double"/> (<c>0x800fffffffffffff</c>).
        /// </summary>
        public const long DBL_EXP_CLR_MASK = DBL_SGN_MASK | DBL_MANT_MASK;

        /// <summary>
        /// Bit-mask used for extracting the exponent bits of a <see cref="double"/> (<c>0x7ff0000000000000</c>).
        /// </summary>
        public const long DBL_EXP_MASK = 0x7ff0000000000000L;

        /// <summary>
        /// The number of bits in the mantissa of a <see cref="double"/>, excludes the implicit leading <c>1</c> bit (<c>52</c>).
        /// </summary>
        public const int DBL_MANT_BITS = 52;

        /// <summary>
        /// Bit-mask used for clearing the mantissa bits of a <see cref="double"/> (<c>0xfff0000000000000</c>).
        /// </summary>
        public const long DBL_MANT_CLR_MASK = DBL_SGN_MASK | DBL_EXP_MASK;

        /// <summary>
        /// Bit-mask used for extracting the mantissa bits of a <see cref="double"/> (<c>0x000fffffffffffff</c>).
        /// </summary>
        public const long DBL_MANT_MASK = 0x000fffffffffffffL;

        /// <summary>
        /// Maximum positive, normal value of a <see cref="double"/> (<c>1.7976931348623157E+308</c>).
        /// </summary>
        public const double DBL_MAX = System.Double.MaxValue;

        /// <summary>
        /// Minimum positive, normal value of a <see cref="double"/> (<c>2.2250738585072014e-308</c>).
        /// </summary>
        public const double DBL_MIN = 2.2250738585072014e-308D;

        /// <summary>
        /// Maximum positive, subnormal value of a <see cref="double"/> (<c>2.2250738585072009e-308</c>).
        /// </summary>
        public const double DBL_DENORM_MAX = DBL_MIN - DBL_DENORM_MIN;

        /// <summary>
        /// Minimum positive, subnormal value of a <see cref="double"/> (<c>4.94065645841247E-324</c>).
        /// </summary>
        public const double DBL_DENORM_MIN = System.Double.Epsilon;

        /// <summary>
        /// Bit-mask used for clearing the sign bit of a <see cref="double"/> (<c>0x7fffffffffffffff</c>).
        /// </summary>
        public const long DBL_SGN_CLR_MASK = 0x7fffffffffffffffL;

        /// <summary>
        /// Bit-mask used for extracting the sign bit of a <see cref="double"/> (<c>0x8000000000000000</c>).
        /// </summary>
        public const long DBL_SGN_MASK = -1 - 0x7fffffffffffffffL;

        /// <summary>
        /// The exponent bias of a <see cref="float"/>, i.e. value to subtract from the stored exponent to get the real exponent (<c>127</c>).
        /// </summary>
        public const int FLT_EXP_BIAS = 127;

        /// <summary>
        /// The number of bits in the exponent of a <see cref="float"/> (<c>8</c>).
        /// </summary>
        public const int FLT_EXP_BITS = 8;

        /// <summary>
        /// The maximum (unbiased) exponent of a <see cref="float"/> (<c>127</c>).
        /// </summary>
        public const int FLT_EXP_MAX = 127;

        /// <summary>
        /// The minimum (unbiased) exponent of a <see cref="float"/> (<c>-126</c>).
        /// </summary>
        public const int FLT_EXP_MIN = -126;

        /// <summary>
        /// Bit-mask used for clearing the exponent bits of a <see cref="float"/> (<c>0x807fffff</c>).
        /// </summary>
        public const int FLT_EXP_CLR_MASK = FLT_SGN_MASK | FLT_MANT_MASK;

        /// <summary>
        /// Bit-mask used for extracting the exponent bits of a <see cref="float"/> (<c>0x7f800000</c>).
        /// </summary>
        public const int FLT_EXP_MASK = 0x7f800000;

        /// <summary>
        /// The number of bits in the mantissa of a <see cref="float"/>, excludes the implicit leading <c>1</c> bit (<c>23</c>).
        /// </summary>
        public const int FLT_MANT_BITS = 23;

        /// <summary>
        /// Bit-mask used for clearing the mantissa bits of a <see cref="float"/> (<c>0xff800000</c>).
        /// </summary>
        public const int FLT_MANT_CLR_MASK = FLT_SGN_MASK | FLT_EXP_MASK;

        /// <summary>
        /// Bit-mask used for extracting the mantissa bits of a <see cref="float"/> (<c>0x007fffff</c>).
        /// </summary>
        public const int FLT_MANT_MASK = 0x007fffff;

        /// <summary>
        /// Maximum positive, normal value of a <see cref="float"/> (<c>3.40282347e+38</c>).
        /// </summary>
        public const float FLT_MAX = System.Single.MaxValue;

        /// <summary>
        /// Minimum positive, normal value of a <see cref="float"/> (<c>1.17549435e-38</c>).
        /// </summary>
        public const float FLT_MIN = 1.17549435e-38F;

        /// <summary>
        /// Maximum positive, subnormal value of a <see cref="float"/> (<c>1.17549421e-38</c>).
        /// </summary>
        public const float FLT_DENORM_MAX = FLT_MIN - FLT_DENORM_MIN;

        /// <summary>
        /// Minimum positive, subnormal value of a <see cref="float"/> (<c>1.401298E-45</c>).
        /// </summary>
        public const float FLT_DENORM_MIN = System.Single.Epsilon;

        /// <summary>
        /// Bit-mask used for clearing the sign bit of a <see cref="float"/> (<c>0x7fffffff</c>).
        /// </summary>
        public const int FLT_SGN_CLR_MASK = 0x7fffffff;

        /// <summary>
        /// Bit-mask used for extracting the sign bit of a <see cref="float"/> (<c>0x80000000</c>).
        /// </summary>
        public const int FLT_SGN_MASK = -1 - 0x7fffffff;

        #endregion
    }
}
