Shader "FluidCrowd/Body"
{
    Properties
    {
        _MinBrightness ("Minimum brightness at zero health", Range(0, 1)) = 0.25

        _Roundness ("Roundness", Range(0, 1)) = 1

        _Depth ("Depth", Float) = 0

        _Outline ("Outline width", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "TintedIndirectUnlit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct InstanceData
            {
                float4 body;
                float4 facing;
            };

            StructuredBuffer<InstanceData> _Instances;

            float4 _Palette[8];
            float _MinBrightness;
            float _Roundness;
            float _Depth;
            float _Outline;

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                half3 tint : TEXCOORD0;

                float4 shape : TEXCOORD1;

                float2 corner : TEXCOORD2;
            };

            Varyings Vert(Attributes input, uint instanceID : SV_InstanceID)
            {
                InstanceData instance = _Instances[instanceID];

                float2 local = input.positionOS.xy * instance.body.zw;
                float2 facing = instance.facing.xy;
                float2 halfSize = instance.body.zw * 0.5;

                float2 turned = float2(
                    local.x * facing.x - local.y * facing.y,
                    local.x * facing.y + local.y * facing.x);

                float3 positionWS = float3(turned + instance.body.xy, _Depth);

                int paletteIndex = (int)floor(instance.facing.z);
                float healthFraction = instance.facing.z - (float)paletteIndex;

                float squareness = saturate(instance.facing.w);

                float hollow = max(0.0, -instance.facing.w);

                Varyings output;
                output.positionHCS = TransformWorldToHClip(positionWS);
                output.tint = (half3)(_Palette[paletteIndex].rgb * lerp(_MinBrightness, 1.0, healthFraction));
                output.shape = float4(local, halfSize);
                output.corner = float2(
                    min(halfSize.x, halfSize.y) * _Roundness * (1.0 - squareness), hollow);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 p = input.shape.xy;
                float2 h = input.shape.zw;

                float radius = input.corner.x;
                float2 q = abs(p) - (h - radius);

                float distance = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;

                float wall = input.corner.y;

                clip(wall > 0.0 ? min(-distance, distance + wall) : -distance);

                float edge = wall > 0.0 ? 0.0 : min(_Outline, min(h.x, h.y) * 0.35);

                half3 colour = distance > -edge ? half3(0.0h, 0.0h, 0.0h) : input.tint;

                return half4(colour, 1.0h);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
