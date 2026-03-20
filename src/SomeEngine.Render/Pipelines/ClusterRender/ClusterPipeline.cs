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
/// 示例代码，演示如何用内置 Stage 组装管线。
/// </summary>
public class ClusterPipeline : IRenderFeature
{
    public string Name { get; }

    // ─── Owned resources ───
    private readonly RenderContext _context;
    private readonly ClusterResourceManager _clusterMgr;
    private readonly InstanceDataManager _instanceMgr;
    private readonly MaterialRegistry _registry;
    private readonly ClusterUploadStage _uploadStage;
    private readonly ClusterBVHTraversePass _bvhTraversePass;
    private readonly ClusterShade _shadeStage;
    private readonly ClusterStreamer _clusterStreamer;
    private readonly PingPongHandle _hizPingPong = new();
    private readonly BinSpace _binSpace = new();
    public BinSpace BinSpace => _binSpace;
    private int _rasterBinFieldIndex, _shadingBinFieldIndex;

    internal ClusterDebugReadbackPass? _debugReadbackPass;
    private ShadePSOGroup[] _shadePSOGroups = [];
    private uint _lastBinSpaceVersion = uint.MaxValue;
    private HiZDebugMode _prevHiZMode = HiZDebugMode.Full2Phase;

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

    // ─── Camera ───
    private ClusterCameraData _camera;
    private ClusterCameraData _frozenCamera;
    private bool _freezeCullingCamera;
    private Matrix4x4 _prevViewProjT = Matrix4x4.Identity;
    private Matrix4x4 _prevView = Matrix4x4.Identity;
    private Matrix4x4 _prevProj = Matrix4x4.Identity;

    public bool FreezeCullingCamera
    {
        get => _freezeCullingCamera;
        set { if (value && !_freezeCullingCamera) _frozenCamera = _camera; _freezeCullingCamera = value; }
    }

    // ─── Debug readback stats (1-frame latency) ───
    public uint DebugCandidateCount => _debugReadbackPass?.CandidateCount ?? 0;
    public uint DebugDrawVertexCount => _debugReadbackPass?.DrawVertexCount ?? 0;
    public uint DebugDrawInstanceCount => _debugReadbackPass?.DrawInstanceCount ?? 0;
    public uint DebugPhase2DrawVertexCount => _debugReadbackPass?.Phase2DrawVertexCount ?? 0;
    public uint DebugPhase2DrawInstanceCount => _debugReadbackPass?.Phase2DrawInstanceCount ?? 0;
    public uint DebugCandidateArgsX => _debugReadbackPass?.CandidateArgs[0] ?? 0;
    public uint DebugPhase2Count => _debugReadbackPass?.Phase2CandidateCount ?? 0;
    public uint LastPageFaultCount => _clusterStreamer.LastFrameFaultCount;
    public uint LastLoadedPageCount => _clusterStreamer.LastFrameLoadedPages;
    private byte[]? _lastDebugHiZData;
    public ReadOnlySpan<byte> DebugHiZData => _lastDebugHiZData ?? ReadOnlySpan<byte>.Empty;

    // ─── 产出（AddPasses 后有效） ───
    public ClusterGlobalResources LastGlobalResources { get; private set; }
    public ClusterCullOutput LastCullOutput { get; private set; }
    public ClusterRasterOutput LastOpaqueRasterOutput { get; private set; }
    public ClusterRasterOutput LastTransparentRasterOutput { get; private set; }
    public ClusterRasterBinOutput LastRasterBinOutput { get; private set; }
    public ClusterShadeBinOutput LastShadeBinOutput { get; private set; }
    public ClusterShadeOutput LastShadeOutput { get; private set; }

    // ─── Construction ───

