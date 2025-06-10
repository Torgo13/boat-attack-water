#ifndef WATER_COMMON_INCLUDED
#define WATER_COMMON_INCLUDED

#define SHADOWS_SCREEN 0

#include "WaterInput.hlsl"
#include "CommonUtilities.hlsl"
#include "GerstnerWaves.hlsl"
#include "WaterLighting.hlsl"

#if defined(_STATIC_SHADER)
    #define WATER_TIME 0.0
#else
    #define WATER_TIME _Time.y
#endif

#define DEPTH_MULTIPLIER 1 / _MaxDepth

#define WaterBufferA(uv) SAMPLE_TEXTURE2D(_WaterBufferA, sampler_ScreenTextures_linear_clamp, uv)
#define WaterBufferAVert(uv) SAMPLE_TEXTURE2D_LOD(_WaterBufferA, sampler_ScreenTextures_linear_clamp, uv, 0)
#define WaterBufferB(uv) SAMPLE_TEXTURE2D(_WaterBufferB, sampler_ScreenTextures_linear_clamp, uv)
#define WaterBufferBVert(uv) SAMPLE_TEXTURE2D_LOD(_WaterBufferB, sampler_ScreenTextures_linear_clamp, uv, 0)

///////////////////////////////////////////////////////////////////////////////
//          	   	       Water debug functions                             //
///////////////////////////////////////////////////////////////////////////////

half3 DebugWaterFX(half3 input, half4 waterFX, half screenUV)
{
    input = lerp(input, half3(waterFX.y, 1, waterFX.z), saturate(floor(screenUV + 0.7)));
    input = lerp(input, waterFX.xxx, saturate(floor(screenUV + 0.5)));
    half3 disp = lerp(0, half3(1, 0, 0), saturate((waterFX.www - 0.5) * 4));
    disp += lerp(0, half3(0, 0, 1), saturate(((1 - waterFX.www) - 0.5) * 4));
    input = lerp(input, disp, saturate(floor(screenUV + 0.3)));
    return input;
}

///////////////////////////////////////////////////////////////////////////////
//          	   	      Water shading functions                            //
///////////////////////////////////////////////////////////////////////////////

half3 Scattering(half depth)
{
    const half grad = saturate(exp2(-depth * DEPTH_MULTIPLIER));
    return _ScatteringColor * (1 - grad);
}

half3 Absorption(half depth)
{
    return saturate(exp(-depth * DEPTH_MULTIPLIER * 10 * (1 - _AbsorptionColor)));
}

float2 AdjustedDepth(float2 uvs, float4 additionalData)
{
    const float rawD = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, sampler_ScreenTextures_point_clamp, uvs);
    const float d = LinearEyeDepth(rawD, _ZBufferParams);
    float x = d * additionalData.x - additionalData.y;

    if (d > _ProjectionParams.z)// TODO might be cheaper alternative
    {
        x = 1024;
    }

    float y = rawD * -_ProjectionParams.x;
    return float2(x, y);
}

float AdjustWaterTextureDepth(float input)
{
    return  max(0, (1 - input) * 20 - 4);
}

float WaterTextureDepthVert(float2 uv)
{
    return AdjustWaterTextureDepth(WaterBufferBVert(uv).b);// * (_MaxDepth + _VeraslWater_DepthCamParams.x) - _VeraslWater_DepthCamParams.x;
}

float WaterTextureDepth(float2 uv)
{
    return AdjustWaterTextureDepth(WaterBufferB(uv).b);//(1 - SAMPLE_TEXTURE2D_LOD(_WaterDepthMap, sampler_WaterDepthMap_linear_clamp, positionWS.xz * 0.002 + 0.5, 1).r) * (_MaxDepth + _VeraslWater_DepthCamParams.x) - _VeraslWater_DepthCamParams.x;
}

float3 WaterDepth(float3 positionWS, float4 additionalData, float2 screenUVs)// x = seafloor depth, y = water depth
{
    float3 out_depth;
    out_depth.xz = AdjustedDepth(screenUVs, additionalData);
    const float wd = WaterTextureDepth(screenUVs);
    out_depth.y = wd + positionWS.y;
    return out_depth;
}

