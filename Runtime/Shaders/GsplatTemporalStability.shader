// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

Shader "Gsplat/TemporalStability"
{
    Properties
    {
        _MainTex ("Current Frame", 2D) = "white" {}
        _HistoryTex ("History Frame", 2D) = "white" {}
        _BlendFactor ("Blend Factor", Range(0, 1)) = 0.3
        _ColorThreshold ("Color Threshold", Range(0, 1)) = 0.1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            sampler2D _HistoryTex;
            float _BlendFactor;
            float _ColorThreshold;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 current = tex2D(_MainTex, i.uv);
                float4 history = tex2D(_HistoryTex, i.uv);

                // Calculate color difference
                float3 diff = abs(current.rgb - history.rgb);
                float colorDiff = max(max(diff.r, diff.g), diff.b);

                // Alpha difference
                float alphaDiff = abs(current.a - history.a);

                // Determine stability
                bool stable = colorDiff < _ColorThreshold && alphaDiff < _ColorThreshold;

                // Blend if stable, otherwise use current
                float4 result;
                if (stable)
                {
                    result = lerp(current, history, _BlendFactor);
                }
                else
                {
                    result = current;
                }

                return result;
            }
            ENDHLSL
        }
    }
}
