cbuffer UnityPerMaterial // TODO: Macro Include
{
    float4 _Color;
};

TEXTURE2D(_StochasticTexture);
SAMPLER(sampler_StochasticTexture);

float4 _BlueNoiseParams;
int _MSAASampleCount;

struct Interpolators
{
    float4 positionCS : SV_POSITION;
	float2 texcoord   : TEXCOORD0;
};

Interpolators VertexFullScreen(uint vertexID : SV_VertexID)
{
	Interpolators o;
	o.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
	o.texcoord   = GetFullScreenTriangleTexCoord(vertexID);
	return o;
}

Interpolators Vertex(float4 vertex : POSITION, float2 texcoord : TEXCOORD0)
{
    Interpolators o;
    o.positionCS = TransformObjectToHClip(vertex.xyz);
	o.texcoord = texcoord;
    return o;
}

//-------------------------------------------------------------------------
// 1.) Alpha Transmission Pass
//-------------------------------------------------------------------------
float FragmentTransmission(Interpolators i) : SV_Target
{
    return _Color.a;
}

//-------------------------------------------------------------------------
// 2.) Stochastic Depths Pass
//-------------------------------------------------------------------------

// NOTE: This can be improved by building a mask on cpu, to only sample once.
uint StochasticMask(float alpha, uint2 positionSS, uint primitiveID)
{
    float2 positionNDC = positionSS * (1.0 / float2(1920.0, 1080.0));

    // Generate mask.
    uint mask = 0;
    
    for(int s = 0; s < _MSAASampleCount; ++s)
    {
        float2 offset = _BlueNoiseParams.zw * (primitiveID + 1) * (s + 1);

        float blueNoise = SAMPLE_TEXTURE2D(_StochasticTexture, sampler_StochasticTexture, positionNDC * _BlueNoiseParams.xy + offset ).a;
        if(alpha > blueNoise)
        {
            mask |= (1 << s);
        }
    }

    return mask;
}

uint FragmentStochasticDepths(Interpolators i, uint primitiveID : SV_PrimitiveID) : SV_Coverage
{
    return StochasticMask(_Color.a, i.positionCS.xy, primitiveID);
} 

//-------------------------------------------------------------------------
//3.) Stochastic Colors Pass
//-------------------------------------------------------------------------
float4 FragmentStochasticColors(Interpolators i) : SV_Target
{
    return float4(_Color.rgb * _Color.a, _Color.a);
}

//-------------------------------------------------------------------------
//4.) Final Pass
//-------------------------------------------------------------------------
Texture2DMS<float,  8> _TransmissionBuffer;
Texture2DMS<float4, 8> _BackgroundBuffer;
Texture2D<float4>	   _StochasticColorBuffer;

float4 FragmentFinalPass(Interpolators i) : SV_Target
{
	float3 resolvedBackgroundColor = 0.0;
	float  resolvedTransmittance   = 0.0;

	[unroll]
	for (int sampleId = 0; sampleId < 8; ++sampleId)
	{
		float T = _TransmissionBuffer.Load(i.positionCS.xy, sampleId).r;
		resolvedBackgroundColor += T * _BackgroundBuffer.Load(i.positionCS.xy, sampleId).rgb;
		resolvedTransmittance   += T;
	}

	// TODO: Currently hardcoded MSAA Samples.
	resolvedBackgroundColor *= 1.0 / 8.0;
	resolvedTransmittance   *= 1.0 / 8.0;

	// NOTE: At the moment, we only accumulate/simulate 1x8MSAA sample.
	float4 resolvedTransparenctColor = _StochasticColorBuffer.Load(int3(i.positionCS.xy, 0));
	resolvedTransparenctColor *= ((1.0 - resolvedTransmittance) / resolvedTransparenctColor.a); // Alpha Correction

	return float4(resolvedTransparenctColor.rgb + resolvedBackgroundColor.rgb, 1.0);
}