half3 Refraction(half2 distortion, half depth, half edgeFade)
{
    half3 output = SAMPLE_TEXTURE2D_LOD(_CameraOpaqueTexture, sampler_ScreenTextures_linear_clamp, distortion, depth * 0.25).rgb;
    output *= max(Absorption(depth), 1 - edgeFade);
    return output;
}

half2 DistortionUVs(half depth, float3 normalWS, float3 viewDirectionWS)
{
    half3 viewNormal = mul((float3x3)GetWorldToHClipMatrix(), -normalWS).xyz;

    //float4x4 viewMat = GetWorldToViewMatrix();
    //half3 f = viewMat[1].xyz;

    //half d = dot(f, half3(0, 1, 0));

    //half y = normalize(viewNormal.y) + f.y;

    //half2 distortion = half2(viewNormal.x, y);
    //half2 distortion = half2(viewNormal.x, viewNormal.y - d);

    return viewNormal.xz * clamp(0, 0.1, saturate(depth * 0.05));
}

float4 AdditionalData(float3 positionWS, WaveStruct wave)
{
    float4 data = float4(0.0, 0.0, 0.0, 0.0);
    float3 viewPos = TransformWorldToView(positionWS);
    data.x = length(viewPos / viewPos.z);// distance to surface
    data.y = length(GetCameraPositionWS().xyz - positionWS); // local position in camera space(view direction WS)
    data.z = wave.position.y / _MaxWaveHeight * 0.5 + 0.5; // encode the normalized wave height into additional data
    data.w = wave.foam;// wave.position.x + wave.position.z;
    return data;
}

/**
 * \brief
 * \param dist raw distance from the center of cascades
 * \return returns the cascade a/b level(x, y) and the blend(z)
 */
half3 GetDetailCascades(float3 dist)
{
    float raw = distance(dist, _WorldSpaceCameraPos) * 10;
    raw = pow(raw * raw, 0.1);

    half2 uvScales = half2(ceil(raw + 0.5), ceil(raw)) * 0.3;

    half blend = 1 - abs(1 - 2 * frac(raw));

    //return x * x *( 3.0 - 2.0 * x );
    blend = blend * blend * (3.0 - 2.0 * blend);

    return half3(uvScales * uvScales * uvScales * uvScales, blend);
}

float4 DetailUVs(float3 positionWS, half noise)
{
    half3 dist = GetDetailCascades(positionWS);

    float2 scale = float2(0.16, 0.08);
    noise *= 0.05;

    /*
    float2 s, c;
    sincos(dist.x, s, c);
    float2x2 rotA = float2x2(c.x, -s.x, s.x, c.x);
    */

    float4 output = positionWS.xzxz * scale.xxyy;
    output.xy += WATER_TIME * 0.2h + noise; // small detail
    output.zw += WATER_TIME * 0.1h + noise; // medium detail
    output.xy /= dist.x;
    output.zw /= dist.y;
    //output.xy = mul(output.xy, rotA);
    return output;
}

void DetailNormals(inout float3 normalWS, float4 uvs, half4 waterFX, float depth, float fade)
{
    float2 detailBump1 = SAMPLE_TEXTURE2D(_SurfaceMap, sampler_SurfaceMap, uvs.zw).xy * 2 - 1;
    float2 detailBump2 = SAMPLE_TEXTURE2D(_SurfaceMap, sampler_SurfaceMap, uvs.xy).xy * 2 - 1;
    float2 detailBump = (detailBump1 * fade + detailBump2 * (1 - fade)) * saturate(depth * 0.25 + 0.25);

    float3 normal1 = float3(detailBump.x, 0, detailBump.y) * _BoatAttack_Water_MicroWaveIntensity;
    float3 normal2 = float3(1 - waterFX.y, 0.5h, 1 - waterFX.z) - 0.5;
    normalWS = normalize(normalWS + normal1 + normal2);
}

#if BLEND_LODS
void BlendLods(inout float3 positionWS, float3 positionOS, float2 uv)
{
	float blend = Remap(LodBlend(positionWS), float4(0.2, 0.8, 0, 1));
	float2 minMax = float2(1, saturate(blend) + 1);
	// Mesh LOD
	positionOS *= Remap(float3(uv.x, 0, uv.y), float4(0, 1, minMax));
	positionWS = TransformObjectToWorld(positionOS);
}
#endif // BLEND_LODS