    private ClusterPipeline(
        string name, RenderContext context,
        ClusterResourceManager clusterMgr, InstanceDataManager instanceMgr,
        MaterialRegistry registry, bool includeTransparent)
    {
        Name = name;
        _context = context;
        _clusterMgr = clusterMgr;
        _instanceMgr = instanceMgr;
        _registry = registry;
        IncludeTransparentPass = includeTransparent;
        _clusterStreamer = new ClusterStreamer(clusterMgr);
        _uploadStage = new ClusterUploadStage(context, clusterMgr, instanceMgr);
        _bvhTraversePass = new ClusterBVHTraversePass(context, clusterMgr, instanceMgr);
        _shadeStage = new ClusterShade(context, registry);
    }

    public static ClusterPipeline Opaque(
        RenderContext ctx, ClusterResourceManager cm,
        InstanceDataManager im, MaterialRegistry mr
    ) => new("ClusterPipeline.Opaque", ctx, cm, im, mr, false);

    public static ClusterPipeline OpaqueAndTransparent(
        RenderContext ctx, ClusterResourceManager cm,
        InstanceDataManager im, MaterialRegistry mr
    ) => new("ClusterPipeline.OpaqueAndTransparent", ctx, cm, im, mr, true);

    // ─── Public API ───

    public void SetCamera(
        in Matrix4x4 view, in Matrix4x4 proj, Vector3 cameraPos,
        float lodThreshold = 1.0f, float lodScale = 500.0f, int forcedLODLevel = -1)
    {
        uint sw = _context.SwapChain?.GetDesc().Width ?? 1;
        uint sh = _context.SwapChain?.GetDesc().Height ?? 1;
        _camera = ClusterCameraData.Default(view, proj, cameraPos, sw, sh) with
        {
            LodThreshold = lodThreshold, LodScale = lodScale, ForcedLODLevel = forcedLODLevel,
            PrevViewProj = Matrix4x4.Transpose(_prevViewProjT),
            PrevView = _prevView, PrevProj = _prevProj,
        };
    }





    public void Initialize(RenderContext context)
    {
        _uploadStage.Init();
        _bvhTraversePass.Init();
        _debugReadbackPass = new ClusterDebugReadbackPass(context);
        _rasterBinFieldIndex = _binSpace.RegisterField("RasterBin");
        _shadingBinFieldIndex = _binSpace.RegisterField("ShadingBin");

        _binSpace.RegisterRegion(_rasterBinFieldIndex, "default",
            () => _registry.Query<SomeEngine.Render.Materials.ClusterShaderTag>(),
            _ => 0UL); // all same signature → 1 bin

        _binSpace.RegisterRegion(_shadingBinFieldIndex, "opaque",
            () => _registry.Query<SomeEngine.Render.Materials.OpaqueTag>(),
            p => p.ComputeSignature());

        _binSpace.FreezeLayout();
        _shadeStage.Init();
    }
    // ─── PSO Group Management ───

    private readonly List<(SomeEngine.Assets.Schema.ShaderAsset? shader, IPipelineState pso)> _psoCache = new();

    /// <summary>
    /// Rebuild shade PSOGroups from BinQueue state.
    /// BinQueue guarantees same-ShaderAsset bins are contiguous (sort in Rebuild).
    /// </summary>
    private void RebuildShadePSOGroups()
    {
        _lastBinSpaceVersion = _binSpace.Version;

        int totalBins = _binSpace.GetTotalBinCount(_shadingBinFieldIndex);
        if (totalBins == 0)
        {
            _shadePSOGroups = [];
            return;
        }

        var groups = new List<ShadePSOGroup>();
        int groupStart = 0;
        var firstPass = _binSpace.GetPass(_shadingBinFieldIndex, 0);
        var currentShader = firstPass.Shader;

        for (int bin = 1; bin <= totalBins; bin++)
        {
            var nextShader = bin < totalBins ? _binSpace.GetPass(_shadingBinFieldIndex, bin).Shader : null;
            bool isBreak = bin == totalBins || !ReferenceEquals(nextShader, currentShader);

            if (isBreak)
            {
                int count = bin - groupStart;
                var pso = FindOrCreatePSO(currentShader);
                var srbs = new IShaderResourceBinding[count];
                var passes = new MaterialPass[count];

                for (int i = 0; i < count; i++)
                {
                    var pass = _binSpace.GetPass(_shadingBinFieldIndex, groupStart + i);
                    passes[i] = pass;
                    srbs[i] = pso.CreateShaderResourceBinding(false);
                    // Bind material textures (Mutable — set once at rebuild)
                    pass.ApplyToSRB(srbs[i]);
                }

                groups.Add(new ShadePSOGroup
                {
                    PSO = pso,
                    SRBs = srbs,
                    Passes = passes,
                    BinStart = groupStart,
                    BinCount = count,
                });

                if (bin < totalBins)
                {
                    groupStart = bin;
                    currentShader = nextShader;
                }
            }
        }

        _shadePSOGroups = groups.ToArray();
    }

