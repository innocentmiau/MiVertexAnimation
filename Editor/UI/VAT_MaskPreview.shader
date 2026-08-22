/*
 * Editor only, and it lives under Editor/ so it never ships in a build.
 *
 * The baker's preview draws plain meshes it deforms on the CPU, so this does no VAT sampling at all -
 * it paints whatever colour the window wrote into the mesh, which is the section weight ramped.
 * Unlit on purpose: a mask read through lighting would be darker on one side of the head than the
 * other and that shading would be mistaken for falloff.
 */
Shader "Hidden/Mi/VAT Mask Preview"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "MaskPreview"
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color      : COLOR;
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return half4(input.color.rgb, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