Varyings WaveVertexOperations(Varyings input)
{
    input.normalWS = float3(0, 1, 0);
    input.fogFactorNoise.y = ((noise((input.positionWS.xz * 0.5) + WATER_TIME) + noise((input.positionWS.xz * 1) + WATER_TIME)) * 0.25 - 0.5) + 1;

    // Detail UVs
    input.uv = DetailUVs(input.positionWS, input.fogFactorNoise.y);

    half4 screenUV = GetVertexPositionInputs(TransformWorldToObject(input.positionWS)).positionNDC;
    screenUV.xyz /= screenUV.w;

    // shallows mask
    float waterDepth = 1 - WaterBufferBVert(screenUV.xy).b;// WaterTextureDepthVert(screenUV);
    //input.positionWS.y += pow(saturate((-waterDepth + 1.5) * 0.4), 2);

    //Gerstner here
    half depthWaveRamp = SAMPLE_TEXTURE2D_LOD(_BoatAttack_RampTexture, sampler_BoatAttack_Linear_Clamp_RampTexture, waterDepth, 0).b;
    half opacity = depthWaveRamp;// saturate(waterDepth * 0.1 + 0.05);

    WaveStruct wave;
    SampleWaves(input.positionWS, opacity, wave);
    input.normalWS = wave.normal;
#if _VOXEL
#else
    input.positionWS += wave.position;
#endif // _VOXEL

#ifdef SHADER_API_PS4
    input.positionWS.y -= 0.5;
#endif

    // Dynamic displacement
#if _VOXEL
    half4 waterFX = WaterBufferAVert(screenUV.xy);
    input.positionWS.y += waterFX.w * 2 - 1;
#endif // _VOXEL

    // After waves
    input.positionCS = TransformWorldToHClip(input.positionWS);
    input.screenPosition = GetVertexPositionInputs(TransformWorldToObject(input.positionWS)).positionNDC;
    input.viewDirectionWS.xyz = SafeNormalize(_WorldSpaceCameraPos - input.positionWS);

    // Fog
    input.fogFactorNoise.x = ComputeFogFactor(input.positionCS.z);
    input.preWaveSP = screenUV.xyz; // pre-displaced screenUVs

    // Additional data
    input.additionalData = AdditionalData(input.positionWS, wave);

    // distance blend
    half distanceBlend = saturate(abs(length((_WorldSpaceCameraPos.xz - input.positionWS.xz) * 0.005)) - 0.25);
    input.normalWS = lerp(input.normalWS, float3(0, 1, 0), distanceBlend);

    return input;
}

void InitializeInputData(Varyings input, out WaterInputData inputData, float2 screenUV)
{
    // bluenoise
    float2 uv = screenUV * _DitherPattern_TexelSize.xy * _ScreenParams.xy;
    inputData.screenNoise = SAMPLE_TEXTURE2D(_DitherPattern, sampler_DitherPattern, uv).xyz;

    float3 depth = WaterDepth(input.positionWS, input.additionalData, screenUV);// TODO - hardcoded shore depth UVs
    // Sample water FX texture
    inputData.waterBufferA = WaterBufferA(input.preWaveSP.xy);
    inputData.waterBufferB = WaterBufferB(input.preWaveSP.xy);
    inputData.waterBufferB.b = AdjustWaterTextureDepth(inputData.waterBufferB.b);

    inputData.positionWS = input.positionWS;

    inputData.normalWS = input.normalWS;
    // Detail waves
    float fade = GetDetailCascades(inputData.positionWS).z;
    DetailNormals(inputData.normalWS, input.uv, inputData.waterBufferA, depth.x, fade);

    inputData.viewDirectionWS = input.viewDirectionWS.xyz;
    
    half2 distortion = DistortionUVs(depth.x, inputData.normalWS, input.viewDirectionWS.xyz);
    distortion = screenUV.xy + distortion;//* clamp(depth.x, 0, 5);
    float d = depth.x;
    depth.xz = AdjustedDepth(distortion, input.additionalData); // only x y
    distortion = depth.x < 0 ? screenUV.xy : distortion;
    inputData.refractionUV = distortion;
    depth.x = depth.x < 0 ? d : depth.x;

    inputData.detailUV = input.uv;

#ifdef MAIN_LIGHT_CALCULATE_SHADOWS
    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.normalWS);
