#ifndef WATER_LIGHTING_INCLUDED
#define WATER_LIGHTING_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

#ifdef MAIN_LIGHT_CALCULATE_SHADOWS
#define SHADOW_ITERATIONS 4
#endif // MAIN_LIGHT_CALCULATE_SHADOWS

#ifdef _SSR_SAMPLES_HIGH
    #define SSR_ITERATIONS 32
#elif _SSR_SAMPLES_MEDIUM
    #define SSR_ITERATIONS 16
#else
    #define SSR_ITERATIONS 8
#endif

#ifdef _VOXEL
    #undef _REFLECTION_PLANARREFLECTION
#endif

half CalculateFresnelTerm(half3 normalWS, half3 viewDirectionWS, float distance)
{
    const half fresnel = F_Schlick(0.02, dot(normalWS, viewDirectionWS));
    return fresnel * (1 - saturate((half)distance * 0.005) * 0.5);
}

///////////////////////////////////////////////////////////////////////////////
//                         Lighting Calculations                             //
///////////////////////////////////////////////////////////////////////////////

//diffuse
half4 VertexLightingAndFog(half3 normalWS, half3 posWS, half3 clipPos)
{
    half3 vertexLight = VertexLighting(posWS, normalWS);
    half fogFactor = ComputeFogFactor(clipPos.z);
    return half4(fogFactor, vertexLight);
}

//specular
half3 Highlights(half3 positionWS, half roughness, half3 normalWS, half3 viewDirectionWS)
{
    Light mainLight = GetMainLight();

    half roughness2 = roughness * roughness;
    half3 halfDir = SafeNormalize(mainLight.direction + viewDirectionWS);
    half NoH = saturate(dot(normalize(normalWS), halfDir));
    half LoH = saturate(dot(mainLight.direction, halfDir));
    // GGX Distribution multiplied by combined approximation of Visibility and Fresnel
    // See "Optimizing PBR for Mobile" from Siggraph 2015 moving mobile graphics course
    // https://community.arm.com/events/1155
    half d = NoH * NoH * (roughness2 - 1.h) + 1.0001h;
    half LoH2 = LoH * LoH;
    half specularTerm = roughness2 / ((d * d) * max(0.1h, LoH2) * (roughness + 0.5h) * 4);
    // on mobiles (where half actually means something) denominator have risk of overflow
    // clamp below was added specifically to "fix" that, but dx compiler (we convert bytecode to metal/gles)
    // sees that specularTerm have only non-negative terms, so it skips max(0,..) in clamp (leaving only min(100,...))
#if defined (SHADER_API_MOBILE)
    specularTerm = specularTerm - HALF_MIN;
    specularTerm = clamp(specularTerm, 0.0, 5.0); // Prevent FP16 overflow on mobiles
#endif
    return specularTerm * mainLight.color * mainLight.distanceAttenuation;
}

//Soft Shadows
half SoftShadows(float2 screenUV, float3 positionWS, half3 viewDir, half depth)
{
#ifdef MAIN_LIGHT_CALCULATE_SHADOWS
    half2 jitterUV = screenUV * _ScreenParams.xy * _DitherPattern_TexelSize.xy;
    half shadowBase = SAMPLE_TEXTURE2D_SHADOW(_MainLightShadowmapTexture, sampler_MainLightShadowmapTexture, TransformWorldToShadowCoord(positionWS));
	half shadowAttenuation = 1 - (1-shadowBase) * 0.5;

	float loopDiv = rcp(SHADOW_ITERATIONS);
	half depthFrac = depth * loopDiv;
	half3 lightOffset = -viewDir;// * depthFrac;
	for (uint i = 0u; i < SHADOW_ITERATIONS; ++i)
    {
#ifndef _STATIC_SHADER
        jitterUV += frac(half2(_Time.x, -_Time.z));
#endif
        float3 jitterTexture = SAMPLE_TEXTURE2D(_DitherPattern, sampler_DitherPattern, jitterUV + i * _ScreenParams.xy).xyz * 2 - 1;
	    half3 j = jitterTexture.xzy * i * 0.1;
	    float3 lightJitter = (positionWS + j) + (lightOffset * (i + jitterTexture.y));
	    half shadow = SAMPLE_TEXTURE2D_SHADOW(_MainLightShadowmapTexture, sampler_MainLightShadowmapTexture, TransformWorldToShadowCoord(lightJitter));
	    shadowAttenuation *= 1 - ((1-shadow) * 0.5 * (i * loopDiv));
	}
    shadowAttenuation = BEYOND_SHADOW_FAR(TransformWorldToShadowCoord(positionWS)) ? 1.0 : shadowAttenuation;

    //half fade = GetShadowFade(positionWS);
    half fade = GetMainLightShadowFade(positionWS);

    return lerp(shadowAttenuation, length(SampleMainLightCookie(positionWS)), fade);
#else
    return 1;
#endif
}

