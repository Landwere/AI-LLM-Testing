using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[System.Serializable]
public class CustomPostProcessPass : ScriptableRenderPass
{
    private Material m_bloomMaterial;
    private Material m_compositeMaterial;

    const int k_MaxPyramidSize = 16;
    private int[] _BloomMipUp;
    private int[] _BloomMipDown;
    private RTHandle[] m_BloomMipUp;
    private RTHandle[] m_BloomMipDown;
    private GraphicsFormat hdrFormat;

    private RenderTextureDescriptor m_Descriptor;
    private RTHandle m_CameraColourTarget;
    private RTHandle m_CameraDepthTarget;
    private BloomEffectComponent m_BloomEffect;
    public CustomPostProcessPass(Material bloomMaterial, Material compositeMaterial)
    {
        m_bloomMaterial = bloomMaterial;
        m_compositeMaterial = compositeMaterial;

        renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;

        _BloomMipUp = new int[k_MaxPyramidSize];
        _BloomMipDown = new int[k_MaxPyramidSize];
        m_BloomMipUp = new RTHandle[k_MaxPyramidSize];
        m_BloomMipDown = new RTHandle[k_MaxPyramidSize];

        for (int i = 0; i < k_MaxPyramidSize; i++)
        {
            _BloomMipUp[i] = Shader.PropertyToID("_BloomMipUp" + i);
            _BloomMipDown[i] = Shader.PropertyToID("_BloomMipDown" + i);
            //get name will get allocated descriptor later
            m_BloomMipUp[i] = RTHandles.Alloc(_BloomMipUp[i], name: "_BloomMipUp" + i);
            m_BloomMipDown[i] = RTHandles.Alloc(m_BloomMipDown[i], name: "_BloomMipDown" + i);

        }

        const FormatUsage usage = FormatUsage.Linear | FormatUsage.Render;
        if (SystemInfo.IsFormatSupported(GraphicsFormat.B10G11R11_UFloatPack32, usage)) //HDR fallback
        {
            hdrFormat = GraphicsFormat.B10G11R11_UFloatPack32;
        }
        else
        {
            hdrFormat = QualitySettings.activeColorSpace == ColorSpace.Linear
                ? GraphicsFormat.R8G8B8A8_SRGB
                : GraphicsFormat.R8G8B8A8_UNorm;
        }
    }

    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
    {
        m_Descriptor = renderingData.cameraData.cameraTargetDescriptor;
    }

    public void SetTarget(RTHandle cameraColourTargetHandle, RTHandle cameraDepthTargetHandle)
    {
        m_CameraColourTarget = cameraColourTargetHandle;
        m_CameraDepthTarget = cameraDepthTargetHandle;
    }

    //internal void SetTarget(RTHandle cameraColourTargetHandle, RTHandle cameraDepthTargetHandle)
    //{
    //    m_CameraColourTarget = cameraColourTargetHandle;
    //    m_CameraDepthTarget = cameraDepthTargetHandle;
    //}


    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (renderingData.cameraData.cameraType == CameraType.Game)
        {
            VolumeStack stack = VolumeManager.instance.stack;
            m_BloomEffect = stack.GetComponent<BloomEffectComponent>();
            CommandBuffer cmd = CommandBufferPool.Get();

            using (new ProfilingScope(cmd, new ProfilingSampler("Custom Post Process Effects")))
            {
                //Do the bloom pass here first
                SetupBloom(cmd, m_CameraColourTarget);

                //setup composite values
                m_compositeMaterial.SetFloat("_Cutoff", m_BloomEffect.x.value);
                m_compositeMaterial.SetFloat("_BloomIntensity", m_BloomEffect.intensity.value);
                m_compositeMaterial.SetFloat("_Density", m_BloomEffect.density.value);

                Blitter.BlitCameraTexture(cmd, m_CameraColourTarget, m_CameraColourTarget, m_compositeMaterial, 0);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            CommandBufferPool.Release(cmd);
        }
    }

