using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using Friflo.Engine.ECS;
using SomeEngine.Assets.Schema;
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
    private readonly MaterialSystem _materialSystem;
    private readonly ClusterUploadStage _uploadStage;
    private readonly ClusterBVHTraversePass _bvhTraversePass;
    private readonly ClusterStreamer _clusterStreamer;
    private readonly PingPongHandle _hizPingPong = new();
    private readonly GlobalPsoCache _psoCache;
    private readonly BinSpace _binSpace = new();
    public BinSpace BinSpace => _binSpace;
    private int _rasterBinFieldIndex,
        _shadingBinFieldIndex,
        _vertexEvalFieldIndex;

    internal ClusterDebugReadbackPass? _debugReadbackPass;
    private ShadePSOGroup[] _shadePSOGroups = [];
    private ShadePSOGroup[] _swRasterPSOGroups = [];
    private ShadePSOGroup[] _deformPSOGroups = [];
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
    public bool UseSWRaster { get; set; } = true;

    private bool _useDeformCache = true;
    public bool UseDeformCache
    {
        get => _useDeformCache;
        set
        {
            if (_binSpace.IsFrozen && value != _useDeformCache)
                throw new InvalidOperationException(
                    "Cannot change UseDeformCache after Initialize()."
                );
            _useDeformCache = value;
        }
    }

    public bool DebugClusterID
    {
        get => DebugMode == ClusterDebugMode.ClusterID;
        set
        {
            if (value)
                DebugMode = ClusterDebugMode.ClusterID;
            else if (DebugMode == ClusterDebugMode.ClusterID)
                DebugMode = ClusterDebugMode.None;
        }
    }

    public bool DebugLOD
    {
        get => DebugMode == ClusterDebugMode.LODLevel;
        set
        {
            if (value)
                DebugMode = ClusterDebugMode.LODLevel;
            else if (DebugMode == ClusterDebugMode.LODLevel)
                DebugMode = ClusterDebugMode.None;
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
        set
        {
            if (value && !_freezeCullingCamera)
                _frozenCamera = _camera;
            _freezeCullingCamera = value;
        }
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

    /// <summary>HiZ texture RenderGraph handle (valid after AddPasses). Use RenderGraph.GetPhysicalTexture() to resolve.</summary>
    public RenderGraphHandle LastHiZTextureHandle { get; private set; }

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
        string name,
        RenderContext context,
        ClusterResourceManager clusterMgr,
        InstanceDataManager instanceMgr,
        MaterialSystem materialSystem,
        GlobalPsoCache psoCache,
        bool includeTransparent
    )
    {
        Name = name;
        _context = context;
        _clusterMgr = clusterMgr;
        _instanceMgr = instanceMgr;
        _materialSystem = materialSystem;
        _psoCache = psoCache;
        IncludeTransparentPass = includeTransparent;
        _clusterStreamer = new ClusterStreamer(clusterMgr);
        _uploadStage = new ClusterUploadStage(context, clusterMgr, instanceMgr);
        _bvhTraversePass = new ClusterBVHTraversePass(
            context,
            clusterMgr,
            instanceMgr,
            faults =>
            {
                _clusterStreamer.EnqueueFaultNodes(faults);
                _clusterStreamer.Update();
            }
        );
    }

    public static ClusterPipeline Opaque(
        RenderContext ctx,
        ClusterResourceManager cm,
        InstanceDataManager im,
        MaterialSystem materialSystem,
        GlobalPsoCache pc
    ) => new("ClusterPipeline.Opaque", ctx, cm, im, materialSystem, pc, false);

    public static ClusterPipeline OpaqueAndTransparent(
        RenderContext ctx,
        ClusterResourceManager cm,
        InstanceDataManager im,
        MaterialSystem materialSystem,
        GlobalPsoCache pc
    ) => new("ClusterPipeline.OpaqueAndTransparent", ctx, cm, im, materialSystem, pc, true);

    // ─── Public API ───

    public void SetCamera(
        in Matrix4x4 view,
        in Matrix4x4 proj,
        Vector3 cameraPos,
        float lodThreshold = 1.0f,
        float lodScale = 500.0f,
        int forcedLODLevel = -1
    )
    {
        uint sw = _context.SwapChain?.GetDesc().Width ?? 1;
        uint sh = _context.SwapChain?.GetDesc().Height ?? 1;
        _camera = ClusterCameraData.Default(view, proj, cameraPos, sw, sh) with
        {
            LodThreshold = lodThreshold,
            LodScale = lodScale,
            ForcedLODLevel = forcedLODLevel,
            PrevViewProj = Matrix4x4.Transpose(_prevViewProjT),
            PrevView = _prevView,
            PrevProj = _prevProj,
        };
    }

    public void Initialize(RenderContext context)
    {
        _uploadStage.Init();
        _bvhTraversePass.Init();
        _debugReadbackPass = new ClusterDebugReadbackPass(context);
        _rasterBinFieldIndex = _binSpace.RegisterField("RasterBin");
        _shadingBinFieldIndex = _binSpace.RegisterField("ShadingBin");

        if (UseDeformCache)
            _vertexEvalFieldIndex = _binSpace.RegisterField("VertexEval");
        else
            _vertexEvalFieldIndex = -1;

        _binSpace.RegisterGroup(
            _rasterBinFieldIndex,
            new BinQueue.BinGroup
            {
                Query = QueryRasterEntities,
                OrderKey = static _ => 0,
                SignatureFunc = entity => MaterialEntityUtility.ComputeMaterialSignature(entity, SelectRasterVariant(entity)),
            });

        _binSpace.RegisterGroup(
            _shadingBinFieldIndex,
            new BinQueue.BinGroup
            {
                Query = QueryPrimaryShadeEntities,
                OrderKey = static _ => 0,
                SignatureFunc = entity => MaterialEntityUtility.ComputeMaterialSignature(entity, SelectShadeVariant(entity)),
            });

        _binSpace.RegisterGroup(
            _shadingBinFieldIndex,
            new BinQueue.BinGroup
            {
                Query = QueryOverlayShadeEntities,
                OrderKey = entity => entity.GetComponent<OverlayShade>().Layer + 1,
                SignatureFunc = entity => MaterialEntityUtility.ComputeMaterialSignature(entity, SelectShadeVariant(entity)),
            });

        _binSpace.RegisterGroup(
            _shadingBinFieldIndex,
            new BinQueue.BinGroup
            {
                Query = QueryMaskedShadeEntities,
                OrderKey = static _ => 10000,
                SignatureFunc = entity => MaterialEntityUtility.ComputeMaterialSignature(entity, SelectShadeVariant(entity)),
            });

        if (UseDeformCache)
        {
            _binSpace.RegisterGroup(
                _vertexEvalFieldIndex,
                new BinQueue.BinGroup
                {
                    Query = QueryDeformEntities,
                    OrderKey = static _ => 0,
                    SignatureFunc = entity => MaterialEntityUtility.ComputeMaterialSignature(entity, SelectDeformVariant(entity)),
                });
        }

        _binSpace.FreezeLayout();
    }

    // ─── PSO Group Management ───

    private void RebuildPSOGroups()
    {
        _lastBinSpaceVersion = _binSpace.Version;

        // Shade PSO groups (per-material with Sig1/SRB)
        _shadePSOGroups = ClusterShade.BuildPSOGroups(
            _binSpace,
            _shadingBinFieldIndex,
            _psoCache,
            _context
        );

        // SW Raster PSO groups (from raster bin field)
        _swRasterPSOGroups = RasterPSOBuilder.BuildComputePSOGroups(
            _binSpace,
            _rasterBinFieldIndex,
            _psoCache,
            _context,
            "SWRaster",
            SelectRasterVariant
        );

        // Deform PSO groups (from vertex eval bin field)
        if (UseDeformCache && _vertexEvalFieldIndex >= 0)
        {
            _deformPSOGroups = RasterPSOBuilder.BuildComputePSOGroups(
                _binSpace,
                _vertexEvalFieldIndex,
                _psoCache,
                _context,
                "Deform",
                SelectDeformVariant
            );
        }
        else
        {
            _deformPSOGroups = [];
        }
    }

    // ─── Main pipeline assembly ───

    public void AddPasses(RenderGraph graph)
    {
        _clusterStreamer.Update();

        var colorTarget = graph.GetResourceHandle("ColorTarget");
        var depthTarget = graph.GetResourceHandle("DepthTarget");
        var camera = _freezeCullingCamera ? _frozenCamera : _camera;

        // Reset HiZ history on mode change
        if (HiZMode != _prevHiZMode)
        {
            _hizPingPong.Reset();
            _prevHiZMode = HiZMode;
        }

        // Prepare
        _binSpace.RebuildIfDirty();
        if (_binSpace.Version != _lastBinSpaceVersion)
            RebuildPSOGroups();
        var globals = _uploadStage.AddPasses(graph);
        LastGlobalResources = globals;

        // DrawUniforms
        var hDrawUB = ClusterStageUtils.AddDynamicUniformPass(
            graph,
            "DrawUniforms",
            new DrawUniforms
            {
                ViewProj = Matrix4x4.Transpose(camera.View * camera.Proj),
                View = Matrix4x4.Transpose(camera.View),
                PageTableSize = _clusterMgr.PageCount,
                DebugMode = (uint)DebugMode,
                ScreenWidth = camera.ScreenWidth,
                ScreenHeight = camera.ScreenHeight,
                QuantOrigin = _clusterMgr.QuantOrigin,
                QuantStep = _clusterMgr.QuantStep,
            }
        );

        // Traverse
        var traverseOut = ClusterTraverse.AddPasses(
            graph,
            _context,
            _bvhTraversePass,
            _clusterMgr,
            _instanceMgr,
            globals,
            camera,
            ClusterTraverseConfig.Default()
        );

        var hMaterialSlots = _binSpace.AddUploadPass(graph);
        graph.MarkOutput(colorTarget);

        // Cull + RasterBin + Draw (via HiZ 2-Phase)
        var hizResult = ClusterHiZ.Add2PhasePipeline(
            graph,
            _context,
            traverseOut,
            globals,
            camera,
            hDrawUB,
            hMaterialSlots,
            _binSpace,
            _rasterBinFieldIndex,
            _vertexEvalFieldIndex,
            _hizPingPong,
            depthTarget,
            new ClusterHiZ.HiZConfig
            {
                HiZMode = HiZMode,
                DebugMode = DebugMode,
                Wireframe = WireframeEnabled,
                Overdraw = OverdrawEnabled,
                DebugShowHiZAABBs = DebugShowHiZAABBs,
                DumpNextFrame = DumpNextFrame,
                UseSWRaster = UseSWRaster,
                UseDeformCache = UseDeformCache,
                QuantStep = _clusterMgr.QuantStep,
                QuantOrigin = _clusterMgr.QuantOrigin,
            },
            (uint)_instanceMgr.Count,
            deformPSOGroups: _deformPSOGroups,
            swRasterPSOGroups: _swRasterPSOGroups
        );
        LastCullOutput = hizResult.Cull;
        LastOpaqueRasterOutput = hizResult.Raster;
        LastHiZTextureHandle = hizResult.HiZTexture;

        // Shade
        if (UseVisBuffer)
        {
            var camPos = _freezeCullingCamera ? _frozenCamera.CameraPos : _camera.CameraPos;
            var (shadeBinOut, shadeOut) = ClusterShade.AddPasses(
                graph,
                _context,
                LastOpaqueRasterOutput,
                hizResult.Cull,
                globals,
                hDrawUB,
                hMaterialSlots,
                _shadePSOGroups,
                colorTarget,
                depthTarget,
                _binSpace,
                _shadingBinFieldIndex,
                CountMaterialEntities(),
                _camera.View,
                _camera.Proj,
                camPos,
                _clusterMgr.PageCount,
                _clusterMgr.QuantOrigin,
                _clusterMgr.QuantStep,
                DebugMode,
                camera.ScreenWidth,
                camera.ScreenHeight,
                hizResult.DeformCache,
                hizResult.CacheOffsets
            );
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
        if (DumpNextFrame)
            DumpNextFrame = false;
        if (ClusterSWDraw.DebugDumpNextFrame)
            ClusterSWDraw.DebugDumpNextFrame = false;
    }

    public void Dispose()
    {
        _uploadStage.Dispose();
        _bvhTraversePass.Dispose();
        _binSpace.Dispose();
    }

    private Entity[] QueryRasterEntities()
    {
        return ToEntityArray(_materialSystem.Store.Query<ClusterRaster>().ToEntityList());
    }

    private Entity[] QueryPrimaryShadeEntities()
    {
        return ToEntityArray(
            _materialSystem.Store
                .Query<ClusterShadeComponent>()
                .AllTags(Tags.Get<Opaque>())
                .WithoutAllComponents(ComponentTypes.Get<OverlayShade>())
                .ToEntityList());
    }

    private Entity[] QueryOverlayShadeEntities()
    {
        return ToEntityArray(_materialSystem.Store.Query<ClusterShadeComponent, OverlayShade>().ToEntityList());
    }

    private Entity[] QueryMaskedShadeEntities()
    {
        return ToEntityArray(
            _materialSystem.Store
                .Query<ClusterShadeComponent>()
                .AllTags(Tags.Get<Masked>())
                .WithoutAllComponents(ComponentTypes.Get<OverlayShade>())
                .ToEntityList());
    }

    private Entity[] QueryDeformEntities()
    {
        return ToEntityArray(_materialSystem.Store.Query<ClusterDeform>().ToEntityList());
    }

    private uint CountMaterialEntities()
    {
        uint count = 0;
        foreach (var _ in _materialSystem.Store.Query<MaterialRef>().ToEntityList())
        {
            count++;
        }

        return count;
    }

    private ShaderVariantRef SelectRasterVariant(Entity entity)
    {
        var raster = entity.GetComponent<ClusterRaster>();
        if (UseDeformCache && !raster.SWCached.IsEmpty)
        {
            return raster.SWCached;
        }

        if (!raster.SWInline.IsEmpty)
        {
            return raster.SWInline;
        }

        return raster.SWCached;
    }

    private static ShaderVariantRef SelectShadeVariant(Entity entity)
    {
        return entity.GetComponent<ClusterShadeComponent>().Default;
    }

    private static ShaderVariantRef SelectDeformVariant(Entity entity)
    {
        return entity.GetComponent<ClusterDeform>().Default;
    }

    private static Entity[] ToEntityArray(EntityList entityList)
    {
        var entities = new Entity[entityList.Count];
        int index = 0;
        foreach (var entity in entityList)
        {
            entities[index++] = entity;
        }

        return entities;
    }
}
