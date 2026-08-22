// Minimal URP lit shader that reads vertex positions/normals from a Vertex Animation Texture.
//
// Every pass applies the same VAT displacement, so shadows and the depth/normals prepass
// match the visible geometry. Those extra passes each re-run the vertex texture fetches,
// which is why keeping this shader to two samplers matters.
//
// Not supported on purpose (keep it simple; add if you need it):
//   lightmaps, normal maps (needs baked tangents), decal/rendering layers, motion vectors.
Shader "Mi/Vertex Animation/Lit"
{
    Properties
    {
        [Header(Surface)] [Space(4)]
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
        _Smoothness("Smoothness", Range(0,1)) = 0.5
        _Metallic("Metallic", Range(0,1)) = 0.0

        [Toggle(_ALPHATEST_ON)] _AlphaClip("Alpha Clip", Float) = 0
        _Cutoff("Alpha Cutoff", Range(0,1)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Render Face", Float) = 2

        [Header(VAT)] [Space(4)]
        [NoScaleOffset] _VATPositionTex("Position Array", 2DArray) = "" {}
        [NoScaleOffset] _VATNormalTex("Normal Array", 2DArray) = "" {}
        _VATTextureWidth("Texture Width", Float) = 1
        _VATTextureHeight("Slice Height", Float) = 1
        _VATRowsPerFrame("Rows Per Frame", Float) = 1
        _VATClipCount("Clip Count", Float) = 1

        // (frames, rate) for two clips each - see VAT_Core.hlsl. Written by the baker.
        [HideInInspector] _VATClipData0("Clip Data 0", Vector) = (1,24,1,24)
        [HideInInspector] _VATClipData1("Clip Data 1", Vector) = (1,24,1,24)
        [HideInInspector] _VATClipData2("Clip Data 2", Vector) = (1,24,1,24)
        [HideInInspector] _VATClipData3("Clip Data 3", Vector) = (1,24,1,24)
        [HideInInspector] _VATClipData4("Clip Data 4", Vector) = (1,24,1,24)
        [HideInInspector] _VATClipData5("Clip Data 5", Vector) = (1,24,1,24)
        [HideInInspector] _VATClipData6("Clip Data 6", Vector) = (1,24,1,24)
        [HideInInspector] _VATClipData7("Clip Data 7", Vector) = (1,24,1,24)

        [Header(Playback)] [Space(4)]
        _VATClip("Clip Index", Float) = 0
        _VATSpeed("Playback Speed", Float) = 1
        _VATBlendDuration("Clip Blend Duration", Float) = 0.15
        _VATPhaseVariation("Phase Variation", Range(0,1)) = 0
        [Toggle(_VAT_FRAMEBLEND)] _VATFrameBlend("Frame Blend", Float) = 0

        // Both off by default, so a material baked before either existed is read exactly as it was written.
        [Toggle(_VAT_NONORMALS)] _VATNoNormals("Bind Pose Normals", Float) = 0
        [Toggle(_VAT_NORMALS8)] _VATNormals8("8-bit Normals", Float) = 0
        [Toggle(_VAT_NORMALSOCT)] _VATNormalsOct("Octahedral Normals", Float) = 0

        [Toggle(_VAT_POSNORM)] _VATPositionNormalized("Normalized Positions", Float) = 0
        [HideInInspector] _VATPositionMin("Position Min", Vector) = (0,0,0,0)
        [HideInInspector] _VATPositionExtent("Position Extent", Vector) = (1,1,1,0)

        [Header(Sections (Experimental))] [Space(4)]
        [Toggle(_VAT_SECTIONS)] _VATSections("Mesh Sections", Float) = 0
        [HideInInspector] _VATPivotTex("Section Pivots", 2DArray) = "" {}
        [HideInInspector] _VATSectionCount("Section Count", Float) = 0
        [HideInInspector] _VATPivotHeight("Pivot Rows", Float) = 1
        [HideInInspector] _VATSectionFromRot0("Section 0 From", Vector) = (0,0,0,1)
        [HideInInspector] _VATSectionToRot0("Section 0 To", Vector) = (0,0,0,1)
        [HideInInspector] _VATSectionFromOff0("Section 0 From Offset", Vector) = (0,0,0,0)
        [HideInInspector] _VATSectionToOff0("Section 0 To Offset", Vector) = (0,0,0,0)
        [HideInInspector] _VATSectionFromRot1("Section 1 From", Vector) = (0,0,0,1)
        [HideInInspector] _VATSectionToRot1("Section 1 To", Vector) = (0,0,0,1)
        [HideInInspector] _VATSectionFromOff1("Section 1 From Offset", Vector) = (0,0,0,0)
        [HideInInspector] _VATSectionToOff1("Section 1 To Offset", Vector) = (0,0,0,0)
        [HideInInspector] _VATSectionFromRot2("Section 2 From", Vector) = (0,0,0,1)
        [HideInInspector] _VATSectionToRot2("Section 2 To", Vector) = (0,0,0,1)
        [HideInInspector] _VATSectionFromOff2("Section 2 From Offset", Vector) = (0,0,0,0)
        [HideInInspector] _VATSectionToOff2("Section 2 To Offset", Vector) = (0,0,0,0)
        [HideInInspector] _VATSectionFromRot3("Section 3 From", Vector) = (0,0,0,1)
        [HideInInspector] _VATSectionToRot3("Section 3 To", Vector) = (0,0,0,1)
        [HideInInspector] _VATSectionFromOff3("Section 3 From Offset", Vector) = (0,0,0,0)
        [HideInInspector] _VATSectionToOff3("Section 3 To Offset", Vector) = (0,0,0,0)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma require 2darray
            #pragma vertex vert
            #pragma fragment frag

            #pragma shader_feature_local_vertex _VAT_FRAMEBLEND
            #pragma shader_feature_local_vertex _VAT_NONORMALS
            #pragma shader_feature_local_vertex _VAT_NORMALS8
            #pragma shader_feature_local_vertex _VAT_NORMALSOCT
            #pragma shader_feature_local_vertex _VAT_POSNORM
            #pragma shader_feature_local_vertex _VAT_SECTIONS
            #pragma shader_feature_local_fragment _ALPHATEST_ON

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "VAT_Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float4 sectionMask : TEXCOORD3;
                uint   vertexID   : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                float  fogFactor  : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // The mesh's own position/normal are discarded - the texture is the animation.
                float3 positionOS, normalOS;
                VAT_Sample(input.vertexID, input.normalOS, input.sectionMask, positionOS, normalOS);

                VertexPositionInputs pos = GetVertexPositionInputs(positionOS);
                output.positionCS = pos.positionCS;
                output.positionWS = pos.positionWS;
                output.normalWS   = TransformObjectToWorldNormal(normalOS);
                output.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor  = ComputeFogFactor(pos.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

            #if defined(_ALPHATEST_ON)
                clip(albedo.a - _Cutoff);
            #endif

                SurfaceData surface = (SurfaceData)0;
                surface.albedo     = albedo.rgb;
                surface.alpha      = 1.0;
                surface.metallic   = _Metallic;
                surface.smoothness = _Smoothness;
                surface.occlusion  = 1.0;
                surface.normalTS   = half3(0, 0, 1);

                float3 normalWS = normalize(input.normalWS);

                InputData inputData = (InputData)0;
                inputData.positionWS      = input.positionWS;
                inputData.normalWS        = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord     = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord        = input.fogFactor;
                // Ambient from probes only - animated meshes are never lightmapped.
                inputData.bakedGI         = SampleSH(normalWS);
                inputData.shadowMask      = half4(1, 1, 1, 1);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                half4 color = UniversalFragmentPBR(inputData, surface);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma require 2darray
            #pragma vertex vert
            #pragma fragment frag

            #pragma shader_feature_local_vertex _VAT_FRAMEBLEND
            #pragma shader_feature_local_vertex _VAT_NONORMALS
            #pragma shader_feature_local_vertex _VAT_NORMALS8
            #pragma shader_feature_local_vertex _VAT_NORMALSOCT
            #pragma shader_feature_local_vertex _VAT_POSNORM
            #pragma shader_feature_local_vertex _VAT_SECTIONS
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing

            #include "VAT_Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float4 sectionMask : TEXCOORD3;
                uint   vertexID   : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionOS, normalOS;
                VAT_Sample(input.vertexID, input.normalOS, input.sectionMask, positionOS, normalOS);

                float3 positionWS = TransformObjectToWorld(positionOS);
                float3 normalWS   = TransformObjectToWorldNormal(normalOS);

            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #endif

                output.positionCS = positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                VAT_AlphaClip(input.uv);

                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma require 2darray
            #pragma vertex vert
            #pragma fragment frag

            #pragma shader_feature_local_vertex _VAT_FRAMEBLEND
            #pragma shader_feature_local_vertex _VAT_NONORMALS
            #pragma shader_feature_local_vertex _VAT_NORMALS8
            #pragma shader_feature_local_vertex _VAT_NORMALSOCT
            #pragma shader_feature_local_vertex _VAT_POSNORM
            #pragma shader_feature_local_vertex _VAT_SECTIONS
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_instancing

            // This pass writes depth or an id and never reads the normal.
            #define VAT_NORMALS_UNUSED
            #include "VAT_Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float4 sectionMask : TEXCOORD3;
                uint   vertexID   : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionOS, normalOS;
                VAT_Sample(input.vertexID, input.sectionMask, positionOS, normalOS);

                output.positionCS = TransformObjectToHClip(positionOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                VAT_AlphaClip(input.uv);

                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma require 2darray
            #pragma vertex vert
            #pragma fragment frag

            #pragma shader_feature_local_vertex _VAT_FRAMEBLEND
            #pragma shader_feature_local_vertex _VAT_NONORMALS
            #pragma shader_feature_local_vertex _VAT_NORMALS8
            #pragma shader_feature_local_vertex _VAT_NORMALSOCT
            #pragma shader_feature_local_vertex _VAT_POSNORM
            #pragma shader_feature_local_vertex _VAT_SECTIONS
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_instancing

            #include "VAT_Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float4 sectionMask : TEXCOORD3;
                uint   vertexID   : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD1;
                float3 normalWS   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionOS, normalOS;
                VAT_Sample(input.vertexID, input.normalOS, input.sectionMask, positionOS, normalOS);

                output.positionCS = TransformObjectToHClip(positionOS);
                output.normalWS   = TransformObjectToWorldNormal(normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                VAT_AlphaClip(input.uv);

                return half4(normalize(input.normalWS), 0.0);
            }
            ENDHLSL
        }

        // Editor-only. Shader Graph generates these automatically; a hand-written shader has to
        // declare them or the scene-view selection outline and click-picking fall back to the
        // undeformed bind pose.
        Pass
        {
            Name "SceneSelectionPass"
            Tags { "LightMode" = "SceneSelectionPass" }

            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma require 2darray
            #pragma vertex vert
            #pragma fragment frag
            #pragma editor_sync_compilation
            #pragma multi_compile_instancing

            #pragma shader_feature_local_vertex _VAT_FRAMEBLEND
            #pragma shader_feature_local_vertex _VAT_NONORMALS
            #pragma shader_feature_local_vertex _VAT_NORMALS8
            #pragma shader_feature_local_vertex _VAT_NORMALSOCT
            #pragma shader_feature_local_vertex _VAT_POSNORM
            #pragma shader_feature_local_vertex _VAT_SECTIONS
            #pragma shader_feature_local_fragment _ALPHATEST_ON

            // This pass writes depth or an id and never reads the normal.
            #define VAT_NORMALS_UNUSED
            #include "VAT_Core.hlsl"

            int _ObjectId;
            int _PassValue;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float4 sectionMask : TEXCOORD3;
                uint   vertexID   : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float3 positionOS, normalOS;
                VAT_Sample(input.vertexID, input.sectionMask, positionOS, normalOS);
                output.positionCS = TransformObjectToHClip(positionOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                VAT_AlphaClip(input.uv);

                return half4(_ObjectId, _PassValue, 1.0, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ScenePickingPass"
            Tags { "LightMode" = "Picking" }

            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma require 2darray
            #pragma vertex vert
            #pragma fragment frag
            #pragma editor_sync_compilation
            #pragma multi_compile_instancing

            #pragma shader_feature_local_vertex _VAT_FRAMEBLEND
            #pragma shader_feature_local_vertex _VAT_NONORMALS
            #pragma shader_feature_local_vertex _VAT_NORMALS8
            #pragma shader_feature_local_vertex _VAT_NORMALSOCT
            #pragma shader_feature_local_vertex _VAT_POSNORM
            #pragma shader_feature_local_vertex _VAT_SECTIONS
            #pragma shader_feature_local_fragment _ALPHATEST_ON

            // This pass writes depth or an id and never reads the normal.
            #define VAT_NORMALS_UNUSED
            #include "VAT_Core.hlsl"

            float4 _SelectionID;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float4 sectionMask : TEXCOORD3;
                uint   vertexID   : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float3 positionOS, normalOS;
                VAT_Sample(input.vertexID, input.sectionMask, positionOS, normalOS);
                output.positionCS = TransformObjectToHClip(positionOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                VAT_AlphaClip(input.uv);

                return _SelectionID;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
