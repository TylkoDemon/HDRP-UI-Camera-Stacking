#if USING_HDRP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.HighDefinition;
using System;

/// <summary>
/// When this component is added to a camera, it replaces the standard rendering by a single optimized pass to render the GUI in screen space.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(Camera))]
public class HDCameraUI : MonoBehaviour
{
    /// <summary>
    /// Specifies the compositing mode to use when combining the UI render texture and the camera color.
    /// </summary>
    public enum CompositingMode
    {
        /// <summary>Automatically combines both UI and camera color after the rendering of the main camera.</summary>
        Automatic,
        /// <summary>Disables the automatic compositing so you have to manually composite the UI buffer with the camera color.</summary>
        Manual,
        /// <summary>Automatically combines both UI and camera color using a custom material (compositingMaterial field). The material must be compatible with the Blit() command.</summary>
        Custom,
    }

    /// <summary>
    /// Specifies on which camera the UI needs to be rendered.
    /// </summary>
    public enum TargetCamera
    {
        /// <summary>Only render the UI on the camera with the tag "Main Camera".</summary>
        Main,
        /// <summary>Render the UI on all cameras.</summary>
        All,
        /// <summary>Render the UI on all cameras in a specific layer.</summary>
        Layer,
        /// <summary>Only render the UI on a specific camera.</summary>
        Specific,
    }

    /// <summary>
    /// Select which layer mask to use to render the UI.
    /// </summary>
    [Tooltip("Select which layer mask to use to render the UI.")]
    public LayerMask uiLayerMask = 1 << 5;

    /// <summary>
    /// Select in which order the UI cameras are composited, higher priority will be executed before.
    /// </summary>
    [Tooltip("Select in which order the UI cameras are composited, higher priority will be executed before.")]
    public float priority = 0;

    /// <summary>
    /// Specifies the compositing mode to use when combining the UI render texture and the camera color.
    /// </summary>
    public CompositingMode compositingMode;

    /// <summary>
    /// Use this property to apply a post process shader effect on the UI. The shader must be compatible with Graphics.Blit().
    /// see https://github.com/h33p/Unity-Graphics-Demo/blob/master/Assets/Asset%20Store/PostProcessing/Resources/Shaders/Blit.shader
    /// </summary>
    [Tooltip("Apply a post process effect on the UI buffer. The shader must be compatible with Graphics.Blit().")]
    public Material compositingMaterial;

    /// <summary>
    /// Apply post processes to the UI. Use the camera volume layer mask to control which post process are applied.
    /// </summary>
    // public bool postProcess;

    /// <summary>
    /// The pass name of the compositing material to use.
    /// </summary>
    public int compositingMaterialPass;

    public bool forImgui;
    
    [HideInInspector]
    RenderTexture internalRenderTexture;

    /// <summary>
    /// The render texture used to render the UI. This field can reflect the camera target texture if not null.
    /// </summary>
    public RenderTexture renderTexture
    {
        get => attachedCamera.targetTexture == null ? internalRenderTexture : attachedCamera.targetTexture;
        set => attachedCamera.targetTexture = value;
    }

    /// <summary>
    /// Specifies the graphics format to use when rendering the UI.
    /// </summary>
    [Tooltip("Specifies the graphics format to use when rendering the UI.")]
    public GraphicsFormat graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;

    /// <summary>
    /// Copy the UI after rendering in the camera buffer. Useful if you need to use the target BuiltinRenderTextureType.CameraTarget in C#.
    /// </summary>
    public bool renderInCameraBuffer;

    /// <summary>
    /// Specifies on which camera the UI needs to be rendered. The default is Main Camera only.
    /// </summary>
    public TargetCamera targetCamera = TargetCamera.Main;

    /// <summary>
    /// Specifies which layer target camera(s) are using. All cameras using this layer will have the same UI applied.
    /// </summary>
    public LayerMask targetCameraLayer = 1;

    /// <summary>
    /// Specifies the camera where the UI should be rendered.
    /// </summary>
    public Camera targetCameraObject;

    /// <summary>
    /// Event triggered just before the rendering of the UI (after the culling)
    /// </summary>
    public event Action beforeUIRendering;

    /// <summary>
    /// Event triggered just after the rendering of the UI.
    /// </summary>
    public event Action afterUIRendering;

    CullingResults cullingResults;
    [SerializeField]
    internal bool showAdvancedSettings;
    [SerializeField]
    Shader blitWithBlending; // Force the serialization of the shader in the scene so it ends up in the build

    internal Camera attachedCamera;
    HDAdditionalCameraData data;
    ShaderTagId[] hdTransparentPassNames;
    ProfilingSampler cullingSampler;
    ProfilingSampler renderingSampler;
    ProfilingSampler uiCameraStackingSampler;
    ProfilingSampler copyToCameraTargetSampler;

