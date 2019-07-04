using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

using UnityEngine.Rendering.PostProcessing;

using RTHandle = RTHandleSystem.RTHandle;

[ExecuteInEditMode]
public class StochasticRasterizer : RenderPipelineAsset
{
    #if UNITY_EDITOR
    [UnityEditor.MenuItem("Assets/Create/Render Pipeline/StochasticTransparencyRenderPipeline", priority = 1)]
    static void CreateStochasticRasterizer()
    {
        var instance = ScriptableObject.CreateInstance<StochasticRasterizer>();
        UnityEditor.AssetDatabase.CreateAsset(instance, "Assets/Settings/StochasticTransparencyRenderPipeline.asset");
    }
#endif

    public enum AccumulationMode
    {
        Disabled   = 0,
        Finite     = 1,
        Continuous = 2
    }

    public AccumulationMode accumulationMode;

    [Range(1, 200)] public int accumulationIterations;
    
    public Texture2D randomMask;
    
    protected override RenderPipeline CreatePipeline()
    {
        return new StochasticRasterizerInstance();
    }
}

public static class ShaderIDs
{
    public static readonly int _MSAASampleCount   = Shader.PropertyToID("_MSAASampleCount");
    public static readonly int _StochasticTexture = Shader.PropertyToID("_StochasticTexture");
    public static readonly int _AlphaMaskTexture  = Shader.PropertyToID("_AlphaMaskTexture");
}

public class StochasticRasterizerInstance : RenderPipeline
{
    private static readonly ShaderTagId m_TransmittancePassName   = new ShaderTagId("Transmittance");
    private static readonly ShaderTagId m_StochasticDepthPassName = new ShaderTagId("StochasticDepths");
    private static readonly ShaderTagId m_StochasticColorPassName = new ShaderTagId("StochasticColors");

    // MSAA
    private const int k_MSAASamples = 8;
    
    // RT 
    RTHandle m_ColorBuffer;
    RTHandle m_StochasticColorBuffer;
    RTHandle m_DepthStencilBuffer;
    RTHandle m_TransmissionBuffer;

    // Accumulation: History Buffers
    int m_HistorySourceIndex;
    int m_HistoryDestIndex;
    RTHandle[] m_HistoryBuffers;

    // Accumulation: Settings
    StochasticRasterizer.AccumulationMode m_AccumulationMode;
    int m_AccumulationIterations;

    //Stochastic Sampling
    private const int k_SampleDim  = 128;
    private const int k_Iterations = 256;
    private const int k_PatternShift = 4;

    static readonly System.Random m_Random = new System.Random();

    // Engine Materials
    private Material m_FinalPass;

    // Post Processing
    private PostProcessRenderContext m_PostProcessRenderContext;

    public StochasticRasterizerInstance()
    {
        m_PostProcessRenderContext = new PostProcessRenderContext();

        int w = Screen.width; int h = Screen.height;

        // Initial state of the RTHandle system.
        // Tells the system that we will require MSAA or not so that we can avoid wasteful render texture allocation.
        // TODO: Might want to initialize to at least the window resolution to avoid un-necessary re-alloc in the player
        RTHandles.Initialize(1, 1, true, (MSAASamples)k_MSAASamples);
        RTHandles.SetReferenceSize(w, h, (MSAASamples)k_MSAASamples);

        InitializeBuffers();

        m_FinalPass = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/StochasticTransparency/FinalPass"));
    }

    private void InitializeBuffers()
    {
        m_ColorBuffer = RTHandles.Alloc(Vector2.one, 
                                        colorFormat: SystemInfo.GetGraphicsFormat(UnityEngine.Experimental.Rendering.DefaultFormat.HDR),
                                        enableMSAA: true,
                                        name: "ColorBuffer");

        m_StochasticColorBuffer = RTHandles.Alloc(Vector2.one,
                                                  colorFormat: SystemInfo.GetGraphicsFormat(UnityEngine.Experimental.Rendering.DefaultFormat.HDR),
                                                  enableMSAA: true,
                                                  name: "StochasticColorBuffer");

        m_DepthStencilBuffer = RTHandles.Alloc(Vector2.one,
                                         depthBufferBits: DepthBits.Depth32,
                                         enableMSAA: true,
                                         name: "DepthStencilBuffer");

        m_TransmissionBuffer = RTHandles.Alloc(Vector2.one,
                                          colorFormat: UnityEngine.Experimental.Rendering.GraphicsFormat.R16_SFloat,
                                          enableMSAA: true,
                                          name: "TransmissionBuffer"); // NOTE: No MSAA on opacity for now.

        // Alloc History Buffers
        m_HistoryBuffers = new RTHandle[2];
        for(int i = 0; i < m_HistoryBuffers.Length; ++i)
        {
            m_HistoryBuffers[i] = RTHandles.Alloc(Vector2.one,
                                                  colorFormat: SystemInfo.GetGraphicsFormat(UnityEngine.Experimental.Rendering.DefaultFormat.HDR),
                                                  enableMSAA: false,
                                                  name: "HistoryBuffer" + i);
        }
    }

