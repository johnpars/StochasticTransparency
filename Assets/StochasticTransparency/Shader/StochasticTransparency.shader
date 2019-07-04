Shader "StochasticRasterizer/UnlitStochastic"
{
    Properties
    {
        _MainTex ("_MainTex (RGBA)", 2D) = "white" {}
		_Color("Main Color", Color) = (1,1,1,1)
	}
	SubShader
	{
		Tags { "RenderType" = "Opaque" }

		//-------------------------------------------------------------------------
		// 1.) Alpha Transmission Pass
		//-------------------------------------------------------------------------
		Pass
		{
			Tags { "LightMode" = "Transmittance" }

			Blend Zero OneMinusSrcColor, One One
			ZWrite Off ZTest Off ZClip Off

			HLSLPROGRAM

			#include "Library/Transformation.hlsl"
			#include "StochasticTransparency.hlsl"

			#pragma target 5.0

			#pragma vertex   Vertex
			#pragma fragment FragmentTransmission

			ENDHLSL
		}

		//-------------------------------------------------------------------------
		// 2.) Stochastic Depths Pass
		//-------------------------------------------------------------------------
		Pass
		{
			Tags { "LightMode" = "StochasticDepths" }

			ZWrite On 
			ZTest LEqual

			HLSLPROGRAM
			
			#include "Library/Transformation.hlsl"
			#include "StochasticTransparency.hlsl"

			#pragma target 5.0

			#pragma vertex	 Vertex
			#pragma fragment FragmentStochasticDepths

			ENDHLSL
		}

		//-------------------------------------------------------------------------
		// 3.) Stochastic Color Pass
		//-------------------------------------------------------------------------
		Pass
		{
			Tags { "LightMode" = "StochasticColors" }

			Blend One One

			ZWrite Off 
			ZTest LEqual

			HLSLPROGRAM
			
			#include "Library/Transformation.hlsl"
			#include "StochasticTransparency.hlsl"

			#pragma target 5.0

			#pragma vertex	 Vertex
			#pragma fragment FragmentStochasticColors

			ENDHLSL
		}


		Pass
		{
			Tags { "LightMode" = "StochasticTransparency" }

			HLSLPROGRAM

			// NOTE: Required for SV_Coverage
			#pragma target 5.0


			#pragma vertex   Vertex
			#pragma fragment Fragment

			#include "Library/Transformation.hlsl"

			struct VertexInput
			{
				float4 vertex   : POSITION;
				float2 texcoord : TEXCOORD0;
			};

			struct Interpolators
			{
				float4 positionCS : SV_POSITION;
				float2 texcoord   : TEXCOORD0;
			};

			TEXTURE2D(_MainTex);
			SAMPLER(sampler_MainTex);

			float4 _MainTex_ST;
			float4 _Color;

			TEXTURE2D(_AlphaMaskTexture);
			SAMPLER(sampler_AlphaMaskTexture);

			float _SubframeIndex;

			TEXTURE2D(_StochasticTexture);
			SAMPLER(sampler_StochasticTexture);

			float _Jitter;

			uint _MSAASampleCount;

			Interpolators Vertex (VertexInput i)
			{
				Interpolators o;
				o.positionCS = TransformObjectToHClip(i.vertex.xyz);
				o.texcoord   = i.texcoord;
				return o;
			}

			float hash12(float2 p)
			{
				float3 p3 = frac(float3(p.xyx) * .1031);
				p3 += dot(p3, p3.yzx + 19.19);
				return frac((p3.x + p3.y) * p3.z);
			}

			float GetNoise(float2 p, int offset)
			{
				int ITERATIONS = 4;
				float a = 0.0, b = a;
				for (int t = 0; t < ITERATIONS; t++)
				{
					float v = float(t + 1) * .152;
					float2 pos = (p * v + offset + 50.0);
					a += hash12(pos);
				}

				return a / float(ITERATIONS);
			}

			float4 _BlueNoiseParams;

			bool StochasticTransparency(float alpha, float2 positionSS, uint subsampleIndex, uint primitiveID)
			{
				// Generate unique sub-sample coordinate
				float2 screenUV = positionSS * (1.0 / float2(1920.0, 1080.0));
				float2 coord = screenUV * _BlueNoiseParams.xy + (_BlueNoiseParams.zw * (subsampleIndex + primitiveID));// +subsampleIndex + primitiveID + _Time.y;

				// Sample Blue Noise
				float blueNoise = SAMPLE_TEXTURE2D(_StochasticTexture, sampler_StochasticTexture, coord	).a;
				//float blueNoise = GetNoise(positionSS, subsampleIndex + primitiveID + _Time.y * 1000);

				// Test
				return alpha > blueNoise;
			}

			float4 Fragment (Interpolators i, uint primitiveID : SV_PrimitiveID, out uint coverageMask : SV_Coverage) : SV_Target
			{
				// Sample Visibility
				coverageMask = 0;
				for (uint s = 0; s < _MSAASampleCount; ++s) 
				{
					if (StochasticTransparency(_Color.a, i.positionCS.xy, s, primitiveID))
					{
						coverageMask |= (1 << s);
					}
				}
				if (coverageMask == 0) discard;

				// Shade as normal
				return float4( _Color.rgb, 1 );
			}

			ENDHLSL
		}
    }
}