#endif // MAIN_LIGHT_CALCULATE_SHADOWS

    inputData.fogCoord = input.fogFactorNoise.x;
    inputData.depth = depth.x;
    inputData.reflectionUV = 0;
    inputData.GI = 0;
}

void InitializeSurfaceData(inout WaterInputData input, out WaterSurfaceData surfaceData, float4 additionalData)
{
    surfaceData.absorption = 0;
    surfaceData.scattering = 0;

    // Foam
    half depthEdge = saturate(input.depth * 0.5);
    half foamShoreRamp = SAMPLE_TEXTURE2D(_BoatAttack_RampTexture, sampler_BoatAttack_Linear_Clamp_RampTexture, 1 - depthEdge).r;
    half foamWaveRamp = SAMPLE_TEXTURE2D(_BoatAttack_RampTexture, sampler_BoatAttack_Linear_Clamp_RampTexture, additionalData.w).g;

    half foamBlendMask = max(foamWaveRamp, foamShoreRamp) + input.waterBufferA.r;// + edgeFoam + input.waterBufferA.r;// max(max(waveFoam, edgeFoam), input.waterFX.r * 2);
    foamBlendMask += (-1 + _BoatAttack_water_FoamIntensity) * 0.5;

    half4 mask = half4(0, 0, 0, 0);
    mask.r = saturate(foamBlendMask * 3 - 2);
    mask.g = saturate(foamBlendMask * 3 - 1) - mask.r;
    mask.b = saturate(foamBlendMask * 3) - mask.g - mask.r;
    mask.a = 1 - mask.r - mask.g - mask.b;

    mask = saturate(mask);

    half4 foamA = half4(SAMPLE_TEXTURE2D(_FoamMap, sampler_FoamMap, input.detailUV.xy * 0.5).rgb, 0); //r=thick, g=medium, b=light
    half4 foamB = half4(SAMPLE_TEXTURE2D(_FoamMap, sampler_FoamMap, input.detailUV.zw * 0.5).rgb, 0); //r=thick, g=medium, b=light
    half4 foam = lerp(foamA, foamB, GetDetailCascades(input.positionWS).z);
    surfaceData.foamMask = length(foam * mask);

    surfaceData.foam = 1;//saturate(length(foamMap * foamBlend) * 1.5 - 0.1);
}

half3 WaterShading(WaterInputData input, WaterSurfaceData surfaceData, float4 additionalData, float2 screenUV)
{
    // extra inputs
    half edgeFade = saturate(input.depth * 5);

    // Fresnel
    half fresnelTerm = CalculateFresnelTerm(input.normalWS, input.viewDirectionWS, distance(input.positionWS, GetCameraPositionWS()));

    // Lighting
    Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS), input.positionWS, 1);
#ifdef MAIN_LIGHT_CALCULATE_SHADOWS
    half volumeShadow = SoftShadows(screenUV, input.positionWS, input.viewDirectionWS, input.depth);