    // Start is called before the first frame update
    void OnEnable()
    {
        data = GetComponent<HDAdditionalCameraData>();
        attachedCamera = GetComponent<Camera>();

        if (data == null)
            return;

        data.customRender -= DoRenderUI;
        data.customRender += DoRenderUI;

        hdTransparentPassNames = new ShaderTagId[]
        {
            HDShaderPassNames.s_TransparentBackfaceName,
            HDShaderPassNames.s_ForwardOnlyName,
            HDShaderPassNames.s_ForwardName,
            HDShaderPassNames.s_SRPDefaultUnlitName
        };

        // TODO: Add VR support
        internalRenderTexture = new RenderTexture(1, 1, 0, graphicsFormat, 1);
        internalRenderTexture.dimension = TextureDimension.Tex2DArray;
        internalRenderTexture.volumeDepth = 1;
        internalRenderTexture.depth = 24;
        internalRenderTexture.name = "HDCameraUI Output Target";

        cullingSampler = new ProfilingSampler("UI Culling");
        renderingSampler = new ProfilingSampler("UI Rendering");
        uiCameraStackingSampler = new ProfilingSampler("Render UI Camera Stacking");
        copyToCameraTargetSampler = new ProfilingSampler("Copy To Camera Target");

        if (blitWithBlending == null)
            blitWithBlending = Shader.Find("Hidden/HDRP/UI_Compositing");

        CameraStackingCompositing.uiList.Add(this);
    }

    void OnDisable()
    {
        if (data == null)
            return;

        data.customRender -= DoRenderUI;
        CameraStackingCompositing.uiList.Remove(this);
    }

    void UpdateRenderTexture(Camera camera)
    {
        if (camera.pixelWidth != internalRenderTexture.width
            || camera.pixelHeight != internalRenderTexture.height
            || internalRenderTexture.graphicsFormat != graphicsFormat)
        {
            internalRenderTexture.Release();
            internalRenderTexture.width = Mathf.Max(4, camera.pixelWidth);
            internalRenderTexture.height = Mathf.Max(4, camera.pixelHeight);
            internalRenderTexture.graphicsFormat = graphicsFormat;
            internalRenderTexture.Create();
        }
    }

    void CullUI(CommandBuffer cmd, ScriptableRenderContext ctx, Camera camera)
    {
        using (new ProfilingScope(cmd, cullingSampler))
        {
            camera.TryGetCullingParameters(out var cullingParameters);
            cullingParameters.cullingOptions = CullingOptions.None;
            cullingParameters.cullingMask = (uint)uiLayerMask.value;
            cullingResults = ctx.Cull(ref cullingParameters);
        }
    }

    void RenderUI(CommandBuffer cmd, ScriptableRenderContext ctx, Camera camera, RenderTexture targetTexture)
    {
        beforeUIRendering?.Invoke();

        using (new ProfilingScope(cmd, renderingSampler))
        {
            CoreUtils.SetRenderTarget(cmd, targetTexture.colorBuffer, targetTexture.depthBuffer, ClearFlag.All);

            var drawSettings = new DrawingSettings
            {
                sortingSettings = new SortingSettings(camera) { criteria = SortingCriteria.CommonTransparent | SortingCriteria.CanvasOrder | SortingCriteria.RendererPriority }
            };
            for (int i = 0; i < hdTransparentPassNames.Length; i++)
                drawSettings.SetShaderPassName(i, hdTransparentPassNames[i]);

            var filterSettings = new FilteringSettings(RenderQueueRange.all, uiLayerMask);

            ctx.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            ctx.DrawRenderers(cullingResults, ref drawSettings, ref filterSettings);
        }

        afterUIRendering?.Invoke();
    }

    void DoRenderUI(ScriptableRenderContext ctx, HDCamera hdCamera)
    {
        if (!hdCamera.camera.enabled)
            return;

        try
        {
            var hdrp = RenderPipelineManager.currentPipeline as HDRenderPipeline;
            if (hdrp == null)
                return;

            if (hdCamera.xr.enabled)
                Debug.LogError("XR is not supported by HDCameraUI the component.");

            // Update the internal render texture only if we use it
            if (hdCamera.camera.targetTexture == null)
                UpdateRenderTexture(hdCamera.camera);

            var cmd = CommandBufferPool.Get();

            // Setup render context for rendering GUI
            ScriptableRenderContext.EmitGeometryForCamera(hdCamera.camera);
            ctx.SetupCameraProperties(hdCamera.camera, hdCamera.xr.enabled);

            // Setup HDRP camera properties to render HDRP shaders
            hdrp.UpdateCameraCBuffer(cmd, hdCamera);

            using (new ProfilingScope(cmd, uiCameraStackingSampler))
            {
                CullUI(cmd, ctx, hdCamera.camera);
                RenderUI(cmd, ctx, hdCamera.camera, renderTexture);

                if (renderInCameraBuffer && hdCamera.camera.targetTexture == null)
                {
                    using (new ProfilingScope(cmd, copyToCameraTargetSampler))
                        cmd.Blit(renderTexture, BuiltinRenderTextureType.CameraTarget, 0, 0);
                }
            }

            ctx.ExecuteCommandBuffer(cmd);
            ctx.Submit();

            if (forImgui)
                OnAfterUIRendering?.Invoke(ctx);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, this);
        }
    }

    internal bool IsActive() => isActiveAndEnabled && attachedCamera.isActiveAndEnabled;

    public static event Action<ScriptableRenderContext> OnAfterUIRendering;
}
#else
using UnityEngine;

[RequireComponent(typeof(Camera))]
public class HDCameraUI : MonoBehaviour
{
}
#endif