    protected override void Render(ScriptableRenderContext context, Camera[] cameras)
    {
        BeginFrameRendering(cameras);

        foreach (Camera camera in cameras)
        {
            BeginCameraRendering(camera);
            
            //Culling
            ScriptableCullingParameters cullingParams;
            if (!camera.TryGetCullingParameters(out cullingParams))
                continue;
            CullingResults cull = context.Cull(ref cullingParams);

            //Camera setup some builtin variables e.g. camera projection matrices etc
            context.SetupCameraProperties(camera);
            camera.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;
            
            //Setup sort, filter, draw settings
            var sortingSettings = new SortingSettings(camera);

            var drawSettings = new DrawingSettings(m_StochasticColorPassName, sortingSettings);
            drawSettings.perObjectData |= PerObjectData.MotionVectors;

            var filterSettings = new FilteringSettings(RenderQueueRange.all);
            filterSettings.excludeMotionVectorObjects = false;

            InitializeFrameSettings();

            // Render background
            DrawSkybox(camera, context);

            // Push constants
            PushShadingConstants(camera, context, 0);

            //------------------------------------------------------------------------------------
            // STOCHASTIC TRANSPARENCY IMPLEMENTATION
            //------------------------------------------------------------------------------------
            {
                //1.) Transmission Pass
                RenderTransmission(sortingSettings, drawSettings, filterSettings, cull, context);

                //2.) Stochastic Depths
                RenderStochasticDepths(sortingSettings, drawSettings, filterSettings, cull, context);

                //3.) Stochastic Colors
                RenderStochasticColors(sortingSettings, drawSettings, filterSettings, cull, context);
            }
            //------------------------------------------------------------------------------------
            
            CommandBuffer cmd = CommandBufferPool.Get("FinalPass");

            // Post Process
            var postProcessLayer = camera.GetComponent<PostProcessLayer>();
            if (postProcessLayer != null)
            {
                RenderPostProcess(postProcessLayer, cmd, camera);
            }

            // Flip to back buffer
            cmd.Blit(m_ColorBuffer, BuiltinRenderTextureType.CameraTarget);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            /*
#if true
            int iterations = Application.isPlaying && m_AccumulationMode == StochasticRasterizer.AccumulationMode.Finite ? m_AccumulationIterations : 1;
            for(int i = 0; i < iterations; ++i)
            { 
                //Clear
                ClearBuffers(context);

                //Total transmittance
                RenderTransmittance(sortingSettings, drawSettings, filterSettings, cull, context);

                //Sky
                DrawSkybox(camera, context);

                //Shader Inputs
                PushShadingConstants(camera, context, i);

                //Stochastic
                RenderStochasticTransparency(sortingSettings, drawSettings, filterSettings, cull, context);

                //Final Pass    
                RenderFinalPass(context, i, camera);
            }
            PresentAccumulation(context);
#endif      */

            context.Submit();
        }
    }

    private void InitializeFrameSettings()
    {
        var stochasticRasterizer = GraphicsSettings.renderPipelineAsset as StochasticRasterizer;
        m_AccumulationMode = stochasticRasterizer.accumulationMode;
        m_AccumulationIterations = stochasticRasterizer.accumulationIterations;
    }

    private void ClearBuffers(ScriptableRenderContext context)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Clear");
        
        cmd.SetRenderTarget(m_TransmissionBuffer);
        cmd.ClearRenderTarget(true, true, Color.white);

        cmd.SetRenderTarget(m_ColorBuffer);
        cmd.ClearRenderTarget(true, true, Color.clear);

        cmd.SetRenderTarget(m_DepthStencilBuffer);
        cmd.ClearRenderTarget(true, false, Color.black);

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    private Texture2D GetBlueNoise(int index)
    {
        // NOTE: On the first frame, the render context will not have been set up by the post layer yet.
        //       We need this here for first frame.
        //       Requires post process layer on current camera.
        if (m_PostProcessRenderContext.resources != null)
        {
            // We use post-processing stack's blue noise list for now.
            var blueNoise = m_PostProcessRenderContext.resources.blueNoise64;
            Assert.IsTrue(blueNoise != null && blueNoise.Length > 0);

            return blueNoise[index];
        }

        return Texture2D.whiteTexture; 
    }

    private void PresentAccumulation(ScriptableRenderContext context)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Present Accumulation");

