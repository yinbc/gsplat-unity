// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

Shader "Gsplat/Interpolated"
{
    Properties {}
    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "Queue"="Transparent"
        }

        Pass
        {
            ZWrite Off
            Blend One OneMinusSrcAlpha
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma require compute
            #pragma multi_compile SH_BANDS_0 SH_BANDS_1 SH_BANDS_2 SH_BANDS_3

            #include "UnityCG.cginc"
            #include "Gsplat.hlsl"

            bool _GammaToLinear;
            int _SplatCount;
            int _SplatInstanceSize;
            int _SHDegree;
            float4x4 _MATRIX_M;
            float _InterpolationFactor; // 0.0 = frame A, 1.0 = frame B

            StructuredBuffer<uint> _OrderBuffer;

            // Frame A buffers
            StructuredBuffer<float3> _PositionBuffer;
            StructuredBuffer<float3> _ScaleBuffer;
            StructuredBuffer<float4> _RotationBuffer;
            StructuredBuffer<float4> _ColorBuffer;

            // Frame B buffers
            StructuredBuffer<float3> _PositionBufferB;
            StructuredBuffer<float3> _ScaleBufferB;
            StructuredBuffer<float4> _RotationBufferB;
            StructuredBuffer<float4> _ColorBufferB;

            #ifndef SH_BANDS_0
            StructuredBuffer<float3> _SHBuffer;
            StructuredBuffer<float3> _SHBufferB;
            #endif

            struct appdata
            {
                float4 vertex : POSITION;
                #if !defined(UNITY_INSTANCING_ENABLED) && !defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && !defined(UNITY_STEREO_INSTANCING_ENABLED)
                uint instanceID : SV_InstanceID;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            bool InitSource(appdata v, out SplatSource source)
            {
                #if !defined(UNITY_INSTANCING_ENABLED) && !defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && !defined(UNITY_STEREO_INSTANCING_ENABLED)
                source.order = v.instanceID * _SplatInstanceSize + asuint(v.vertex.z);
                #else
                source.order = unity_InstanceID * _SplatInstanceSize + asuint(v.vertex.z);
                #endif

                if (source.order >= _SplatCount)
                    return false;

                source.id = _OrderBuffer[source.order];
                source.cornerUV = float2(v.vertex.x, v.vertex.y);
                return true;
            }

            bool InitCenter(float3 modelCenter, out SplatCenter center)
            {
                float4x4 modelView = mul(UNITY_MATRIX_V, _MATRIX_M);
                float4 centerView = mul(modelView, float4(modelCenter, 1.0));
                if (centerView.z > 0.0)
                {
                    return false;
                }
                float4 centerProj = mul(UNITY_MATRIX_P, centerView);
                centerProj.z = clamp(centerProj.z, -abs(centerProj.w), abs(centerProj.w));
                center.view = centerView.xyz / centerView.w;
                center.proj = centerProj;
                center.projMat00 = UNITY_MATRIX_P[0][0];
                center.modelView = modelView;
                return true;
            }

            // Spherical linear interpolation for quaternions
            float4 Slerp(float4 q1, float4 q2, float t)
            {
                float dot = q1.x * q2.x + q1.y * q2.y + q1.z * q2.z + q1.w * q2.w;

                // If dot is negative, negate one quaternion to take shorter path
                if (dot < 0.0)
                {
                    q2 = -q2;
                    dot = -dot;
                }

                // If quaternions are very close, use linear interpolation
                if (dot > 0.9995)
                {
                    return normalize(lerp(q1, q2, t));
                }

                float theta = acos(dot);
                float sinTheta = sin(theta);
                float w1 = sin((1.0 - t) * theta) / sinTheta;
                float w2 = sin(t * theta) / sinTheta;

                return q1 * w1 + q2 * w2;
            }

            // Read and interpolate covariance
            SplatCovariance ReadCovarianceInterpolated(SplatSource source, float t)
            {
                float4 quatA = _RotationBuffer[source.id];
                float4 quatB = _RotationBufferB[source.id];
                float3 scaleA = _ScaleBuffer[source.id];
                float3 scaleB = _ScaleBufferB[source.id];

                // Interpolate rotation using slerp
                float4 quat = Slerp(quatA, quatB, t);
                // Interpolate scale linearly
                float3 scale = lerp(scaleA, scaleB, t);

                return CalcCovariance(quat, scale);
            }

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float4 color: COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                SplatSource source;
                if (!InitSource(v, source))
                {
                    o.vertex = discardVec;
                    return o;
                }

                float t = _InterpolationFactor;

                // Interpolate position
                float3 posA = _PositionBuffer[source.id];
                float3 posB = _PositionBufferB[source.id];
                float3 modelCenter = lerp(posA, posB, t);

                SplatCenter center;
                if (!InitCenter(modelCenter, center))
                {
                    o.vertex = discardVec;
                    return o;
                }

                SplatCovariance cov = ReadCovarianceInterpolated(source, t);
                SplatCorner corner;
                if (!InitCorner(source, cov, center, corner))
                {
                    o.vertex = discardVec;
                    return o;
                }

                // Interpolate color
                float4 colorA = _ColorBuffer[source.id];
                float4 colorB = _ColorBufferB[source.id];
                float4 color = lerp(colorA, colorB, t);

                color.rgb = color.rgb * SH_C0 + 0.5;
                #ifndef SH_BANDS_0
                // calculate the model-space view direction
                float3 dir = normalize(mul(center.view, (float3x3)center.modelView));
                float3 sh[SH_COEFFS];
                for (int i = 0; i < SH_COEFFS; i++)
                {
                    float3 shA = _SHBuffer[source.id * SH_COEFFS + i];
                    float3 shB = _SHBufferB[source.id * SH_COEFFS + i];
                    sh[i] = lerp(shA, shB, t);
                }
                color.rgb += EvalSH(sh, dir, _SHDegree);
                #endif

                ClipCorner(corner, color.w);

                o.vertex = center.proj + float4(corner.offset.x, _ProjectionParams.x * corner.offset.y, 0, 0);
                o.color = float4(max(color.rgb, float3(0, 0, 0)), color.a);
                o.uv = corner.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float A = dot(i.uv, i.uv);
                if (A > 1.0) discard;
                float alpha = exp(-A * 4.0) * i.color.a;
                if (alpha < 1.0 / 255.0) discard;
                if (_GammaToLinear)
                    return float4(GammaToLinearSpace(i.color.rgb) * alpha, alpha);
                return float4(i.color.rgb * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
