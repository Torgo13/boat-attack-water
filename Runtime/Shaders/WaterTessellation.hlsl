﻿#ifndef WATER_TESSELLATION_INCLUDED
#define WATER_TESSELLATION_INCLUDED

///////////////////////////////////////////////////////////////////////////////
//                  				Structs		                             //
///////////////////////////////////////////////////////////////////////////////

struct TessellationControlPoint
{
	float4 positionOS               : INTERNALTESSPOS;
	float4 texcoord 				: TEXCOORD0;	// Geometric UVs stored in xy, and world(pre-waves) in zw
	float3 positionWS				: TEXCOORD1;	// world position of the vertices
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct HS_ConstantOutput
{
	float TessFactor[3]    : SV_TessFactor;
	float InsideTessFactor : SV_InsideTessFactor;
};

///////////////////////////////////////////////////////////////////////////////
//                         Tessellation functions                            //
///////////////////////////////////////////////////////////////////////////////

CBUFFER_START(UnityPerMaterial)
half _TessellationEdgeLength;
CBUFFER_END

float TessellationEdgeFactor (float3 p0, float3 p1)
{
    float edgeLength = distance(p0, p1);

    float3 edgeCenter = (p0 + p1) * 0.5;
    float viewDistance = distance(edgeCenter, _WorldSpaceCameraPos);

    return edgeLength * _ScreenParams.y / (_TessellationEdgeLength * viewDistance);
}

TessellationControlPoint TessellationVertex( Attributes input )
{
    TessellationControlPoint output;
    output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
    output.positionOS = input.positionOS;
    output.texcoord.xy = input.texcoord;
    output.texcoord.zw = output.positionWS.xz;
    return output;
}

HS_ConstantOutput HSConstant( InputPatch<TessellationControlPoint, 3> Input )
{
    float3 p0 = TransformObjectToWorld(Input[0].positionOS.xyz);
    float3 p1 = TransformObjectToWorld(Input[1].positionOS.xyz);
    float3 p2 = TransformObjectToWorld(Input[2].positionOS.xyz);
    HS_ConstantOutput output = (HS_ConstantOutput)0;
    output.TessFactor[0] = TessellationEdgeFactor(p1, p2);
    output.TessFactor[1] = TessellationEdgeFactor(p2, p0);
    output.TessFactor[2] = TessellationEdgeFactor(p0, p1);
    output.InsideTessFactor = (TessellationEdgeFactor(p1, p2) +
                                TessellationEdgeFactor(p2, p0) +
                                TessellationEdgeFactor(p0, p1)) * (1 / 3.0);
    return output;
}

[domain("tri")]
[partitioning("fractional_odd")]
[outputtopology("triangle_cw")]
[patchconstantfunc("HSConstant")]
[outputcontrolpoints(3)]
TessellationControlPoint Hull( InputPatch<TessellationControlPoint, 3> Input, uint uCPID : SV_OutputControlPointID )
{
    return Input[uCPID];
}



// Domain: replaces vert for tessellation version
[domain("tri")]
Varyings Domain( HS_ConstantOutput HSConstantData, const OutputPatch<TessellationControlPoint, 3> input, float3 BarycentricCoords : SV_DomainLocation)
{
    Varyings output = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
	UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
	/////////////////////Tessellation////////////////////////
	float fU = BarycentricCoords.x;
	float fV = BarycentricCoords.y;
	float fW = BarycentricCoords.z;

    /*
	float4 vertex = input[0].positionOS * fU + input[1].positionOS * fV + input[2].positionOS * fW;
    */
	output.uv = input[0].texcoord * fU + input[1].texcoord * fV + input[2].texcoord * fW;
	output.positionWS = input[0].positionWS * fU + input[1].positionWS * fV + input[2].positionWS * fW;

    output = WaveVertexOperations(output);

    return output;
}

#endif // WATER_TESSELLATION_INCLUDED
