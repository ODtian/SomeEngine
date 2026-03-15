using System.Numerics;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Level 2: 预设管线 — 组合 Level 1 Stage 实现常见渲染配置。
/// 提供 Opaque / OpaqueAndTransparent 工厂方法。
/// </summary>
public class ClusterPipeline : IRenderFeature
{
    public string Name { get; }

    private readonly ClusterUploadStage _uploadStage;
    private readonly ClusterTraverseStage _traverseStage;
    private readonly ClusterCullStage _cullStage;
    private readonly ClusterRasterBinStage _rasterBinStage;
    private readonly ClusterRasterBinStage _rasterBinStageP2;
    private readonly ClusterDrawStage _drawStageP1;
    private readonly ClusterDrawStage? _drawStageP2;
    private readonly ClusterDrawStage? _drawStageTransparent;
    private readonly ClusterShadeBinStage _shadeBinStage;
    private readonly ClusterShadeStage _shadeStage;

    private readonly RenderContext _context;
    private readonly ClusterResourceManager _clusterMgr;
    private readonly InstanceDataManager _instanceMgr;
    private readonly MaterialRegistry _registry;

    // Internal passes owned by Pipeline
    private readonly ClusterStreamer _clusterStreamer;
    internal ClusterDebugReadbackPass? _debugReadbackPass;

    // ─── Configuration ───
    public HiZDebugMode HiZMode { get; set; } = HiZDebugMode.Full2Phase;
    public ClusterDebugMode DebugMode { get; set; } = ClusterDebugMode.None;
    public bool WireframeEnabled { get; set; }
    public bool OverdrawEnabled { get; set; }
    public bool DebugSpheresEnabled { get; set; }
    public bool UseVisBuffer { get; set; } = true;
    public bool BypassCulling { get; set; }
    public bool DumpNextFrame { get; set; }
    public bool DebugShowHiZAABBs { get; set; }
    public bool IncludeTransparentPass { get; set; }

    // ─── Debug toggles (convenience) ───
    public bool DebugClusterID
    {
        get => DebugMode == ClusterDebugMode.ClusterID;
        set
        {
            if (value) DebugMode = ClusterDebugMode.ClusterID;
            else if (DebugMode == ClusterDebugMode.ClusterID) DebugMode = ClusterDebugMode.None;
        }
    }

    public bool DebugLOD
    {
        get => DebugMode == ClusterDebugMode.LODLevel;
        set
        {
            if (value) DebugMode = ClusterDebugMode.LODLevel;
            else if (DebugMode == ClusterDebugMode.LODLevel) DebugMode = ClusterDebugMode.None;
        }
    }

    // ─── Freeze culling camera ───
    private bool _freezeCullingCamera;
    public bool FreezeCullingCamera
    {
        get => _freezeCullingCamera;
        set
        {
            if (value && !_freezeCullingCamera)
            {
                _frozenView = _view;
                _frozenProj = _proj;
                _frozenCameraPos = _cameraPos;
                _frozenLodThreshold = _lodThreshold;
                _frozenLodScale = _lodScale;
                _frozenForcedLODLevel = _forcedLODLevel;
            }
            _freezeCullingCamera = value;
        }
    }

    private Matrix4x4 _frozenView, _frozenProj;
    private Vector3 _frozenCameraPos;
    private float _frozenLodThreshold, _frozenLodScale;
    private int _frozenForcedLODLevel;

    // ─── Debug readback stats (1-frame latency) ───
    public uint DebugCandidateCount => _debugReadbackPass?.CandidateCount ?? 0;
    public uint DebugDrawVertexCount => _debugReadbackPass?.DrawVertexCount ?? 0;
    public uint DebugDrawInstanceCount => _debugReadbackPass?.DrawInstanceCount ?? 0;
    public uint DebugPhase2DrawVertexCount => _debugReadbackPass?.Phase2DrawVertexCount ?? 0;
    public uint DebugPhase2DrawInstanceCount => _debugReadbackPass?.Phase2DrawInstanceCount ?? 0;
    public uint DebugCandidateArgsX => _debugReadbackPass?.CandidateArgs[0] ?? 0;
    public uint DebugPhase2Count => _debugReadbackPass?.Phase2CandidateCount ?? 0;