#endif // MAIN_LIGHT_CALCULATE_SHADOWS
    half3 GI = SampleSH(input.normalWS);

    // SSS
    half3 directLighting = dot(mainLight.direction, half3(0, 1, 0)) * mainLight.color;
    half temp = dot(input.viewDirectionWS.xyz, -mainLight.direction) * additionalData.z;
    temp *= temp * temp;
    directLighting += saturate(temp) * mainLight.color * 5;
    //half3 directLighting = F_Schlick(0.2, dot(mainLight.direction, input.normalWS)) * mainLight.color;
    //directLighting += saturate(pow(dot(input.viewDirectionWS.xyz, -mainLight.direction) * additionalData.z, 3)) * Absorption(1) * mainLight.color * 2;

	// Specular
	//half NdotL = saturate(dot(input.normalWS, mainLight.direction));
	//half3 radiance = mainLight.color * (mainLight.distanceAttenuation * NdotL) * mainLight.shadowAttenuation;

    BRDFData brdfData;
    half alpha = 1;
    //half smoothness = 0.9; // 1-saturate(fresnelTerm);
    InitializeBRDFData(half3(0, 0, 0), 0, half3(1, 1, 1), 0.98, alpha, brdfData);
    half3 spec = DirectBRDFSpecular(brdfData, input.normalWS, mainLight.direction, input.viewDirectionWS) * brdfData.specular;
    spec *= mainLight.color * mainLight.shadowAttenuation; //* length(input.normalWS.xz);
    //spec *= radiance; // * length(input.normalWS.xz);
    spec *= 1 - saturate(surfaceData.foamMask * 4);

    // Foam
    surfaceData.foam *= (GI + directLighting * mainLight.shadowAttenuation) * 3 * saturate(surfaceData.foamMask);

    // SSS
    half3 sss = directLighting /* * volumeShadow */ + GI;
    sss *= Scattering(input.depth);

    // Reflections
    half3 reflection = SampleReflections(input.normalWS, input.positionWS, input.viewDirectionWS, screenUV);
    reflection *= edgeFade;

    // Refraction
    half3 refraction = Refraction(input.refractionUV, input.depth, edgeFade);

    // Do compositing
    half3 compA = lerp(refraction, reflection, fresnelTerm) + spec + sss;
    half3 compB = compA * saturate(1 - surfaceData.foamMask) + surfaceData.foam;
    // final
    half3 output = MixFog(compB, input.fogCoord);

    /*
    half2 a = frac(input.detailUV.xy);// * input.detailUV.z;
    half2 b = frac(input.detailUV.zw);// * 1-input.detailUV.z;
    */

    // Debug block
#if defined(BOAT_ATTACK_WATER_DEBUG_DISPLAY)
    [branch] switch (_BoatAttack_Water_DebugPass)
    {
    case 0: // none
        return output;
    case 1: // normalWS
        return saturate(half3(input.normalWS.x, 0, input.normalWS.z) * 10);
    case 2: // Reflection
        return reflection;
    case 3: // Refraction
        return refraction;
    case 4: // Specular
        return spec;
    case 5: // SSS
        return sss;
    case 6: // Foam
        return surfaceData.foam.xxx * surfaceData.foamMask;
    case 7: // Foam Mask
        return surfaceData.foamMask.xxx;
    case 8: // buffer A
        return WaterBufferA(screenUV);
    case 9: // buffer B
        return WaterBufferB(screenUV);
    case 10: // eye depth
        float d = input.depth;
        return half3(frac(d), frac(d * 0.1), 0);
    case 11: // water depth texture
        float wd = WaterTextureDepth(screenUV);
        return half3(frac(wd), frac(wd * 0.1), 0);
    case 12: // fresnel
        return fresnelTerm.xxx;
    }
#endif

    //return final
    return output;
}

half WaterNearFade(Varyings input, WaterInputData inputData)
{
    float3 camPos = GetCameraPositionWS();
    camPos.y = 0;
    half fade = saturate(inputData.depth * 20);
    half distanceFade = 1 - saturate((distance(input.positionWS, camPos) - _BoatAttack_Water_DistanceBlend) * 0.05);
    return min(fade, distanceFade);
}

///////////////////////////////////////////////////////////////////////////////
//               	   Vertex and Fragment functions                         //
///////////////////////////////////////////////////////////////////////////////

// Vertex: Used for Standard non-tessellated water
Varyings WaterVertex(Attributes v)
{
    Varyings o = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_TRANSFER_INSTANCE_ID(v, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

    o.uv.xy = v.texcoord; // geo uvs
    o.positionWS = TransformObjectToWorld(v.positionOS.xyz);

#if BLEND_LODS
	BlendLods(o.positionWS, v.positionOS.xyz, v.texcoord);
#endif // BLEND_LODS

    o = WaveVertexOperations(o);
    return o;
}

// Fragment for water
half4 WaterFragment(Varyings IN) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(IN);
    float2 screenUV = IN.screenPosition.xy / IN.screenPosition.w; // screen UVs

    WaterInputData inputData;
    InitializeInputData(IN, inputData, screenUV);

    WaterSurfaceData surfaceData;
    InitializeSurfaceData(inputData, surfaceData, IN.additionalData);

    half4 output;
    output.a = WaterNearFade(IN, inputData);
    output.rgb = WaterShading(inputData, surfaceData, IN.additionalData, screenUV);

    return output;
}

#endif // WATER_COMMON_INCLUDED