        if(!Application.isPlaying || m_AccumulationMode == StochasticRasterizer.AccumulationMode.Disabled)
        {
            cmd.Blit(m_ColorBuffer, BuiltinRenderTextureType.CameraTarget);
        }
        else
        { 
            cmd.Blit(m_HistoryBuffers[m_HistoryDestIndex], BuiltinRenderTextureType.CameraTarget);
        
            // Clear history in case of finite accumulation
            if(m_AccumulationMode == StochasticRasterizer.AccumulationMode.Finite)
            { 
                for(int i = 0; i < 2; ++i)
                {
                    cmd.SetRenderTarget(m_HistoryBuffers[i]);
                    cmd.ClearRenderTarget(false, true, Color.black);
                }
            }
        }

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    private void DrawSkybox(Camera camera, ScriptableRenderContext context)
    {
        CommandBuffer cmd = new CommandBuffer() { name = "Draw Sky" };
        cmd.SetRenderTarget(m_ColorBuffer, m_DepthStencilBuffer);
        context.ExecuteCommandBuffer(cmd);
        cmd.Release();
            
        //Skybox
        if (camera.clearFlags == CameraClearFlags.Skybox)  {  context.DrawSkybox(camera);  }
    }

    private void PushShadingConstants(Camera camera, ScriptableRenderContext context, int iteration)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Push");

        cmd.SetGlobalInt(ShaderIDs._MSAASampleCount, k_MSAASamples);

        if(m_AccumulationMode == StochasticRasterizer.AccumulationMode.Continuous)
        {
            cmd.SetGlobalTexture(ShaderIDs._StochasticTexture, GetBlueNoise(Time.renderedFrameCount % 64));
            cmd.SetGlobalFloat("_Jitter", Time.time);
        }
        else if (m_AccumulationMode == StochasticRasterizer.AccumulationMode.Finite)
        {
            //TODO: Blue Noise
            //cmd.SetGlobalTexture(ShaderIDs._StochasticTexture, GetBlueNoise());
            //cmd.SetGlobalFloat("_SubframeIndex", iteration % k_PatternShift);

            cmd.SetGlobalTexture(ShaderIDs._StochasticTexture, GetBlueNoise(iteration % 64));
            cmd.SetGlobalFloat("_Jitter", iteration * 100);
        }
        else
        {
            cmd.SetGlobalTexture(ShaderIDs._StochasticTexture, GetBlueNoise(Time.renderedFrameCount % 64));
        }

        cmd.SetGlobalVector("_BlueNoiseParams", new Vector4((float)camera.pixelWidth / (float)64f, (float)camera.pixelHeight / (float)64f, 
                                                            (float)m_Random.NextDouble(), (float)m_Random.NextDouble()));

        var stochasticRasterizer = GraphicsSettings.renderPipelineAsset as StochasticRasterizer;
        if (stochasticRasterizer != null)
        {
            if(stochasticRasterizer.randomMask != null)
            {
                cmd.SetGlobalTexture("_Randoms", stochasticRasterizer.randomMask);
            }
            else
            {
                cmd.SetGlobalTexture("_Randoms", Texture2D.whiteTexture);
            }
        }