    // ─── Page streaming stats ───
    public uint LastPageFaultCount => _clusterStreamer.LastFrameFaultCount;
    public uint LastLoadedPageCount => _clusterStreamer.LastFrameLoadedPages;

    // ─── HiZ debug data ───
    private byte[]? _lastDebugHiZData;
    public ReadOnlySpan<byte> DebugHiZData => _lastDebugHiZData ?? ReadOnlySpan<byte>.Empty;

    // ─── Camera ───
    private Matrix4x4 _view = Matrix4x4.Identity;
    private Matrix4x4 _proj = Matrix4x4.Identity;
    private Vector3 _cameraPos;
    private float _lodThreshold = 1.0f;
    private float _lodScale = 500.0f;
    private int _forcedLODLevel = -1;
    private Matrix4x4 _prevViewProjT = Matrix4x4.Identity;
    private Matrix4x4 _prevView = Matrix4x4.Identity;
    private Matrix4x4 _prevProj = Matrix4x4.Identity;

    // Default textures for material setup
    private ITexture[]? _defaultTextures;
    private ISampler? _defaultSampler;

    // ─── 产出（AddPasses 后有效） ───
    public ClusterGlobalResources LastGlobalResources { get; private set; }
    public ClusterCullOutput LastCullOutput { get; private set; }
    public ClusterRasterOutput LastOpaqueRasterOutput { get; private set; }
    public ClusterRasterOutput LastTransparentRasterOutput { get; private set; }
    public ClusterRasterBinOutput LastRasterBinOutput { get; private set; }
    public ClusterShadeBinOutput LastShadeBinOutput { get; private set; }
    public ClusterShadeOutput LastShadeOutput { get; private set; }

    private ClusterPipeline(
        string name,
        RenderContext context,
        ClusterResourceManager clusterMgr,
        InstanceDataManager instanceMgr,
        MaterialRegistry registry,
        bool includeTransparent
    )
    {
        Name = name;
        _context = context;
        _clusterMgr = clusterMgr;
        _instanceMgr = instanceMgr;
        _registry = registry;
        IncludeTransparentPass = includeTransparent;
        _clusterStreamer = new ClusterStreamer(clusterMgr);

        _uploadStage = new ClusterUploadStage(context, clusterMgr, instanceMgr);
        _traverseStage = new ClusterTraverseStage(context, clusterMgr, instanceMgr);
        _cullStage = new ClusterCullStage(context);
        _rasterBinStage = new ClusterRasterBinStage(context);
        _rasterBinStageP2 = new ClusterRasterBinStage(context);
        _drawStageP1 = new ClusterDrawStage(context, "DrawPhase1");
        _drawStageP2 = new ClusterDrawStage(context, "DrawPhase2");
        _shadeBinStage = new ClusterShadeBinStage(context);
        _shadeStage = new ClusterShadeStage(context, registry);

        if (includeTransparent)
            _drawStageTransparent = new ClusterDrawStage(context, "DrawTransparent");
    }

    // ─── Factory methods ───

    /// <summary>创建不透明管线。</summary>
    public static ClusterPipeline Opaque(
        RenderContext ctx, ClusterResourceManager clusterMgr,
        InstanceDataManager instanceMgr, MaterialRegistry registry
    ) => new("ClusterPipeline.Opaque", ctx, clusterMgr, instanceMgr, registry, false);

    /// <summary>创建包含透明 Pass 的管线（共享 Depth，独立 VisBuffer）。</summary>
    public static ClusterPipeline OpaqueAndTransparent(
        RenderContext ctx, ClusterResourceManager clusterMgr,
        InstanceDataManager instanceMgr, MaterialRegistry registry
    ) => new("ClusterPipeline.OpaqueAndTransparent", ctx, clusterMgr, instanceMgr, registry, true);