    //public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    //{
    //     base.RecordRenderGraph(renderGraph, frameData);
    //    //VolumeStack stack = VolumeManager.instance.stack;
    //    //m_BloomEffect = stack.GetComponent<BloomEffectComponent>();
    //    //CommandBuffer cmd = CommandBufferPool.Get();

    //    //using (new ProfilingScope(cmd, new ProfilingSampler("Custom Post Process Effects")))
    //    //{
    //    //    //Do the bloom pass here first
    //    //    SetupBloom(cmd, m_CameraColourTarget);

    //    //    //setup composite values
    //    //    m_compositeMaterial.SetFloat("_Cutoff", m_BloomEffect.x.value);

    //    //    Blitter.BlitCameraTexture(cmd, m_CameraColourTarget, m_CameraColourTarget, m_compositeMaterial, 0);
    //    //}

    //    //cmd.Clear();

    //    //CommandBufferPool.Release(cmd);
    //}


    private void SetupBloom(CommandBuffer cmd, RTHandle source)
    {
        //start at half-res
        int downres = 1;
        int tw = m_Descriptor.width >> downres;
        int th = m_Descriptor.height >> downres;

        //determine the iteration count
        int maxSize = Mathf.Max(tw, th);
        int iterations = Mathf.FloorToInt(Mathf.Log(maxSize, 2f) - 1);
        int mipCount = Mathf.Clamp(iterations, 1, m_BloomEffect.maxIterations.value);

        //pre filtering parameters
        float clamp = m_BloomEffect.clamp.value;
        float threshold = Mathf.GammaToLinearSpace(m_BloomEffect.threshold.value);
        float threshholdKnee = threshold * 0.5f; //Hardcoded soft knee

        //material setup
        float scatter = Mathf.Lerp(0.05f, 0.95f, m_BloomEffect.scatter.value);
        var bloomMaterial = m_bloomMaterial;

        bloomMaterial.SetVector("_Params", new Vector4(scatter, clamp, threshold, threshholdKnee));

        //Prefilter
        var desc = GetCompatibleDescriptor(tw, th, hdrFormat);
        for (int i = 0; i < mipCount; i++)
        {
            RenderingUtils.ReAllocateIfNeeded(ref m_BloomMipUp[i], desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: m_BloomMipUp[i].name);
            RenderingUtils.ReAllocateIfNeeded(ref m_BloomMipDown[i], desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: m_BloomMipDown[i].name);
            desc.width = Mathf.Max(1, desc.width >> 1);
            desc.height = Mathf.Max(1, desc.height >> 1);

        }
        Blitter.BlitCameraTexture(cmd, source, m_BloomMipDown[0], RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, bloomMaterial, 0);


        //Downsample - gaussian pyramid
        var lastDown = m_BloomMipDown[0];
        for (int i = 1; i < mipCount; i++)
        {
            //classic two pass gaussian blur - use mipUp as a temporary target
            //first pass does 2x downsampling + 9-tap gaussian
            //second pass does 9-tap gaussian using a 5-tap filter + bilinear filtering
            Blitter.BlitCameraTexture(cmd, lastDown, m_BloomMipUp[i], RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, bloomMaterial, 1);
            Blitter.BlitCameraTexture(cmd, m_BloomMipUp[i], m_BloomMipDown[i], RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, bloomMaterial, 2);

            lastDown = m_BloomMipDown[i];
        }

        //upsample (bilinear by defualt, HQ filtering does bicubic instead)
        for (int i = mipCount - 2; i >= 0; i--)
        {
            var lowMip = (i == mipCount - 2) ? m_BloomMipDown[i + 1] : m_BloomMipUp[i + 1];
            var highMip = m_BloomMipDown[i];
            var dst = m_BloomMipUp[i];

            cmd.SetGlobalTexture("_SourceTexLowMip", lowMip);
            Blitter.BlitCameraTexture(cmd, highMip, dst, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, bloomMaterial, 3);
        }

        cmd.SetGlobalTexture("_Bloom_Texture", m_BloomMipUp[0]);
        cmd.SetGlobalFloat("_BloomIntensity", m_BloomEffect.intensity.value);
    }