        context.ExecuteCommandBuffer(cmd);
        cmd.Clear();
    }

    private void RenderTransmission(SortingSettings sortingSettings, DrawingSettings drawSettings, FilteringSettings filterSettings,
                               CullingResults cull, ScriptableRenderContext context)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Transmission");

        //Set Alpha RT
        cmd.SetRenderTarget(m_TransmissionBuffer);
        cmd.ClearRenderTarget(false, true, Color.white);

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        //Opaque objects
        sortingSettings.criteria = SortingCriteria.CommonOpaque;
        drawSettings.sortingSettings = sortingSettings;
        drawSettings.SetShaderPassName(0, m_TransmittancePassName);
        filterSettings.renderQueueRange = RenderQueueRange.opaque;
        context.DrawRenderers(cull, ref drawSettings, ref filterSettings);
    }

    private void RenderStochasticDepths(SortingSettings sortingSettings, DrawingSettings drawSettings, FilteringSettings filterSettings,
                                         CullingResults cull, ScriptableRenderContext context)
    {
        CommandBuffer cmd = CommandBufferPool.Get("StochasticDepths");

        //Set Depth RT + Clear
        cmd.SetRenderTarget(m_DepthStencilBuffer);
        cmd.ClearRenderTarget(true, false, Color.clear);
        cmd.SetGlobalInt(ShaderIDs._MSAASampleCount, k_MSAASamples);

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        //Opaque objects
        sortingSettings.criteria = SortingCriteria.CommonOpaque;
        drawSettings.sortingSettings = sortingSettings;
        drawSettings.SetShaderPassName(0, m_StochasticDepthPassName);
        filterSettings.renderQueueRange = RenderQueueRange.opaque;
        context.DrawRenderers(cull, ref drawSettings, ref filterSettings);
    }

    private void RenderStochasticColors(SortingSettings sortingSettings, DrawingSettings drawSettings, FilteringSettings filterSettings,
                                     CullingResults cull, ScriptableRenderContext context)
    {
        CommandBuffer cmd = CommandBufferPool.Get("StochasticColors");

        //Set Depth RT + Clear
        cmd.SetRenderTarget(m_StochasticColorBuffer, m_DepthStencilBuffer);
        cmd.ClearRenderTarget(false, true, Color.black);
        cmd.SetGlobalInt(ShaderIDs._MSAASampleCount, k_MSAASamples);

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        //Opaque objects
        sortingSettings.criteria = SortingCriteria.CommonOpaque;
        drawSettings.sortingSettings = sortingSettings;
        drawSettings.SetShaderPassName(0, m_StochasticColorPassName);
        filterSettings.renderQueueRange = RenderQueueRange.opaque;
        context.DrawRenderers(cull, ref drawSettings, ref filterSettings);
    }

    private void RenderStochasticTransparency(SortingSettings sortingSettings, DrawingSettings drawSettings, FilteringSettings filterSettings,
                                         CullingResults cull, ScriptableRenderContext context)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Stochastic");

        //Set Color RT
        cmd.SetRenderTarget(m_ColorBuffer, m_DepthStencilBuffer);
        cmd.SetGlobalInt(ShaderIDs._MSAASampleCount, k_MSAASamples);

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        //Opaque objects
        sortingSettings.criteria = SortingCriteria.CommonOpaque;
        drawSettings.sortingSettings = sortingSettings;
        drawSettings.SetShaderPassName(0, m_StochasticColorPassName);
        filterSettings.renderQueueRange = RenderQueueRange.opaque;          
        context.DrawRenderers(cull, ref drawSettings, ref filterSettings);
    }

    private void RenderFinalPass(ScriptableRenderContext context, int iteration, Camera camera)
    {
        CommandBuffer cmd = CommandBufferPool.Get("FinalPass");

        if(Application.isPlaying)
        { 
            // Ping-pong indices
            if(m_AccumulationMode == StochasticRasterizer.AccumulationMode.Continuous)
            {
                m_HistorySourceIndex = (Time.frameCount + 0) % 2;
                m_HistoryDestIndex   = (Time.frameCount + 1) % 2;
                
                cmd.SetGlobalFloat("_AccumulationWeight", 0.99f);
            }
            else
            {
                m_HistorySourceIndex = (iteration + 0) % 2;
                m_HistoryDestIndex   = (iteration + 1) % 2;

                // TODO: Note
                //if(iteration == 0)
                //    cmd.SetGlobalFloat("_AccumulationWeight", 0f);
                //else
                    cmd.SetGlobalFloat("_AccumulationWeight", 1f - (1f / (float)m_AccumulationIterations));
            }

            // Post Process
            var postProcessLayer = camera.GetComponent<PostProcessLayer>();
            if(postProcessLayer != null)
            {
                RenderPostProcess(postProcessLayer, cmd, camera);
            }

            cmd.SetGlobalTexture("_ColorBuffer",   m_ColorBuffer); 
            cmd.SetGlobalTexture("_HistoryBuffer", m_HistoryBuffers[m_HistorySourceIndex]);
            CoreUtils.DrawFullScreen(cmd, m_FinalPass, m_HistoryBuffers[m_HistoryDestIndex], m_ColorBuffer);
        }
        else
        {
            //TODO: Final Pass here still needs opacity buffer for final resolve.
            // Post Process
            var postProcessLayer = camera.GetComponent<PostProcessLayer>();
            if(postProcessLayer != null)
            {
                RenderPostProcess(postProcessLayer, cmd, camera);
            }
            else
            {
                cmd.Blit(m_ColorBuffer, BuiltinRenderTextureType.CameraTarget);
            }
        }

        
        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    private void RenderPostProcess(PostProcessLayer layer, CommandBuffer cmd, Camera camera)
    {
        var context = m_PostProcessRenderContext;
        context.Reset();
        context.source = m_ColorBuffer;
        context.destination = m_ColorBuffer;
        context.command = cmd;
        context.camera = camera;
        context.sourceFormat = RenderTextureFormat.ARGBHalf;
        context.flip = false;
#if !UNITY_2019_1_OR_NEWER // Y-flip correction available in 2019.1
        context.flip = context.flip && (!hdcamera.camera.stereoEnabled);
#endif

        layer.Render(context);
    }

    protected override void Dispose(bool disposing)
    {
        RTHandles.Release(m_ColorBuffer);
        RTHandles.Release(m_StochasticColorBuffer);
        RTHandles.Release(m_DepthStencilBuffer);
        RTHandles.Release(m_TransmissionBuffer);
        for (int i = 0; i < m_HistoryBuffers.Length; ++i)
            RTHandles.Release(m_HistoryBuffers[i]);

        CoreUtils.Destroy(m_FinalPass);
    }
}