    public void SetCamera(
        in Matrix4x4 view, in Matrix4x4 proj, Vector3 cameraPos,
        float lodThreshold = 1.0f, float lodScale = 500.0f, int forcedLODLevel = -1)
    {
        _view = view; _proj = proj; _cameraPos = cameraPos;
        _lodThreshold = lodThreshold; _lodScale = lodScale; _forcedLODLevel = forcedLODLevel;
    }

    /// <summary>
    /// Sets up a StandardPBRMaterial with default (1×1 white) textures.
    /// </summary>
    public void SetupMaterialWithDefaults(StandardPBRMaterial mat)
    {
        if (_defaultTextures == null || _defaultSampler == null)
            throw new InvalidOperationException(
                "Default textures not created. Call Initialize() first."
            );
        #pragma warning disable CS0618 // Obsolete access is intentional
        ClusterRenderFeature.SetupDefaultMaterialSlots(
            mat, _defaultTextures[0], _defaultTextures[1], _defaultTextures[2], _defaultSampler
        );
        #pragma warning restore CS0618
    }

    public void Initialize(RenderContext context)
    {
        _uploadStage.Init();
        _traverseStage.Init();
        _cullStage.Init();
        _rasterBinStage.Init();
        _rasterBinStageP2.Init();
        _drawStageP1.Init();
        _drawStageP2?.Init();
        _drawStageTransparent?.Init();
        _shadeBinStage.Init();
        _shadeStage.Init();
        _debugReadbackPass = new ClusterDebugReadbackPass(context);

        // Register material shader types + create default textures
        #pragma warning disable CS0618 // Obsolete access is intentional
        ClusterRenderFeature.RegisterDefaultMaterials(
            context, _registry, out _defaultTextures, out _defaultSampler);
        #pragma warning restore CS0618
    }

