Shader "BabelTime/ClipmapExampleShader"
{
    Properties
    {
       [MainTexture] ClipmapTex("Clipmap Array", 2DArray) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline"}
        LOD 100

        Pass
        {
            Name "TestClipmap"
            Tags {"LightMode"="UniversalForward"}

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma require 2darray

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float2 uv : TEXCOORD0;
                float4 positionCS : SV_POSITION;
            };

            TEXTURE2D_ARRAY(ClipmapTex);
            SAMPLER(sampler_ClipmapTex);    //需要定义为Point采样模式; 
            
            CBUFFER_START(UnityPerMaterial)
                //[xy=Layer左下角anchor的绝对UV, z=Layer的UV跨度, w=Layer中1个像素对应的UV跨度]; 
                float4 ClipmapTex_LayerAnchor[5];
            CBUFFER_END

            Varyings Vert (Attributes input)
            {
                Varyings output;
                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = vertexInput.positionCS;
                output.uv = input.uv;
                return output;
            }

            int CalcBestMipOfClipmap5(float2 uv)
            {
                //考虑到可能需要采样当前纹素 + 右侧 + 上方等(+1)临近纹素信息; 
                //这里对右侧和上方边界做了缩减处理(缩减1个纹素的尺寸);
                //确保所有落在当前Mip等级的采样能获取到符合要求的领域数据; 
                int mip = 4;    
                int2 flag = (uv.xy >= ClipmapTex_LayerAnchor[3].xy && uv.xy < (ClipmapTex_LayerAnchor[3].xy + ClipmapTex_LayerAnchor[3].zz - ClipmapTex_LayerAnchor[3].ww)) ? 1 : 0;
                mip = mip - flag.x * flag.y;
                flag = (uv.xy >= ClipmapTex_LayerAnchor[2].xy && uv.xy < (ClipmapTex_LayerAnchor[2].xy + ClipmapTex_LayerAnchor[2].zz - ClipmapTex_LayerAnchor[2].ww)) ? 1 : 0;
                mip = mip - flag.x * flag.y;
                flag = (uv.xy >= ClipmapTex_LayerAnchor[1].xy && uv.xy < (ClipmapTex_LayerAnchor[1].xy + ClipmapTex_LayerAnchor[1].zz - ClipmapTex_LayerAnchor[1].ww)) ? 1 : 0;
                mip = mip - flag.x * flag.y;
                flag = (uv.xy >= ClipmapTex_LayerAnchor[0].xy && uv.xy < (ClipmapTex_LayerAnchor[0].xy + ClipmapTex_LayerAnchor[0].zz - ClipmapTex_LayerAnchor[0].ww)) ? 1 : 0;
                mip = mip - flag.x * flag.y;
                return mip;
            }

            static const half4 kDebugLayerColors[8] =
            {
                half4(1.0, 0.0, 0.0, 1.0), // Layer 0 red 
                half4(0.0, 1.0, 0.0, 1.0), // Layer 1 Green
                half4(0.0, 0.0, 1.0, 1.0), // Layer 2 Blue
                half4(1.0, 1.0, 0.0, 1.0), // Layer 3 yellow
                half4(0.0, 1.0, 1.0, 1.0), // Layer 4 Cyan
                half4(1.0, 0.0, 1.0, 1.0), // Layer 5
                half4(0.5, 0.0, 0.5, 1.0), // Layer 6
                half4(0.5, 0.5, 0.5, 1.0)  // Layer 7
            };

            half4 GetDebugColor(int layer)
            {
                return kDebugLayerColors[layer];
            }

            half4 Frag (Varyings input) : SV_Target
            {
                int bestMip = CalcBestMipOfClipmap5(input.uv);
                float2 sampleUV = input.uv / ClipmapTex_LayerAnchor[bestMip].zz;    //rolling buffer mechanism 
                half4 col = SAMPLE_TEXTURE2D_ARRAY(ClipmapTex, sampler_ClipmapTex, sampleUV, bestMip).rgba;
                col = lerp(col.rgba, GetDebugColor(bestMip).rgba, 0.07);
                return col;
            }
            
            ENDHLSL
        }
    }
}