///////////////////////////////////////////////////////////////////////////////
//                           Reflection Modes                                //
///////////////////////////////////////////////////////////////////////////////

float GetDepth(float2 uv)
{
    float rawDepth = SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, sampler_ScreenTextures_point_clamp, uv, 0);

    #if UNITY_REVERSED_Z
    return rawDepth;
    #else
    // Adjust z to match NDC for OpenGL
    return lerp(UNITY_NEAR_CLIP_VALUE, 1, rawDepth);
    #endif
}

float3 ViewPosFromDepth(float2 positionNDC, float deviceDepth)
{
    float4 positionCS  = ComputeClipSpacePosition(positionNDC, deviceDepth);
    float4 hpositionVS = mul(UNITY_MATRIX_I_P, positionCS);
    return hpositionVS.xyz / hpositionVS.w;
}

float2 ViewSpacePosToUV(float3 pos)
{
    return ComputeNormalizedDeviceCoordinates(pos, UNITY_MATRIX_P);
}

void RayMarch(float3 origin, float3 direction, out half2 sampleUV, out half valid)
{
    direction *= SSR_STEP_SIZE;
    
    [loop]
    for (int i = 0; i < SSR_ITERATIONS; i++)
    {
        origin += direction;
        direction *= 2;
        sampleUV = ViewSpacePosToUV(origin);
            
        if (sampleUV.x > 1 || sampleUV.x < 0 || sampleUV.y > 1 || sampleUV.y < 0)
            break;

        float deviceDepth = GetDepth(sampleUV);
        if (!deviceDepth)
            continue;

        float3 samplePos = ViewPosFromDepth(sampleUV, deviceDepth);

        if (distance(samplePos.z, origin.z) > length(direction) * SSR_THICKNESS) continue;
        
        if (samplePos.z > origin.z)
        {
            valid = 1;
            return;
        }
    }
}

half3 CubemapReflection(float3 viewDirectionWS, float3 positionWS, float3 normalWS)
{
    float3 reflectVector = reflect(-viewDirectionWS, normalWS);
    return GlossyEnvironmentReflection(reflectVector, 0, 1); // TODO Sample cubemap instead
}

half3 SampleReflections(float3 normalWS, float3 positionWS, float3 viewDirectionWS, half2 screenUV)
{
    half3 reflection = 0;
    /*
    half2 refOffset = 0;
    */

#if _REFLECTION_CUBEMAP
    half3 reflectVector = reflect(-viewDirectionWS, normalWS);
    reflection = SAMPLE_TEXTURECUBE_LOD(_CubemapTexture, sampler_CubemapTexture, reflectVector, 0).rgb;
#elif _REFLECTION_PROBES
    reflection = CubemapReflection(viewDirectionWS, positionWS, normalWS);
#elif _REFLECTION_PLANARREFLECTION
    half2 reflectionUV = screenUV + half2(normalWS.zx) * half2(0.05, 0.2);
    half4 reflectionRGBA = SAMPLE_TEXTURE2D(_PlanarReflectionTexture, sampler_ScreenTextures_linear_clamp, reflectionUV);//planar reflection

    half3 backup = CubemapReflection(viewDirectionWS, positionWS, normalWS);
    reflection = lerp(backup, reflectionRGBA.rgb, reflectionRGBA.a);
#elif _REFLECTION_SSR
    half2 uv;
    half valid = 0;

    float3 positionVS = TransformWorldToView(positionWS);
    float3 normalVS = TransformWorldToViewDir(normalWS);
    
    float3 pivot = reflect(positionVS, normalVS);
    RayMarch(positionVS, pivot, uv, valid);
    half3 ssr = SAMPLE_TEXTURE2D(_CameraOpaqueTexture, sampler_ScreenTextures_linear_clamp, uv).rgb;

    half3 backup = CubemapReflection(viewDirectionWS, positionWS, normalWS);
    reflection = lerp(backup, ssr, valid);
#endif
    //do backup
    return reflection;
}

#endif // WATER_LIGHTING_INCLUDED
