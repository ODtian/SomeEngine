using System.Numerics;
using System.Runtime.InteropServices;
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
    private readonly ClusterBVHTraversePass _bvhTraversePass;

    private readonly ClusterShade _shadeStage;

    private readonly RenderContext _context;
    private readonly ClusterResourceManager _clusterMgr;
    private readonly InstanceDataManager _instanceMgr;
    private readonly MaterialRegistry _registry;

    // Internal passes owned by Pipeline
    private readonly ClusterStreamer _clusterStreamer;
    private readonly PingPongHandle _hizPingPong;
    private HiZDebugMode _prevHiZMode = HiZDebugMode.Full2Phase;
    internal ClusterDebugReadbackPass? _debugReadbackPass;
    private IPipelineState? _materialShadePSO;

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

    private readonly BinSpace _binSpace = new();
    private int _rasterBinFieldIndex;
    private int _shadingBinFieldIndex;

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
        _hizPingPong = new PingPongHandle();

        _uploadStage = new ClusterUploadStage(context, clusterMgr, instanceMgr);
        _bvhTraversePass = new ClusterBVHTraversePass(context, clusterMgr, instanceMgr);

        _shadeStage = new ClusterShade(context, registry);
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
    /// Sets up a Material with default (1×1 white) textures.
    /// </summary>
    public void SetupMaterialWithDefaults(Material mat)
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

    /// <summary>
    /// Creates SRB for a newly registered material's passes using the shade PSO.
    /// </summary>
    public void CreateSRBForMaterial(Material mat)
    {
        if (_materialShadePSO == null)
            throw new InvalidOperationException("PSO not created. Call Initialize() first.");
        foreach (var pass in mat.Passes)
        {
            pass.SRB ??= _materialShadePSO.CreateShaderResourceBinding(false);
        }
    }

    public void Initialize(RenderContext context)
    {
        _uploadStage.Init();
        _bvhTraversePass.Init();

        _debugReadbackPass = new ClusterDebugReadbackPass(context);

        // 初始化管线自有 BinSpace 布局
        _rasterBinFieldIndex = _binSpace.RegisterField("RasterBin");
        _shadingBinFieldIndex = _binSpace.RegisterField("ShadingBin");
        _binSpace.FreezeLayout();

        // Register material shader types + create default textures (gets PSO)
        #pragma warning disable CS0618 // Obsolete access is intentional
        ClusterRenderFeature.RegisterDefaultMaterials(
            context, _registry, out _defaultTextures, out _defaultSampler, out _materialShadePSO);
        #pragma warning restore CS0618

        // Set PSO on shade facade before Init
        _shadeStage.SetMaterialShadePSO(_materialShadePSO);
        _shadeStage.Init();
    }

    public void AddPasses(RenderGraph graph)
    {
        // Process page streaming
        _clusterStreamer.Update();

        var colorTarget = graph.GetResourceHandle("ColorTarget");
        var depthTarget = graph.GetResourceHandle("DepthTarget");
        uint screenWidth = _context.SwapChain?.GetDesc().Width ?? 1;
        uint screenHeight = _context.SwapChain?.GetDesc().Height ?? 1;

        // ─── HiZ state (managed by Pipeline via PingPongHandle) ───
        uint hizWidth = Math.Max(screenWidth, 1);
        uint hizHeight = Math.Max(screenHeight, 1);
        uint hizMipCount = ClusterCull.CalculateMipCount(hizWidth, hizHeight);
        var hizInvSize = new Vector2(1.0f / hizWidth, 1.0f / hizHeight);

        // Reset ping-pong history when HiZ mode changes
        if (HiZMode != _prevHiZMode)
        {
            _hizPingPong.Reset();
            _prevHiZMode = HiZMode;
        }

        var hCurrHiZ = RenderGraphHandle.Invalid;
        var hPrevHiZ = RenderGraphHandle.Invalid;
        bool useHiZ = HiZMode != HiZDebugMode.Legacy && HiZMode != HiZDebugMode.Phase1OnlyPassAll;

        if (useHiZ)
        {
            var hizDesc = new TextureDesc
            {
                Type = ResourceDimension.Tex2d,
                Width = hizWidth,
                Height = hizHeight,
                MipLevels = hizMipCount,
                Format = TextureFormat.R32_Float,
                Usage = Usage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            };
            _hizPingPong.Prepare(graph, "HiZ", hizDesc, out hCurrHiZ, out hPrevHiZ);
        }

        // Choose active camera (frozen or live)
        var activeView = _freezeCullingCamera ? _frozenView : _view;
        var activeProj = _freezeCullingCamera ? _frozenProj : _proj;
        var activeCamPos = _freezeCullingCamera ? _frozenCameraPos : _cameraPos;
        var activeLodThreshold = _freezeCullingCamera ? _frozenLodThreshold : _lodThreshold;
        var activeLodScale = _freezeCullingCamera ? _frozenLodScale : _lodScale;
        var activeForcedLOD = _freezeCullingCamera ? _frozenForcedLODLevel : _forcedLODLevel;

        // ─── Upload global data (BVH/InstanceHeaders/PageHeap) ───
        _binSpace.RebuildIfDirty(_registry);
        var globals = _uploadStage.AddPasses(graph);
        LastGlobalResources = globals;

        // ─── Create CullingUniforms + upload pass ───
        var cullingData = CullingUniforms.Create(
            activeView, activeProj, activeCamPos,
            activeLodThreshold, activeLodScale, activeForcedLOD,
            (uint)_instanceMgr.Count, BypassCulling, DumpNextFrame, DebugShowHiZAABBs,
            _prevViewProjT, _hizPingPong.HasHistory, hizMipCount, hizInvSize,
            _prevView, _prevProj,
            _clusterMgr.QuantOrigin, _clusterMgr.QuantStep
        );
        var hCullingUB = AddDynamicUniformPass(graph, "CullingUniforms", cullingData);

        // ─── Create DrawUniforms + upload pass ───
        var viewProjT = Matrix4x4.Transpose(activeView * activeProj);
        var viewT = Matrix4x4.Transpose(activeView);
        var hDrawUB = AddDynamicUniformPass(graph, "DrawUniforms", new DrawUniforms
        {
            ViewProj = viewProjT,
            View = viewT,
            PageTableSize = _clusterMgr.PageCount,
            DebugMode = (uint)DebugMode,
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
            QuantOrigin = _clusterMgr.QuantOrigin,
            QuantStep = _clusterMgr.QuantStep,
        });

        // ─── Build uploadConfig for Traverse stage frame data ───
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
            HasPrevHistory = _hizPingPong.HasHistory,
            HiZMipCount = hizMipCount,
            HiZInvSize = hizInvSize,
        };

        // ─── Traverse ───
        var traverseOut = ClusterTraverse.AddPasses(graph, _context, _bvhTraversePass, _clusterMgr, _instanceMgr, globals, hCullingUB, ClusterTraverseConfig.Default(), uploadConfig);

        // ─── Phase2 + utility buffers (owned by Pipeline, not Traverse) ───
        var hPhase2IndirectDrawArgs = graph.CreateBuffer("Phase2IndirectDrawArgs", new BufferDesc
        {
            Size = 256,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
        });
        var hZeroOffsetBuffer = graph.CreateBuffer("ZeroOffsetBuffer", new BufferDesc
        {
            Size = 16,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
        });
        // Clear Phase2 draw args + zero-offset buffer
        graph.AddPass(new ClusterClearBuffersPass(
            RenderGraphHandle.Invalid, RenderGraphHandle.Invalid, RenderGraphHandle.Invalid,
            RenderGraphHandle.Invalid, RenderGraphHandle.Invalid,
            hPhase2IndirectDrawArgs, hZeroOffsetBuffer, RenderGraphHandle.Invalid
        ));

        // ─── Cull ───
        var cullConfig = ClusterCullConfig.Default() with { HiZMode = HiZMode };
        var cullOut = ClusterCull.AddPasses(graph, _context, traverseOut, globals, hCullingUB, cullConfig, hCurrHiZ, hPrevHiZ, _hizPingPong.HasHistory, hPhase2IndirectDrawArgs, DebugShowHiZAABBs);
        LastCullOutput = cullOut;

        graph.MarkOutput(colorTarget);
        if (hCurrHiZ.IsValid)
            graph.MarkOutput(hCurrHiZ);

        // ─── MaterialSlotBuffer (SOA, shared by raster + shade binning) ───
        var hMaterialSlotBuffer = _binSpace.AddUploadPass(graph);

        // ─── RasterBin Phase1 ───
        var rasterBinP1 = ClusterRasterBin.AddPasses(graph, _context, cullOut,
            globals.GlobalInstanceHeader, cullOut.DrawArgs, cullOut.Phase2DrawArgs, hMaterialSlotBuffer,
            (uint)_binSpace.SlotCapacity, (uint)_rasterBinFieldIndex);

        // ─── Draw Phase1 ───
        var drawConfigP1 = ClusterDrawConfig.Opaque() with
        {
            DebugMode = DebugMode,
            Wireframe = WireframeEnabled,
            Overdraw = OverdrawEnabled,
            VisibleClusterMeta = hZeroOffsetBuffer,
        };
        var rasterP1 = ClusterDraw.AddPasses(graph, _context, rasterBinP1, cullOut, globals, hDrawUB, drawConfigP1, depthTarget, screenWidth, screenHeight);

        // ─── Phase1 HiZ Build (AFTER Phase1 Draw so depth has real geometry) ───
        if ((HiZMode == HiZDebugMode.Phase1ThenHiZ || HiZMode == HiZDebugMode.Full2Phase)
            && hCurrHiZ.IsValid)
        {
            ClusterCull.AddFinalHiZBuild(graph, _context, depthTarget, hCurrHiZ, hizMipCount);
        }

        if (HiZMode == HiZDebugMode.Full2Phase && hCurrHiZ.IsValid)
        {
            // ─── Phase2 Cull (uses Phase1's HiZ built from real depth) ───
            ClusterCull.AddPhase2Passes(graph, _context, cullOut, globals, hCullingUB, hCurrHiZ);

            // ─── RasterBin Phase2 ───
            var rasterBinP2 = ClusterRasterBin.AddPasses(graph, _context, cullOut,
                globals.GlobalInstanceHeader, cullOut.Phase2DrawArgs, cullOut.DrawArgs, hMaterialSlotBuffer,
                (uint)_binSpace.SlotCapacity, (uint)_rasterBinFieldIndex, tag: "P2");

            // ─── Draw Phase2 ───
            var drawConfigP2 = ClusterDrawConfig.Opaque() with
            {
                ClearTargets = false,
                DebugMode = DebugMode,
                Wireframe = WireframeEnabled,
                Overdraw = OverdrawEnabled,
                Tag = "P2",
                VisibleClusterMeta = hZeroOffsetBuffer,
            };
            var rasterP2 = ClusterDraw.AddPasses(graph, _context, rasterBinP2, cullOut, globals, hDrawUB, drawConfigP2, depthTarget, screenWidth, screenHeight,
                hOutputVisBuffer: rasterP1.VisBuffer, hOutputDepth: rasterP1.DepthTarget);

            // ─── Final HiZ Build (from Phase1+Phase2 depth, for next frame's Phase1) ───
            ClusterCull.AddFinalHiZBuild(graph, _context, depthTarget, hCurrHiZ, hizMipCount);

            LastOpaqueRasterOutput = rasterP2;
        }
        else
        {
            LastOpaqueRasterOutput = rasterP1;
        }

        LastRasterBinOutput = rasterBinP1;

        // ─── Transparent pass (optional) ───
        if (IncludeTransparentPass)
        {
            var transparentConfig = ClusterDrawConfig.Opaque() with
            {
                DepthWrite = false,
                ClearTargets = true,
                Tag = "Transparent",
                DebugMode = DebugMode,
                VisibleClusterMeta = hZeroOffsetBuffer,
            };
            var transparentRaster = ClusterDraw.AddPasses(
                graph, _context, rasterBinP1, cullOut, globals, hDrawUB, transparentConfig,
                depthTarget, screenWidth, screenHeight,
                hOutputDepth: LastOpaqueRasterOutput.DepthTarget);
            LastTransparentRasterOutput = transparentRaster;
        }

        // ─── Shade ───
        if (UseVisBuffer)
        {
            var activeCamPosForShade = _freezeCullingCamera ? _frozenCameraPos : _cameraPos;
            var (shadeBinOut, shadeOut) = _shadeStage.AddPasses(graph,
                LastOpaqueRasterOutput, cullOut, globals, hDrawUB,
                hMaterialSlotBuffer, colorTarget, depthTarget,
                _binSpace, _shadingBinFieldIndex, _registry,
                _view, _proj, activeCamPosForShade,
                _clusterMgr.PageCount, _clusterMgr.QuantOrigin, _clusterMgr.QuantStep,
                DebugMode, screenWidth, screenHeight);
            LastShadeBinOutput = shadeBinOut;
            LastShadeOutput = shadeOut;
        }

        // ─── Debug readback ───
        if (_debugReadbackPass != null)
        {
            _debugReadbackPass.AddPasses(graph, traverseOut, cullOut);
            _lastDebugHiZData = _debugReadbackPass.DebugHiZData;
        }

        // ─── Update history ───
        _prevViewProjT = Matrix4x4.Transpose(activeView * activeProj);
        _prevView = activeView;
        _prevProj = activeProj;

        if (useHiZ)
            _hizPingPong.EndFrame();

        if (DumpNextFrame) DumpNextFrame = false;
    }

    /// <summary>
    /// 创建 Dynamic 的 uniform buffer 并添加 upload pass。
    /// </summary>
    private static RenderGraphHandle AddDynamicUniformPass<T>(RenderGraph graph, string name, T data) where T : unmanaged
    {
        var handle = graph.CreateBuffer(name, new BufferDesc
        {
            Size = (ulong)Marshal.SizeOf<T>(),
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        graph.AddPass<object>(
            $"Upload{name}",
            (builder, _) => { builder.Write(handle, ResourceState.ConstantBuffer); },
            (rgCtx, _) =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(handle);
                if (ctx2 != null && buf != null)
                {
                    var span = ctx2.MapBuffer<T>(buf, MapType.Write, MapFlags.Discard);
                    span[0] = data;
                    ctx2.UnmapBuffer(buf, MapType.Write);
                }
            }
        );
        return handle;
    }

    public void Dispose()
    {
        _uploadStage.Dispose();
        _bvhTraversePass.Dispose();
        _shadeStage.Dispose();
        _binSpace.Dispose();
        _defaultSampler?.Dispose();
        if (_defaultTextures != null)
            foreach (var t in _defaultTextures) t?.Dispose();
    }
}
