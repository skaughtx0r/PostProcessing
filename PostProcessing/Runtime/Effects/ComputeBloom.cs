using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine.Rendering.PostProcessing
{
    [Serializable]
    [PostProcess(typeof(ComputeBloomRenderer), "Kokku/Compute Bloom")]
    public sealed class ComputeBloom : PostProcessEffectSettings
    {
        [Tooltip("High quality blurs 5 octaves of bloom; low quality only blurs 3.")]
        public BoolParameter HighQualityBloom = new BoolParameter { value = false };

        [Range(0f, 8f), Tooltip("The threshold luminance above which a pixel will start to bloom.")]
        public FloatParameter BloomThreshold = new FloatParameter { value = 1.0f };

        [Min(0f), Tooltip("A modulator controlling how much bloom is added back into the image.")]
        public FloatParameter BloomStrength = new FloatParameter { value = 0.1f };

        [Range(0f, 1f), Tooltip("Controls the \"focus\" of the blur.  High values spread out more causing a haze.")]
        public FloatParameter BloomUpsampleFactor = new FloatParameter { value = 0.65f };

        /// <summary>
        /// The tint of the Bloom filter.
        /// </summary>
#if UNITY_2018_1_OR_NEWER
        [ColorUsage(false, true), Tooltip("Global tint of the bloom filter.")]
#else
        [ColorUsage(false, true, 0f, 8f, 0.125f, 3f), Tooltip("Global tint of the bloom filter.")]
#endif
        public ColorParameter color = new ColorParameter { value = Color.white };

        /// <summary>
        /// The dirtiness texture to add smudges or dust to the lens.
        /// </summary>
        [Tooltip("The lens dirt texture used to add smudges or dust to the bloom effect."), DisplayName("Texture")]
        public TextureParameter dirtTexture = new TextureParameter { value = null };

        /// <summary>
        /// The amount of lens dirtiness.
        /// </summary>
        [Min(0f), Tooltip("The intensity of the lens dirtiness."), DisplayName("Intensity")]
        public FloatParameter dirtIntensity = new FloatParameter { value = 0f };
    }

    [UnityEngine.Scripting.Preserve]
    internal sealed class ComputeBloomRenderer : PostProcessEffectRenderer<ComputeBloom>
    {
        int SourceTex;
        int BloomResult;
        private int BloomBuf;
        private int Result;
        private int Result1;
        private int Result2;
        private int Result3;
        private int Result4;
        private int InputBuf;
        private int HigherResBuf;
        private int LowerResBuf;
        private int g_inverseDimensions;
        private int g_inverseOutputSize;

        public int g_upsampleBlendFactor { get; private set; }

        private int g_bloomThreshold;
        int[] g_aBloomUAV1;    // 640x384 (1/3)
        int[] g_aBloomUAV2;    // 320x192 (1/6)  
        int[] g_aBloomUAV3;    // 160x96  (1/12)
        int[] g_aBloomUAV4;    // 80x48   (1/24)
        int[] g_aBloomUAV5;    // 40x24   (1/48)

        RenderTextureDescriptor bloomUAVDesc1;
        RenderTextureDescriptor bloomUAVDesc2;
        RenderTextureDescriptor bloomUAVDesc3;
        RenderTextureDescriptor bloomUAVDesc4;
        RenderTextureDescriptor bloomUAVDesc5;

        public override void Init()
        {
            SourceTex = Shader.PropertyToID("SourceTex");
            BloomResult = Shader.PropertyToID("BloomResult");

            BloomBuf = Shader.PropertyToID("BloomBuf");
            Result = Shader.PropertyToID("Result");
            Result1 = Shader.PropertyToID("Result1");
            Result2 = Shader.PropertyToID("Result2");
            Result3 = Shader.PropertyToID("Result3");
            Result4 = Shader.PropertyToID("Result4");

            InputBuf = Shader.PropertyToID("InputBuf");
            HigherResBuf = Shader.PropertyToID("HigherResBuf");
            LowerResBuf = Shader.PropertyToID("LowerResBuf");

            g_inverseDimensions = Shader.PropertyToID("g_inverseDimensions");
            g_inverseOutputSize = Shader.PropertyToID("g_inverseOutputSize");
            g_upsampleBlendFactor = Shader.PropertyToID("g_upsampleBlendFactor");
            g_bloomThreshold = Shader.PropertyToID("g_bloomThreshold");

            g_aBloomUAV1 = new int[2];
            g_aBloomUAV2 = new int[2];
            g_aBloomUAV3 = new int[2];
            g_aBloomUAV4 = new int[2];
            g_aBloomUAV5 = new int[2];

            g_aBloomUAV1[0] = Shader.PropertyToID("_BloomBuffer1a");
            g_aBloomUAV1[1] = Shader.PropertyToID("_BloomBuffer1b");
            g_aBloomUAV2[0] = Shader.PropertyToID("_BloomBuffer2a");
            g_aBloomUAV2[1] = Shader.PropertyToID("_BloomBuffer2b");
            g_aBloomUAV3[0] = Shader.PropertyToID("_BloomBuffer3a");
            g_aBloomUAV3[1] = Shader.PropertyToID("_BloomBuffer3b");
            g_aBloomUAV4[0] = Shader.PropertyToID("_BloomBuffer4a");
            g_aBloomUAV4[1] = Shader.PropertyToID("_BloomBuffer4b");
            g_aBloomUAV5[0] = Shader.PropertyToID("_BloomBuffer5a");
            g_aBloomUAV5[1] = Shader.PropertyToID("_BloomBuffer5b");

            bloomUAVDesc1 = new RenderTextureDescriptor()
            {
                enableRandomWrite = true,
                colorFormat = RenderTextureFormat.RGB111110Float,
                depthBufferBits = 0,
                mipCount = 1,
                msaaSamples = 1,
                volumeDepth = 1,
                autoGenerateMips = false,
                dimension = TextureDimension.Tex2D,
                sRGB = false,
            };
            bloomUAVDesc2 = bloomUAVDesc1;
            bloomUAVDesc3 = bloomUAVDesc1;
            bloomUAVDesc4 = bloomUAVDesc1;
            bloomUAVDesc5 = bloomUAVDesc1;
        }

        public override void Render(PostProcessRenderContext context)
        {
            var cmd = context.command;
            var computeShaders = context.resources.computeShaders;

            cmd.BeginSample("ComputeBloom");

            bool highQuality = settings.HighQualityBloom.value;

#if UNITY_2019_1_OR_NEWER
            int screenWidth = (int)(context.screenWidth * ScalableBufferManager.widthScaleFactor);
            int screenHeight = (int)(context.screenHeight * ScalableBufferManager.heightScaleFactor);
#else
            int screenWidth = context.screenWidth;
            int screenHeight = context.screenHeight;
#endif

            int kBloomWidth = screenWidth > 2560 ? 1280 : 640;
            int kBloomHeight = screenHeight > 1440 ? 768 : 384;

#if UNITY_SWITCH
            kBloomWidth /= 2;
            kBloomHeight /= 2;
#endif

            // Bloom buffer 1
            bloomUAVDesc1.width = kBloomWidth;
            bloomUAVDesc1.height = kBloomHeight;
            cmd.GetTemporaryRT(g_aBloomUAV1[0], bloomUAVDesc1, FilterMode.Bilinear);
            cmd.GetTemporaryRT(g_aBloomUAV1[1], bloomUAVDesc1, FilterMode.Bilinear);

            // Bloom buffer 2
            if (highQuality)
            {
                bloomUAVDesc2.width = kBloomWidth / 2;
                bloomUAVDesc2.height = kBloomHeight / 2;
                cmd.GetTemporaryRT(g_aBloomUAV2[0], bloomUAVDesc2, FilterMode.Bilinear);
                cmd.GetTemporaryRT(g_aBloomUAV2[1], bloomUAVDesc2, FilterMode.Bilinear);
            }

            // Bloom buffer 3
            bloomUAVDesc3.width = kBloomWidth   / 4;
            bloomUAVDesc3.height = kBloomHeight / 4;
            cmd.GetTemporaryRT(g_aBloomUAV3[0], bloomUAVDesc3, FilterMode.Bilinear);
            cmd.GetTemporaryRT(g_aBloomUAV3[1], bloomUAVDesc3, FilterMode.Bilinear);

            // Bloom buffer 4
            if (highQuality)
            {
                bloomUAVDesc4.width = kBloomWidth / 8;
                bloomUAVDesc4.height = kBloomHeight / 8;
                cmd.GetTemporaryRT(g_aBloomUAV4[0], bloomUAVDesc4, FilterMode.Bilinear);
                cmd.GetTemporaryRT(g_aBloomUAV4[1], bloomUAVDesc4, FilterMode.Bilinear);
            }

            // Bloom buffer 5
            bloomUAVDesc5.width = kBloomWidth   / 16;
            bloomUAVDesc5.height = kBloomHeight / 16;
            cmd.GetTemporaryRT(g_aBloomUAV5[0], bloomUAVDesc5, FilterMode.Bilinear);
            cmd.GetTemporaryRT(g_aBloomUAV5[1], bloomUAVDesc5, FilterMode.Bilinear);

            // First downsample pass
            var bloomExtractAndDownsampleHdrCS = computeShaders.BloomExtractAndDownsampleHdr;
            cmd.SetComputeTextureParam(bloomExtractAndDownsampleHdrCS, 0, BloomResult, g_aBloomUAV1[0]);
            cmd.SetComputeTextureParam(bloomExtractAndDownsampleHdrCS, 0, SourceTex, context.source);
            cmd.SetComputeVectorParam(bloomExtractAndDownsampleHdrCS, g_inverseOutputSize, new Vector4(1.0f / kBloomWidth, 1.0f / kBloomHeight, 0, 0));
            cmd.SetComputeFloatParam(bloomExtractAndDownsampleHdrCS, g_bloomThreshold, settings.BloomThreshold.value);
            cmd.Dispatch2D(bloomExtractAndDownsampleHdrCS, 0, kBloomWidth, kBloomHeight);

            // Prepare for the next downsample passes
            var downsampleShader = highQuality ? computeShaders.BloomDownsample4 : computeShaders.BloomDownsample2;
            cmd.SetComputeTextureParam(downsampleShader, 0, BloomBuf, g_aBloomUAV1[0]);

            float upsampleBlendFactor = settings.BloomUpsampleFactor.value;
            cmd.SetComputeVectorParam(downsampleShader, g_inverseDimensions, new Vector4(1.0f / kBloomWidth, 1.0f / kBloomHeight, 0, 0));            
            cmd.SetComputeFloatParam(downsampleShader, g_upsampleBlendFactor, upsampleBlendFactor);

            // The difference between high and low quality bloom is that high quality sums 5 octaves with a 2x frequency scale, and the low quality
            // sums 3 octaves with a 4x frequency scale.
            if (settings.HighQualityBloom.value)
            {
                cmd.SetComputeTextureParam(downsampleShader, 0, Result1, g_aBloomUAV2[0]);
                cmd.SetComputeTextureParam(downsampleShader, 0, Result2, g_aBloomUAV3[0]);
                cmd.SetComputeTextureParam(downsampleShader, 0, Result3, g_aBloomUAV4[0]);
                cmd.SetComputeTextureParam(downsampleShader, 0, Result4, g_aBloomUAV5[0]);

                cmd.Dispatch2D(downsampleShader, 0, kBloomWidth / 2, kBloomHeight / 2);                

                // Blur then upsample and blur four times
                BlurBuffer(cmd, bloomUAVDesc5, g_aBloomUAV5, g_aBloomUAV5[0], 1.0f, computeShaders);
                BlurBuffer(cmd, bloomUAVDesc4, g_aBloomUAV4, g_aBloomUAV5[1], upsampleBlendFactor, computeShaders);
                BlurBuffer(cmd, bloomUAVDesc3, g_aBloomUAV3, g_aBloomUAV4[1], upsampleBlendFactor, computeShaders);
                BlurBuffer(cmd, bloomUAVDesc2, g_aBloomUAV2, g_aBloomUAV3[1], upsampleBlendFactor, computeShaders);
                BlurBuffer(cmd, bloomUAVDesc1, g_aBloomUAV1, g_aBloomUAV2[1], upsampleBlendFactor, computeShaders);
            }
            else
            {
                //// Set the UAVs
                cmd.SetComputeTextureParam(downsampleShader, 0, Result1, g_aBloomUAV3[0]);
                cmd.SetComputeTextureParam(downsampleShader, 0, Result2, g_aBloomUAV5[0]);

                //// Each dispatch group is 8x8 threads, but each thread reads in 2x2 source texels (bilinear filter).
                cmd.Dispatch2D(downsampleShader, 0, kBloomWidth / 2, kBloomHeight / 2);

                upsampleBlendFactor = upsampleBlendFactor * 2.0f / 3.0f;

                //// Blur then upsample and blur two times
                BlurBuffer(cmd, bloomUAVDesc5, g_aBloomUAV5, g_aBloomUAV5[0], 1.0f, computeShaders);
                BlurBuffer(cmd, bloomUAVDesc3, g_aBloomUAV3, g_aBloomUAV5[1], upsampleBlendFactor, computeShaders);
                BlurBuffer(cmd, bloomUAVDesc1, g_aBloomUAV1, g_aBloomUAV3[1], upsampleBlendFactor, computeShaders);
            }

            var linearColor = settings.color.value.linear;
            float intensity = RuntimeUtilities.Exp2(settings.BloomStrength.value / 10f) - 1f;
            var shaderSettings = new Vector4(1.0f, intensity, settings.dirtIntensity.value, 1.0f);

            // Lens dirtiness
            // Keep the aspect ratio correct & center the dirt texture, we don't want it to be
            // stretched or squashed
            var dirtTexture = settings.dirtTexture.value == null
                ? RuntimeUtilities.blackTexture
                : settings.dirtTexture.value;

            var dirtRatio = (float)dirtTexture.width / (float)dirtTexture.height;
            var screenRatio = (float)context.screenWidth / (float)context.screenHeight;
            var dirtTileOffset = new Vector4(1f, 1f, 0f, 0f);

            if (dirtRatio > screenRatio)
            {
                dirtTileOffset.x = screenRatio / dirtRatio;
                dirtTileOffset.z = (1f - dirtTileOffset.x) * 0.5f;
            }
            else if (screenRatio > dirtRatio)
            {
                dirtTileOffset.y = dirtRatio / screenRatio;
                dirtTileOffset.w = (1f - dirtTileOffset.y) * 0.5f;
            }

            // Shader properties
            var uberSheet = context.uberSheet;
            uberSheet.EnableKeyword("BLOOM_COMPUTE");
            uberSheet.properties.SetVector(ShaderIDs.Bloom_DirtTileOffset, dirtTileOffset);
            uberSheet.properties.SetVector(ShaderIDs.Bloom_Settings, shaderSettings);
            uberSheet.properties.SetColor(ShaderIDs.Bloom_Color, linearColor);
            uberSheet.properties.SetTexture(ShaderIDs.Bloom_DirtTex, dirtTexture);
            cmd.SetGlobalTexture(ShaderIDs.BloomTex, g_aBloomUAV1[1]);

            // Cleanup
            // Release the temporary buffers, except g_aBloomUAV1[1] which is the final bloom buffer, will be released later
            cmd.ReleaseTemporaryRT(g_aBloomUAV1[0]);
            if (highQuality)
            {
                cmd.ReleaseTemporaryRT(g_aBloomUAV2[0]);
                cmd.ReleaseTemporaryRT(g_aBloomUAV2[1]);
            }
            cmd.ReleaseTemporaryRT(g_aBloomUAV3[0]);
            cmd.ReleaseTemporaryRT(g_aBloomUAV3[1]);
            if (highQuality)
            {
                cmd.ReleaseTemporaryRT(g_aBloomUAV4[0]);
                cmd.ReleaseTemporaryRT(g_aBloomUAV4[1]);
            }
            cmd.ReleaseTemporaryRT(g_aBloomUAV5[0]);
            cmd.ReleaseTemporaryRT(g_aBloomUAV5[1]);

            // Store the final bloom buffer here, so the uber pass will use and release it
            context.bloomBufferNameID = g_aBloomUAV1[1]; 
            cmd.EndSample("ComputeBloom");
        }

        private void BlurBuffer(CommandBuffer cmd, RenderTextureDescriptor bufferDesc, int[] buffers, int lowerResBuf, float upsampleBlendFactor, PostProcessResources.ComputeShaders computeShaders)
        {
            //// Set the shader:  upsample and blur or just blur
            var cs = buffers[0] == lowerResBuf ? computeShaders.BloomBlur : computeShaders.BloomUpsampleAndBlur;

            //// Set the shader constants
            int bufferWidth = bufferDesc.width;
            int bufferHeight = bufferDesc.height;
            cmd.SetComputeVectorParam(cs, g_inverseDimensions, new Vector4() { x = 1.0f / bufferWidth, y = 1.0f / bufferHeight });
            cmd.SetComputeFloatParam(cs, g_upsampleBlendFactor, upsampleBlendFactor);

            //// Set the input textures and output UAV
            cmd.SetComputeTextureParam(cs, 0, Result, buffers[1]);
            cmd.SetComputeTextureParam(cs, 0, InputBuf, buffers[0]);
            cmd.SetComputeTextureParam(cs, 0, HigherResBuf, buffers[0]);
            cmd.SetComputeTextureParam(cs, 0, LowerResBuf, lowerResBuf);

            //// Dispatch the compute shader with default 8x8 thread groups
            cmd.Dispatch2D(cs, 0, bufferWidth, bufferHeight);
        }
    }
}