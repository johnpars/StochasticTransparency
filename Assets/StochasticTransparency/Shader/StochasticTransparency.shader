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

		//-------------------------------------------------------------------------
		// 4.) Final Pass
		//-------------------------------------------------------------------------
		Pass
		{
			Tags { "LightMode" = "StochasticFinalPass" }

			ZWrite Off
			ZTest Always
			Cull Off

			HLSLPROGRAM

			#include "Library/Transformation.hlsl"
			#include "StochasticTransparency.hlsl"

			#pragma target 5.0
			#pragma enable_d3d11_debug_symbols

			#pragma vertex   VertexFullScreen
			#pragma fragment FragmentFinalPass

			ENDHLSL
		}
    }
}