    RenderTextureDescriptor GetCompatibleDescriptor() => GetCompatibleDescriptor(m_Descriptor.width, m_Descriptor.height, m_Descriptor.graphicsFormat);

    RenderTextureDescriptor GetCompatibleDescriptor(int width, int height, GraphicsFormat format, DepthBits depthBufferBits = DepthBits.None)
        => GetCompatibleDescriptor(m_Descriptor, width, height, format, depthBufferBits);

    internal static RenderTextureDescriptor GetCompatibleDescriptor(RenderTextureDescriptor desc, int width, int height, GraphicsFormat format, DepthBits depthBufferBits = DepthBits.None)
    {
        desc.depthBufferBits = (int)depthBufferBits;
        desc.msaaSamples = 1;
        desc.width = width;
        desc.height = height;
        desc.graphicsFormat = format;
        return desc;
    }

 

    //public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    //{
    //    string passName = "Copy To Debug Texture";

    //    // Add a raster render pass to the render graph. The PassData type parameter determines
    //    // the type of the passData output variable.
    //    using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName,
    //        out var passData))
    //    {
    //        // UniversalResourceData contains all the texture references used by URP,
    //        // including the active color and depth textures of the camera.
    //        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

    //        // Populate passData with the data needed by the rendering function
    //        // of the render pass.
    //        // Use the camera's active color texture
    //        // as the source texture for the copy operation.
    //        passData.copySourceTexture = resourceData.activeColorTexture;

    //        // Create a destination texture for the copy operation based on the settings,
    //        // such as dimensions, of the textures that the camera uses.
    //        // Set msaaSamples to 1 to get a non-multisampled destination texture.
    //        // Set depthBufferBits to 0 to ensure that the CreateRenderGraphTexture method
    //        // creates a color texture and not a depth texture.
    //        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
    //        RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
    //        desc.msaaSamples = 1;
    //        desc.depthBufferBits = 0;

    //        // For demonstrative purposes, this sample creates a temporary destination texture.
    //        // UniversalRenderer.CreateRenderGraphTexture is a helper method
    //        // that calls the RenderGraph.CreateTexture method.
    //        // Using a RenderTextureDescriptor instance instead of a TextureDesc instance
    //        // simplifies your code.
    //        TextureHandle destination =
    //            UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc,
    //                "CopyTexture", false);

    //        // Declare that this render pass uses the source texture as a read-only input.
    //        builder.UseTexture(passData.copySourceTexture);

    //        // Declare that this render pass uses the temporary destination texture
    //        // as its color render target.
    //        // This is similar to cmd.SetRenderTarget prior to the RenderGraph API.
    //        builder.SetRenderAttachment(destination, 0);

    //        // RenderGraph automatically determines that it can remove this render pass
    //        // because its results, which are stored in the temporary destination texture,
    //        // are not used by other passes.
    //        // For demonstrative purposes, this sample turns off this behavior to make sure
    //        // that render graph executes the render pass. 
    //        builder.AllowPassCulling(false);

    //        // Set the ExecutePass method as the rendering function that render graph calls
    //        // for the render pass. 
    //        // This sample uses a lambda expression to avoid memory allocations.
    //        builder.SetRenderFunc((PassData data, RasterGraphContext context)
    //            => ExecutePass(data, context));
    //    }
    //    static void ExecutePass(PassData data, RasterGraphContext context)
    //    {
    //        // Records a rendering command to copy, or blit, the contents of the source texture
    //        // to the color render target of the render pass.
    //        // The RecordRenderGraph method sets the destination texture as the render target
    //        // with the UseTextureFragment method.
    //        Blitter.BlitTexture(context.cmd, data.copySourceTexture,
    //            new Vector4(1, 1, 0, 0), 0, false);
    //    }
    //}

}