    public void AddPasses(RenderGraph graph)
    {
        // Process page streaming
        _clusterStreamer.Update();

        var colorTarget = graph.GetResourceHandle("ColorTarget");
        var depthTarget = graph.GetResourceHandle("DepthTarget");
        uint screenWidth = _context.SwapChain?.GetDesc().Width ?? 1;
        uint screenHeight = _context.SwapChain?.GetDesc().Height ?? 1;

        _cullStage.UpdateHiZState(screenWidth, screenHeight);

        // Choose active camera (frozen or live)
        var activeView = _freezeCullingCamera ? _frozenView : _view;
        var activeProj = _freezeCullingCamera ? _frozenProj : _proj;
        var activeCamPos = _freezeCullingCamera ? _frozenCameraPos : _cameraPos;
        var activeLodThreshold = _freezeCullingCamera ? _frozenLodThreshold : _lodThreshold;
        var activeLodScale = _freezeCullingCamera ? _frozenLodScale : _lodScale;
        var activeForcedLOD = _freezeCullingCamera ? _frozenForcedLODLevel : _forcedLODLevel;

        // ─── Upload ───
        var uploadConfig = ClusterUploadConfig.Default(activeView, activeProj, activeCamPos, screenWidth, screenHeight) with
        {
            LodThreshold = activeLodThreshold,
            LodScale = activeLodScale,
            ForcedLODLevel = activeForcedLOD,
            BypassCulling = BypassCulling,
            DebugMode = (uint)DebugMode,
            DumpNextFrame = DumpNextFrame,
            DebugShowHiZAABBs = DebugShowHiZAABBs,
            PrevViewProj = Matrix4x4.Transpose(_prevViewProjT),
            PrevView = _prevView,
            PrevProj = _prevProj,
            HasPrevHistory = _cullStage.HasPrevHistory,
            HiZMipCount = _cullStage.HiZMipCount,
            HiZInvSize = (_cullStage.HiZWidth > 0 && _cullStage.HiZHeight > 0)
                ? new Vector2(1.0f / _cullStage.HiZWidth, 1.0f / _cullStage.HiZHeight)
                : Vector2.Zero,
        };
        var globals = _uploadStage.AddPasses(graph, uploadConfig);
        LastGlobalResources = globals;

        // ─── Traverse ───
        _traverseStage.SetFrameData(
            activeView, activeProj, activeCamPos, activeLodThreshold, activeLodScale, activeForcedLOD,
            BypassCulling, _prevViewProjT, _cullStage.HasPrevHistory,
            _cullStage.HiZMipCount,
            (_cullStage.HiZWidth > 0 && _cullStage.HiZHeight > 0)
                ? new Vector2(1.0f / _cullStage.HiZWidth, 1.0f / _cullStage.HiZHeight)
                : Vector2.Zero
        );
        var traverseOut = _traverseStage.AddPasses(graph, globals, ClusterTraverseConfig.Default());

        // ─── Cull ───
        var cullConfig = ClusterCullConfig.Default() with { HiZMode = HiZMode };
        var cullOut = _cullStage.AddPasses(graph, traverseOut, globals, cullConfig, colorTarget, depthTarget, DebugShowHiZAABBs);
        LastCullOutput = cullOut;

        graph.MarkOutput(colorTarget);
        if (cullOut.HiZ.IsValid)
            graph.MarkOutput(cullOut.HiZ);

        // ─── RasterBin Phase1 ───
        var rasterBinP1 = _rasterBinStage.AddPasses(graph, cullOut, globals,
            ClusterRasterBinConfig.Default(), cullOut.DrawArgs, cullOut.Phase2DrawArgs);

        // ─── Draw Phase1 ───
        var drawConfigP1 = ClusterDrawConfig.Opaque() with
        {
            DebugMode = DebugMode,
            Wireframe = WireframeEnabled,
            Overdraw = OverdrawEnabled,
            VisibleClusterMeta = traverseOut.ZeroOffsetBuffer,
        };
        var rasterP1 = _drawStageP1.AddPasses(graph, rasterBinP1, cullOut, globals, drawConfigP1, depthTarget, screenWidth, screenHeight);

        // ─── Phase1 HiZ Build (AFTER Phase1 Draw so depth has real geometry) ───
        if ((HiZMode == HiZDebugMode.Phase1ThenHiZ || HiZMode == HiZDebugMode.Full2Phase)
            && cullOut.HiZ.IsValid)
        {
            _cullStage.AddFinalHiZBuild(graph, depthTarget, cullOut.HiZ);
        }

        if (HiZMode == HiZDebugMode.Full2Phase && cullOut.HiZ.IsValid)
        {
            // ─── Phase2 Cull (uses Phase1's HiZ built from real depth) ───
            _cullStage.AddPhase2Passes(graph, cullOut, globals);

            // ─── RasterBin Phase2 ───
            var rasterBinP2 = _rasterBinStageP2.AddPasses(graph, cullOut, globals,
                ClusterRasterBinConfig.Default(), cullOut.Phase2DrawArgs, cullOut.DrawArgs, tag: "P2");

            // ─── Draw Phase2 ───
            var drawConfigP2 = ClusterDrawConfig.Opaque() with
            {
                OutputVisBuffer = rasterP1.VisBuffer,
                OutputDepth = rasterP1.DepthTarget,
                ClearTargets = false,
                DebugMode = DebugMode,
                Wireframe = WireframeEnabled,
                Overdraw = OverdrawEnabled,
                Tag = "P2",
                VisibleClusterMeta = traverseOut.ZeroOffsetBuffer,
            };
            var rasterP2 = _drawStageP2!.AddPasses(graph, rasterBinP2, cullOut, globals, drawConfigP2, depthTarget, screenWidth, screenHeight);

            // ─── Final HiZ Build (from Phase1+Phase2 depth, for next frame's Phase1) ───
            _cullStage.AddFinalHiZBuild(graph, depthTarget, cullOut.HiZ);

            LastOpaqueRasterOutput = rasterP2;
        }
        else
        {
            LastOpaqueRasterOutput = rasterP1;
        }

        LastRasterBinOutput = rasterBinP1;

        // ─── Transparent pass (optional) ───
        if (IncludeTransparentPass && _drawStageTransparent != null)
        {
            var transparentConfig = ClusterDrawConfig.Transparent(LastOpaqueRasterOutput.DepthTarget) with
            {
                DebugMode = DebugMode,
                VisibleClusterMeta = traverseOut.ZeroOffsetBuffer,
            };
            var transparentRaster = _drawStageTransparent.AddPasses(
                graph, rasterBinP1, cullOut, globals, transparentConfig,
                depthTarget, screenWidth, screenHeight);
            LastTransparentRasterOutput = transparentRaster;
        }

        // ─── Shade ───
        if (UseVisBuffer)
        {
            uint drawDebugMode = (uint)DebugMode;
            bool isResolveOnlyDebug = drawDebugMode == 1 || drawDebugMode == 2;

            var shadeBinOut = _shadeBinStage.AddPasses(graph,
                LastOpaqueRasterOutput, cullOut, globals,
                ClusterShadeBinConfig.Default(),
                Math.Max(_registry.MaterialCount, 1u),
                screenWidth, screenHeight);
            LastShadeBinOutput = shadeBinOut;

            var viewProjT = Matrix4x4.Transpose(_view * _proj);
            var viewT = Matrix4x4.Transpose(_view);
            var shadeConfig = ClusterShadeConfig.Default(colorTarget) with
            {
                DebugMode = drawDebugMode,
                UseResolveDebug = isResolveOnlyDebug,
                ViewProj = viewProjT,
                View = viewT,
                PageTableSize = _clusterMgr.PageCount,
                QuantOrigin = _clusterMgr.QuantOrigin,
                QuantStep = _clusterMgr.QuantStep,
                CameraPos = _freezeCullingCamera ? _frozenCameraPos : _cameraPos,
            };
            var shadeOut = _shadeStage.AddPasses(graph,
                LastOpaqueRasterOutput, shadeBinOut, cullOut, globals,
                shadeConfig, depthTarget, screenWidth, screenHeight);
            LastShadeOutput = shadeOut;
        }

        // ─── Debug readback ───
        if (_debugReadbackPass != null)
        {
            _debugReadbackPass.HCandidateCount = traverseOut.CandidateCount;
            _debugReadbackPass.HIndirectDrawArgs = cullOut.DrawArgs;
            _debugReadbackPass.HCandidateArgs = traverseOut.CandidateArgs;
            _debugReadbackPass.HPhase2CandidateCount = cullOut.Phase2CandidateCount;
            _debugReadbackPass.HPhase2IndirectDrawArgs = cullOut.Phase2DrawArgs;

            var hDebugReadback = graph.CreateBuffer("DebugReadback", new BufferDesc
            {
                Size = 256,
                Usage = Usage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            graph.MarkOutput(hDebugReadback);
            _debugReadbackPass.HDebugReadbackBuffer = hDebugReadback;
            graph.AddPass(_debugReadbackPass);

            _lastDebugHiZData = _debugReadbackPass.DebugHiZData;
        }

        // ─── Update history ───
        _prevViewProjT = Matrix4x4.Transpose(activeView * activeProj);
        _prevView = activeView;
        _prevProj = activeProj;

        if (DumpNextFrame) DumpNextFrame = false;
    }

    public void Dispose()
    {
        _uploadStage.Dispose();
        _traverseStage.Dispose();
        _cullStage.Dispose();
        _rasterBinStage.Dispose();
        _rasterBinStageP2.Dispose();
        _drawStageP1.Dispose();
        _drawStageP2?.Dispose();
        _drawStageTransparent?.Dispose();
        _shadeBinStage.Dispose();
        _shadeStage.Dispose();
        _defaultSampler?.Dispose();
        if (_defaultTextures != null)
            foreach (var t in _defaultTextures) t?.Dispose();
    }
}