    private IPipelineState FindOrCreatePSO(SomeEngine.Assets.Schema.ShaderAsset? shader)
    {
        if (shader == null || _context.Device == null)
            throw new InvalidOperationException("MaterialPass must have a non-null ShaderAsset.");

        // Linear scan — unique shader count is tiny (1~5)
        foreach (var (s, p) in _psoCache)
            if (ReferenceEquals(s, shader)) return p;

        // Compile compute PSO from the shader asset
        using var cs = shader.CreateShader(_context, "CSMaterialShade");
        var pso = _context.Device.CreateComputePipelineState(
            new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = $"Shade PSO ({shader.Name})",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = ShadePSOLayout,
                },
                Cs = cs,
            });
        _psoCache.Add((shader, pso));
        return pso;
    }

    /// <summary>
    /// Shared resource layout for all shade PSOs.
    /// Material-specific textures/samplers are Mutable; pipeline resources are Dynamic.
    /// </summary>
    private static readonly PipelineResourceLayoutDesc ShadePSOLayout = new()
    {
        DefaultVariableType = ShaderResourceVariableType.Mutable,
        Variables =
        [
            new ShaderResourceVariableDesc { Name = "Uniforms", ShaderStages = ShaderType.Compute, Type = ShaderResourceVariableType.Dynamic },
            new ShaderResourceVariableDesc { Name = "VisBuffer", ShaderStages = ShaderType.Compute, Type = ShaderResourceVariableType.Dynamic },
            new ShaderResourceVariableDesc { Name = "VisibleClusters", ShaderStages = ShaderType.Compute, Type = ShaderResourceVariableType.Dynamic },
            new ShaderResourceVariableDesc { Name = "PageHeap", ShaderStages = ShaderType.Compute, Type = ShaderResourceVariableType.Dynamic },
            new ShaderResourceVariableDesc { Name = "Instances", ShaderStages = ShaderType.Compute, Type = ShaderResourceVariableType.Dynamic },
            new ShaderResourceVariableDesc { Name = "PixelCoordBuffer", ShaderStages = ShaderType.Compute, Type = ShaderResourceVariableType.Dynamic },
            new ShaderResourceVariableDesc { Name = "BinOffsets", ShaderStages = ShaderType.Compute, Type = ShaderResourceVariableType.Dynamic },
            new ShaderResourceVariableDesc { Name = "BinCounts", ShaderStages = ShaderType.Compute, Type = ShaderResourceVariableType.Dynamic },
            new ShaderResourceVariableDesc { Name = "InstanceHeaders", ShaderStages = ShaderType.Compute, Type = ShaderResourceVariableType.Dynamic },
            new ShaderResourceVariableDesc { Name = "InstanceDataHeap", ShaderStages = ShaderType.Compute, Type = ShaderResourceVariableType.Dynamic },
            new ShaderResourceVariableDesc { Name = "OutputColor", ShaderStages = ShaderType.Compute, Type = ShaderResourceVariableType.Dynamic },
        ],
    };

    // ─── Main pipeline assembly ───

    public void AddPasses(RenderGraph graph)
    {
        _clusterStreamer.Update();

        var colorTarget = graph.GetResourceHandle("ColorTarget");
        var depthTarget = graph.GetResourceHandle("DepthTarget");
        var camera = _freezeCullingCamera ? _frozenCamera : _camera;

        // Reset HiZ history on mode change
        if (HiZMode != _prevHiZMode) { _hizPingPong.Reset(); _prevHiZMode = HiZMode; }

        // Upload
        _binSpace.RebuildIfDirty(_registry);
        if (_binSpace.Version != _lastBinSpaceVersion)
            RebuildShadePSOGroups();
        _shadeStage.PSOGroups = _shadePSOGroups;
        var globals = _uploadStage.AddPasses(graph);
        LastGlobalResources = globals;

        // DrawUniforms
        var hDrawUB = AddDynamicUniformPass(graph, "DrawUniforms", new DrawUniforms
        {
            ViewProj = Matrix4x4.Transpose(camera.View * camera.Proj),
            View = Matrix4x4.Transpose(camera.View),
            PageTableSize = _clusterMgr.PageCount,
            DebugMode = (uint)DebugMode,
            ScreenWidth = camera.ScreenWidth,
            ScreenHeight = camera.ScreenHeight,
            QuantOrigin = _clusterMgr.QuantOrigin,
            QuantStep = _clusterMgr.QuantStep,
        });

        // Traverse
        var traverseOut = ClusterTraverse.AddPasses(
            graph, _context, _bvhTraversePass, _clusterMgr, _instanceMgr,
            globals, camera, ClusterTraverseConfig.Default());

        var hMaterialSlots = _binSpace.AddUploadPass(graph);
        graph.MarkOutput(colorTarget);

        // Cull + RasterBin + Draw (via HiZ 2-Phase)
        var hizResult = ClusterHiZ.Add2PhasePipeline(
            graph, _context, traverseOut, globals, camera, hDrawUB,
            hMaterialSlots, _binSpace, _rasterBinFieldIndex, _hizPingPong, depthTarget,
            new ClusterHiZ.HiZConfig
            {
                HiZMode = HiZMode, DebugMode = DebugMode,
                Wireframe = WireframeEnabled, Overdraw = OverdrawEnabled,
                DebugShowHiZAABBs = DebugShowHiZAABBs, DumpNextFrame = DumpNextFrame,
            });
        LastCullOutput = hizResult.Cull;
        LastOpaqueRasterOutput = hizResult.Raster;

        // Shade
        if (UseVisBuffer)
        {
            var camPos = _freezeCullingCamera ? _frozenCamera.CameraPos : _camera.CameraPos;
            var (shadeBinOut, shadeOut) = _shadeStage.AddPasses(graph,
                LastOpaqueRasterOutput, hizResult.Cull, globals, hDrawUB,
                hMaterialSlots, colorTarget, depthTarget,
                _binSpace, _shadingBinFieldIndex, _registry,
                _camera.View, _camera.Proj, camPos,
                _clusterMgr.PageCount, _clusterMgr.QuantOrigin, _clusterMgr.QuantStep,
                DebugMode, camera.ScreenWidth, camera.ScreenHeight);
            LastShadeBinOutput = shadeBinOut;
            LastShadeOutput = shadeOut;
        }

        // Debug readback
        if (_debugReadbackPass != null)
        {
            _debugReadbackPass.AddPasses(graph, traverseOut, hizResult.Cull);
            _lastDebugHiZData = _debugReadbackPass.DebugHiZData;
        }

        // Update history
        _prevViewProjT = Matrix4x4.Transpose(camera.View * camera.Proj);
        _prevView = camera.View;
        _prevProj = camera.Proj;
        if (DumpNextFrame) DumpNextFrame = false;
    }

    private static RenderGraphHandle AddDynamicUniformPass<T>(RenderGraph graph, string name, T data) where T : unmanaged
    {
        var handle = graph.CreateBuffer(name, new BufferDesc
        {
            Size = (ulong)Marshal.SizeOf<T>(),
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        graph.AddPass(
            $"Upload{name}",
            builder => { builder.Write(handle, ResourceState.ConstantBuffer); },
            rgCtx =>
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
    }
}
