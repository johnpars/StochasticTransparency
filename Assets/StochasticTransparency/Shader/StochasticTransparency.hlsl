cbuffer UnityPerMaterial // TODO: Macro Include
{
    float4 _Color;
};

TEXTURE2D(_StochasticTexture);
SAMPLER(sampler_StochasticTexture);

float4 _BlueNoiseParams;
int _MSAASampleCount;

struct VertexInput
{
    float4 vertex : POSITION;
};

struct Interpolators
{
    float4 positionCS : SV_POSITION;
};

Interpolators Vertex(VertexInput i)
{
    Interpolators o;
    o.positionCS = TransformObjectToHClip(i.vertex.xyz);
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