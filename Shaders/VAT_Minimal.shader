// The smallest thing that reads a Vertex Animation Texture and draws it, written to be read rather
// than shipped. One pass, no lighting, no keywords, no cross-fading. If you want to see what a VAT
// actually is, this file is the whole idea in about a dozen lines of HLSL.
//
// Deliberately does NOT include VAT_Core.hlsl. The arithmetic is spelled out here instead, so the file
// can be understood on its own and copied into a shader you already have. That means it is duplicated:
// if the texture layout in VAT_Core.hlsl ever changes, this has to change with it.
//
// It reads slice 0 only, so it plays the first baked clip and ignores the rest. Being one pass, it
// casts no shadows and writes no depth, which is exactly why the Lit shader is six passes and not one.
Shader "Mi/Vertex Animation/Minimal (Example)"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)

        // Written by the baker. Everything here except the texture describes its layout.
        [NoScaleOffset] _VATPositionTex("Position Array", 2DArray) = "" {}
        _VATTextureWidth("Texture Width", Float) = 1
        _VATTextureHeight("Slice Height", Float) = 1
        _VATRowsPerFrame("Rows Per Frame", Float) = 1

        // (frames, rate) for clip 0 in xy, clip 1 in zw. Only clip 0 is read here.
        [HideInInspector] _VATClipData0("Clip Data 0", Vector) = (1,24,1,24)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma require 2darray
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                float  _VATTextureWidth;
                float  _VATTextureHeight;
                float  _VATRowsPerFrame;
                float4 _VATClipData0;
            CBUFFER_END

            TEXTURE2D(_BaseMap);              SAMPLER(sampler_BaseMap);
            TEXTURE2D_ARRAY(_VATPositionTex); SAMPLER(sampler_VATPositionTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;

                // The whole trick. The mesh's own position is never used: this index is what says which
                // texel of the animation belongs to this vertex.
                uint   vertexID   : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                float frames = max(_VATClipData0.x, 1.0);
                float rate   = max(_VATClipData0.y, 1.0);

                // Which frame to be on. frac() wraps the clip, and multiplying by the frame count turns
                // a position in the loop into a row block in the texture.
                float frame = floor(frac(_Time.y * rate / frames) * frames);

                // Where this vertex sits in that block. Vertices run left to right and wrap onto the next
                // row, so a frame occupies rowsPerFrame rows and there is no limit on vertex count.
                float vid = (float)input.vertexID;
                float col = fmod(vid, _VATTextureWidth);
                float row = floor(vid / _VATTextureWidth) + _VATRowsPerFrame * frame;

                // The half texel is what lands on the centre of the texel rather than its corner.
                float2 vatUV = float2((col + 0.5) / _VATTextureWidth,
                                      (row + 0.5) / _VATTextureHeight);

                // LOD 0 explicitly: a vertex shader has no derivatives to pick a mip from, and this
                // texture has no mips anyway.
                float3 positionOS = SAMPLE_TEXTURE2D_ARRAY_LOD(_VATPositionTex, sampler_VATPositionTex,
                    vatUV, 0, 0).xyz;

                Varyings output;
                output.positionCS = TransformObjectToHClip(positionOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
            }
            ENDHLSL
        }
    }
}
