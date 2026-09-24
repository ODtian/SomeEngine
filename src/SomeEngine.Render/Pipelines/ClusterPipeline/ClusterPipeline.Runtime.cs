using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Data;
using SomeEngine.Core.Collections;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Render;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Data;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;
using SomeECS.Core;
using SomeECS.Core.Queries;

namespace SomeEngine.Render.Pipelines;

internal readonly record struct InstanceCounts(int Count, int MaxDepth);

public sealed partial class ClusterPipeline : IDisposable
{
    private readonly RenderContext _context;
    private readonly AssetStore _assets;
    private readonly ClusterMeshes _meshes;
    private readonly Dictionary<string, Handle<Mesh>> _meshHandles = new(StringComparer.Ordinal);
    private readonly Dictionary<Handle<Mesh>, uint> _meshRoots = [];
    private readonly PageFaults _pageFaults = new();
    private readonly PageStream _pageStream;
    private readonly ClusterGpuResources _gpuResources;
    private readonly InstanceGpu _instanceGpu;
    private readonly MaterialItems _materials;
    private readonly PipelineSourceLease _materialsSource;
    private readonly UniformGpu<DrawUniforms> _drawUniforms;
    private ClusterSceneStage _sceneStage;
    private ClusterRasterStage _rasterStage;
    private ClusterShadeStage _shadeStage;
    private ClusterOutputStage _outputStage;
    private BufferHandle _clusterReadOffsetArgsZero;
    private readonly FlatDictionary<BufferViewKey, BufferViewHandle> _clusterReadOffsetArgsZeroViews = new();
    private readonly MaterialFallbackResources _materialFallbacks = new();
    private readonly ClusterCamera _camera = new();
    private readonly ClusterOptions _options = new();

    private bool _framePrepared;
    private bool _rootsReady;
    private uint _rootsShapeVersion;
    private bool _instanceCountsReady;
    private uint _instanceCountsShapeVersion;
    private int _instanceCountsConfiguredDepth;
    private InstanceCounts _instanceCounts;
    private RenderGraphHandle _debugIndirectDrawArgs;
    private RenderGraphHandle _debugCandidateCount;
    private RenderGraphHandle _debugCandidateArgs;
    private RenderGraphHandle _debugPhase2CandidateCount;
    private RenderGraphHandle _debugPhase2DrawArgs;
    private readonly List<BufferCopyRequest> _uploadCopyRequests = [];

    private ClusterPipeline(
        string name,
        RenderContext context,
        ClusterShaders shaders,
        AssetStore assets)
    {
        Name = name;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _meshes = new ClusterMeshes();
        _pageStream = new PageStream(_meshes);
        IDevice device = context.GraphicsDevice ?? throw new InvalidOperationException("cluster render requires an initialized render context.");
        _gpuResources = new ClusterGpuResources(device, _meshes);
        _instanceGpu = new InstanceGpu(device);
        _drawUniforms = new UniformGpu<DrawUniforms>(device);

        _materials = new MaterialItems(context, _assets, device);
        _sceneStage = new ClusterSceneStage(
            context,
            ShaderFor(shaders.BvhPatch),
            ShaderFor(shaders.Traverse),
            ShaderFor(shaders.Cull));
        _rasterStage = new ClusterRasterStage(
            new RasterBinPass(
                context,
                ShaderFor(shaders.Binning)),
            new ClusterDeformPass(context),
            new SwRasterPass(context),
            new ClusterDrawPass(
                context,
                ShaderFor(shaders.Draw)),
            new DepthMergePass(
                context,
                ShaderFor(shaders.DepthMerge)),
            new HiZPass(
                context,
                ShaderFor(shaders.HiZ)));
        _shadeStage = new ClusterShadeStage(
            new ShadeBinPass(
                context,
                ShaderFor(shaders.ShadeBinning)),
            new MaterialShadePass(context));
        _outputStage = new ClusterOutputStage(
            new ClusterResolvePass(
                context,
                ShaderFor(shaders.Resolve)),
            new TemporalResolvePass(
                context,
                _assets.Get(shaders.Temporal)));
        _materialsSource = context.AddPipelineSource(_materials);
        WarmupBuiltins();
    }

    public string Name { get; }

    public HiZMode HiZMode { get => Edit.HiZ; set => Edit.HiZ = value; }
    public ClusterDebugMode DebugMode { get => Edit.Debug; set => Edit.Debug = value; }
    public bool UseVisBuffer { get => Edit.UseVis; set => Edit.UseVis = value; }
    public bool BypassCulling { get => Edit.BypassCull; set => Edit.BypassCull = value; }
    public bool UseSWRaster { get => Edit.UseSwRaster; set => Edit.UseSwRaster = value; }
    public bool UseDeformCache { get => Edit.UseDeformCache; set => Edit.UseDeformCache = value; }
    public ulong DeformCacheBytes { get => Edit.DeformCacheBytes; set => Edit.DeformCacheBytes = value; }
    public int MaterialPipelineBudget { get => Edit.MaterialPipelineBudget; set => Edit.MaterialPipelineBudget = Math.Max(0, value); }
    public bool TemporalResolveEnabled { get => Edit.TemporalResolve; set => Edit.TemporalResolve = value; }
    public bool TemporalJitterEnabled { get => Edit.TemporalJitter; set => Edit.TemporalJitter = value; }
    public TemporalResolveSettings TemporalResolveSettings { get => Edit.TemporalSettings; set => Edit.TemporalSettings = value; }
    public Vector2 TemporalJitterPixels { get => Edit.JitterPixels; set => Edit.JitterPixels = value; }
    public PipelineWarmup LastWarmup { get; private set; } = PipelineWarmup.Empty;

    private ClusterConfig Edit => _options.Edit;

    private void WarmupBuiltins()
    {
        var tickets = new List<PipelineTicket>();
        _sceneStage.AddTickets(tickets);
        _rasterStage.AddTickets(tickets);
        _shadeStage.AddTickets(tickets);
        _outputStage.AddTickets(tickets);
        _context.WaitRequired(CollectionsMarshal.AsSpan(tickets));
    }

    private PipelineWarmup WarmupPipelines(int budget)
    {
        if (budget > 0)
        {
            return _context.WarmupSources(budget);
        }

        return _context.RefreshSources();
    }

    public PipelineWarmup WarmupPipelines()
    {
        LastWarmup = _context.WarmupSources(int.MaxValue);
        return LastWarmup;
    }

    public bool FreezeCullingCamera
    {
        get => _camera.Frozen;
        set => _camera.Freeze(value);
    }

    public uint FaultCount => _pageStream.FaultCount;
    public uint LoadedPages => _pageStream.LoadedPages;
    public uint PageCount => _meshes.PageCount;
    public uint ResidentPageCount => _meshes.ResidentPageCount;
    public int MeshCount => _meshes.MeshCount;
    public uint MaterialVersion => _materials.Version;
    public int MaterialSlotCapacity => _materials.SlotCapacity;
    public int RasterMaterialCount => _materials.RasterCount;
    public int LastInstanceUploadCount => _instanceGpu.LastUploadCount;
    public int LastInstanceUploadBytes => _instanceGpu.LastUploadBytes;
    internal RenderGraphHandle DebugIndirectDrawArgs => _debugIndirectDrawArgs;
    internal RenderGraphHandle DebugCandidateCount => _debugCandidateCount;
    internal RenderGraphHandle DebugCandidateArgs => _debugCandidateArgs;
    internal RenderGraphHandle DebugPhase2CandidateCount => _debugPhase2CandidateCount;
    internal RenderGraphHandle DebugPhase2DrawArgs => _debugPhase2DrawArgs;
    public IEnumerable<KeyValuePair<string, Handle<Mesh>>> Meshes => _meshHandles;

    public InstanceUploadState GetInstanceUploadState(RenderWorld renderWorld)
    {
        ArgumentNullException.ThrowIfNull(renderWorld);
        return _instanceGpu.GetUploadState(renderWorld, _materials.Headers);
    }

    public static ClusterPipeline Opaque(
        RenderContext ctx,
        ClusterShaders shaders,
        AssetStore assets)
        => new("ClusterPipeline.Opaque", ctx, shaders, assets);

    public void Initialize(RenderContext context)
    {
        if (!ReferenceEquals(context, _context))
            throw new InvalidOperationException("ClusterPipeline was initialized with a different render context.");
        if (_context.GraphicsDevice == null || _context.GraphicsQueue == null)
            throw new InvalidOperationException("ClusterPipeline requires an initialized RHI render context.");
    }

    public void PrepareFrame(
        RenderWorld renderWorld,
        ViewHistory histories,
        CameraHistory cameraHistory,
        TemporalState temporalState)
    {
        ArgumentNullException.ThrowIfNull(renderWorld);
        ArgumentNullException.ThrowIfNull(histories);
        ArgumentNullException.ThrowIfNull(cameraHistory);
        ArgumentNullException.ThrowIfNull(temporalState);

        _framePrepared = false;
        _options.Seal();
        ClusterConfig modes = _options.Frame;

        if (temporalState.ConsumeReset())
        {
            histories.ResetAll();
            cameraHistory.Reset();
        }

        using (Profiler.BeginScope("ClusterPipeline.PageStream.Update"))
        {
            _pageStream.Update();
        }

        using (Profiler.BeginScope("ClusterPipeline.MaterialFallbacks.PrepareFrame"))
        {
            _materialFallbacks.EnsureInitialized(_context);
        }

        using (Profiler.BeginScope("ClusterPipeline.Materials.PrepareFrame"))
        {
            _materials.PrepareFrame(renderWorld, modes.UseDeformCache);
        }

        using (Profiler.BeginScope("ClusterPipeline.Pipelines.Warmup"))
        {
            LastWarmup = WarmupPipelines(modes.MaterialPipelineBudget);
        }

        using (Profiler.BeginScope("ClusterPipeline.WriteRoots"))
        {
            WriteRoots(renderWorld, _materials.Headers);
        }

        using (Profiler.BeginScope("ClusterPipeline.Instances.PrepareFrame"))
        {
            _instanceGpu.PrepareFrame(renderWorld, _materials.Headers);
        }

        using (Profiler.BeginScope("ClusterPipeline.Scan"))
        {
            _instanceCounts = Scan(renderWorld, ClusterLimits.DefaultTraverseDepth);
        }

        _framePrepared = true;
    }

    public void SetCamera(
        in Matrix4x4 view,
        in Matrix4x4 proj,
        Vector3 cameraPos,
        CameraHistory history,
        float lodThreshold = 1.0f,
        float lodScale = 500.0f,
        int forcedLODLevel = -1)
        => SetCamera(view, proj, view * proj, cameraPos, history, lodThreshold, lodScale, forcedLODLevel);

    public void SetCamera(
        in Matrix4x4 view,
        in Matrix4x4 proj,
        in Matrix4x4 motionViewProj,
        Vector3 cameraPos,
        CameraHistory history,
        float lodThreshold = 1.0f,
        float lodScale = 500.0f,
        int forcedLODLevel = -1)
    {
        ArgumentNullException.ThrowIfNull(history);
        var swapchain = _context.GraphicsSwapchain
            ?? throw new InvalidOperationException("ClusterPipeline camera setup requires an initialized swapchain.");
        _camera.Set(
            view,
            proj,
            motionViewProj,
            cameraPos,
            lodThreshold,
            lodScale,
            forcedLODLevel,
            swapchain.Width,
            swapchain.Height,
            history);
    }

    public FrameOutputs AddPasses(
        RenderGraph graph,
        RenderWorld renderWorld,
        SceneTextures sceneTextures,
        ViewHistory histories,
        CameraHistory cameraHistory,
        TemporalState temporalState,
        SceneLights? sceneLights = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(renderWorld);
        ArgumentNullException.ThrowIfNull(histories);
        ArgumentNullException.ThrowIfNull(cameraHistory);
        ArgumentNullException.ThrowIfNull(temporalState);
        graph.UsePipelineCache(_context.PipelineCache);
        if (!_framePrepared)
            throw new InvalidOperationException("ClusterPipeline.PrepareFrame must be called before AddPasses.");
        ClusterConfig modes = _options.Frame;
        SceneLights currentLights = sceneLights ?? renderWorld.SceneLights;
        uint currentLightVersion = sceneLights.HasValue ? 0u : renderWorld.LightVersion;
        IDevice device = _context.GraphicsDevice
            ?? throw new InvalidOperationException("ClusterPipeline.AddPasses requires an initialized RHI device.");

        var camera = _camera.Active;
        uint width = Math.Max(sceneTextures.View.Width, 1u);
        uint height = Math.Max(sceneTextures.View.Height, 1u);
        uint hiZMipCount = HiZPass.CalculateMipCount(width, height);
        Vector2 hiZInvSize = new(1.0f / width, 1.0f / height);
        bool useHiZHistory = modes.UseHiZ();
        _debugIndirectDrawArgs = RenderGraphHandle.Invalid;
        _debugCandidateCount = RenderGraphHandle.Invalid;
        _debugCandidateArgs = RenderGraphHandle.Invalid;
        _debugPhase2CandidateCount = RenderGraphHandle.Invalid;
        _debugPhase2DrawArgs = RenderGraphHandle.Invalid;
        RenderGraphHandle currentHiZ = RenderGraphHandle.Invalid;
        RenderGraphHandle phase1HiZ;
        bool hasHiZHistory = false;
        if (useHiZHistory)
        {
            var hiZHistory = histories.ImportHiZ(
                graph,
                device,
                HiZDesc(width, height, hiZMipCount),
                ResourceState.ShaderResource);
            currentHiZ = hiZHistory.Current;
            hasHiZHistory = hiZHistory.HasPrevious && hiZHistory.Previous.IsValid;
            phase1HiZ = hasHiZHistory
                ? hiZHistory.Previous
                : HiZSeed(graph, width, height, hiZMipCount);
        }
        else
        {
            hiZMipCount = 1;
            phase1HiZ = HiZSeed(graph, width, height, hiZMipCount);
        }

        SlotFrame slots;
        using (Profiler.BeginScope("ClusterPipeline.Slots.AddPasses"))
        {
            slots = _materials.RecordFrame(graph);
        }
        InstanceCounts stats = _instanceCounts;

        ClusterBuffers buffers;
        InstanceFrame instances;
        _uploadCopyRequests.Clear();
        using (Profiler.BeginScope("ClusterPipeline.UploadStage.AddPasses"))
        {
            using (Profiler.BeginScope("ClusterPipeline.UploadStage.Resources"))
            {
                buffers = _gpuResources.AddUploadPasses(graph);
            }

            using (Profiler.BeginScope("ClusterPipeline.UploadStage.ScenePatches"))
            {
                _sceneStage.AddPatches(graph, buffers.GlobalBVH, _meshes.TakePatches());
            }

            using (Profiler.BeginScope("ClusterPipeline.UploadStage.Instances"))
            {
                instances = _instanceGpu.RecordFrame(graph, renderWorld, _materials.Headers, _uploadCopyRequests);
            }
        }

        DrawUniforms drawData = MakeDraw(camera);
        UniformFrame drawUniforms = _drawUniforms.Add(graph, [drawData], "DrawUniforms");
        var cullingUniforms = CullingUniforms.Create(
            camera.View,
            camera.Proj,
            camera.CameraPos,
            camera.LodThreshold,
            camera.LodScale,
            camera.ForcedLODLevel,
            checked((uint)stats.Count),
            modes.BypassCull,
            cameraHistory.PrevViewProjT,
            hasHiZHistory,
            hiZMipCount,
            hiZInvSize,
            cameraHistory.PrevView,
            cameraHistory.PrevProj,
            width,
            height);

        ClusterTraverseOutput traverse;
        using (Profiler.BeginScope("ClusterPipeline.Traverse.AddPasses"))
        {
            traverse = _sceneStage.AddTraverse(
                graph,
                buffers,
                instances,
                cullingUniforms,
                stats.Count,
                stats.MaxDepth,
                _uploadCopyRequests);
        }
        _debugIndirectDrawArgs = traverse.IndirectDrawArgs;
        _debugCandidateCount = traverse.CandidateCount;
        _debugCandidateArgs = traverse.CandidateArgs;
        ClusterCullOutput cull;
        using (Profiler.BeginScope("ClusterPipeline.Cull.Phase1.AddPasses"))
        {
            cull = _sceneStage.AddCull1(
                graph,
                buffers,
                instances,
                traverse,
                phase1HiZ,
                enablePhase2: modes.UsePhase2());
        }
        _debugPhase2CandidateCount = cull.Phase2CandidateCount;
        _debugPhase2DrawArgs = cull.Phase2DrawArgs;
        RenderGraphHandle clusterReadOffsetArgs = ZeroRaw(graph, "ClusterReadOffsetArgs", 16);
        bool hasDeformWork = modes.UseDeform()
            && _materials.Deform.Count > 0
            && slots.VertexEvalFieldIndex != uint.MaxValue;
        DeformCacheResources deformCacheResources = default;
        RenderGraphHandle deformCache = RenderGraphHandle.Invalid;
        RenderGraphHandle cacheOffsets = RenderGraphHandle.Invalid;
        RasterBinFrame rasterBinP1;
        if (hasDeformWork)
        {
            deformCacheResources = CreateDeformCache(graph, modes.DeformCacheBytes);
            deformCache = deformCacheResources.DeformCache;
            cacheOffsets = deformCacheResources.CacheOffsets;

            RasterDeformBins combinedBin;
            using (Profiler.BeginScope("ClusterPipeline.RasterDeformBin.P1.AddPasses"))
            {
                combinedBin = _rasterStage.AddDeformBins(
                    graph,
                    buffers,
                    instances,
                    cull,
                    cull.DrawArgs,
                    clusterReadOffsetArgs,
                    slots,
                    deformCacheResources,
                    resetAllocation: true,
                    tag: "P1");
                rasterBinP1 = combinedBin.Raster;
            }

            using (Profiler.BeginScope("ClusterPipeline.Deform.P1.AddPasses"))
            {
                _rasterStage.AddDeform(
                    graph,
                    deformCacheResources,
                    combinedBin.Deform,
                    buffers,
                    instances,
                    cull,
                    cull.DrawArgs,
                    clusterReadOffsetArgs,
                    _materials.Deform,
                    _materialFallbacks.Fallbacks,
                    resetAllocation: true,
                    tag: "P1");
            }
        }
        else
        {
            using (Profiler.BeginScope("ClusterPipeline.RasterBin.P1.AddPasses"))
            {
                rasterBinP1 = _rasterStage.AddBins(
                    graph,
                    buffers,
                    instances,
                    cull,
                    cull.DrawArgs,
                    clusterReadOffsetArgs,
                    slots,
                    "P1");
            }
        }

        ClusterRasterOutput rasterP1;
        using (Profiler.BeginScope("ClusterPipeline.Raster.P1.AddPasses"))
        {
            rasterP1 = _rasterStage.AddRaster(
                graph,
                buffers,
                instances,
                cull,
                rasterBinP1,
                drawData,
                drawUniforms,
                width,
                height,
                sceneTextures.SceneDepth,
                outputVisBuffer: default,
                outputDepthUav: default,
                deformCache: deformCache,
                cacheOffsets: cacheOffsets,
                modes: modes,
                swStates: _materials.Sw,
                drawStates: _materials.Draw,
                fallbacks: _materialFallbacks.Fallbacks,
                clearTargets: true,
                tag: "P1");
        }

        HiZOutput hiZ = default;
        RenderGraphHandle hiZTexture = RenderGraphHandle.Invalid;
        ClusterRasterOutput raster = rasterP1;
        if (modes.BuildHiZ())
        {
            using (Profiler.BeginScope("ClusterPipeline.HiZ.Phase1.AddPasses"))
            {
                hiZ = _rasterStage.AddHiZ(graph, rasterP1.DepthTarget, width, height, currentHiZ, "Phase1");
                hiZTexture = hiZ.Texture;
            }
        }

        if (modes.UsePhase2() && hiZTexture.IsValid)
        {
            using (Profiler.BeginScope("ClusterPipeline.Cull.Phase2.AddPasses"))
            {
                _sceneStage.AddCull2(graph, buffers, instances, traverse, cull, hiZTexture);
            }

            RasterBinFrame rasterBinP2;
            if (hasDeformWork)
            {
                RasterDeformBins combinedBin;
                using (Profiler.BeginScope("ClusterPipeline.RasterDeformBin.P2.AddPasses"))
                {
                    combinedBin = _rasterStage.AddDeformBins(
                        graph,
                        buffers,
                        instances,
                        cull,
                        cull.Phase2DrawArgs,
                        cull.DrawArgs,
                        slots,
                        deformCacheResources,
                        resetAllocation: false,
                        tag: "P2");
                    rasterBinP2 = combinedBin.Raster;
                }

                using (Profiler.BeginScope("ClusterPipeline.Deform.P2.AddPasses"))
                {
                    _rasterStage.AddDeform(
                        graph,
                        deformCacheResources,
                        combinedBin.Deform,
                        buffers,
                        instances,
                        cull,
                        cull.Phase2DrawArgs,
                        cull.DrawArgs,
                        _materials.Deform,
                        _materialFallbacks.Fallbacks,
                        resetAllocation: false,
                        tag: "P2");
                }
            }
            else
            {
                using (Profiler.BeginScope("ClusterPipeline.RasterBin.P2.AddPasses"))
                {
                    rasterBinP2 = _rasterStage.AddBins(
                        graph,
                        buffers,
                        instances,
                        cull,
                        cull.Phase2DrawArgs,
                        cull.DrawArgs,
                        slots,
                        "P2");
                }
            }

            using (Profiler.BeginScope("ClusterPipeline.Raster.P2.AddPasses"))
            {
                raster = _rasterStage.AddRaster(
                    graph,
                    buffers,
                    instances,
                    cull,
                    rasterBinP2,
                    drawData,
                    drawUniforms,
                    width,
                    height,
                    sceneTextures.SceneDepth,
                    rasterP1.VisBuffer,
                    rasterP1.RasterDepth,
                    deformCache,
                    cacheOffsets,
                    modes,
                    _materials.Sw,
                    _materials.Draw,
                    _materialFallbacks.Fallbacks,
                    clearTargets: false,
                    tag: "P2");
            }

            using (Profiler.BeginScope("ClusterPipeline.HiZ.Final.AddPasses"))
            {
                _rasterStage.AddHiZ(graph, raster.DepthTarget, width, height, hiZTexture, "Final");
            }
        }

        using (Profiler.BeginScope("ClusterPipeline.PageFaultReadback.AddPass"))
        {
            _uploadCopyRequests.Clear();
            AddFaults(graph, buffers, _uploadCopyRequests);
        }

        RenderGraphHandle motionVectors = graph.CreateTexture(
            FrameResources.MotionVectors,
            FrameResources.MotionVectorTexture(sceneTextures.View));
        ShadeBinFrame shadeBin = default;
        ClusterShadeOutput shade;
        if (modes.UseShade())
        {
            if (_materials.Shade.Count == 0 && _materials.RasterCount > 0)
            {
                throw new InvalidOperationException(
                    "cluster material shade requires at least one material pass state when active raster features exist.");
            }

            using (Profiler.BeginScope("ClusterPipeline.ShadeBin.AddPasses"))
            {
                shadeBin = _shadeStage.AddBins(
                    graph,
                    buffers,
                    instances,
                    raster,
                    cull,
                    slots,
                    width,
                    height);
            }

            using (Profiler.BeginScope("ClusterPipeline.MaterialShade.AddPasses"))
            {
                shade = _shadeStage.AddShade(
                    graph,
                    buffers,
                    instances,
                    raster,
                    cull,
                    shadeBin,
                    _materials.Shade,
                    _materialFallbacks.Fallbacks,
                    _assets,
                    MakeShade(camera, width, height, slots.ShadingBinCount, cameraHistory),
                    currentLights,
                    currentLightVersion,
                    width,
                    height,
                    modes.DepthSliceCount,
                    outputColor: sceneTextures.SceneColor,
                    outputMotionVectors: motionVectors,
                    deformCache: deformCache,
                    cacheOffsets: cacheOffsets,
                    clearTargets: true);
            }
        }
        else
        {
            if (_uploadCopyRequests.Count != 0)
            {
                BufferCopyPasses.AddCopyBatch(graph, "Cluster Page Fault Readback", [.. _uploadCopyRequests]);
                _uploadCopyRequests.Clear();
            }
            _outputStage.AddResolve(graph, buffers, instances, raster, cull, drawUniforms, sceneTextures.SceneColor);
            FrameResources.AddClearPass(
                graph,
                motionVectors,
                new Color(0, 0, 0, 0),
                "Clear Motion Vectors");
            shade = new ClusterShadeOutput(sceneTextures.SceneColor, motionVectors);
        }

        RenderGraphHandle postSceneColor;
        using (Profiler.BeginScope("ClusterPipeline.Temporal.AddPasses"))
        {
            postSceneColor = AddTemporalOutput(
                graph,
                sceneTextures,
                histories,
                shade.MotionVectors,
                temporalState,
                modes);
        }

        graph.Blackboard.Set(sceneTextures with
        {
            MotionVectors = shade.MotionVectors,
            PostSceneColor = postSceneColor,
            HiZ = hiZTexture,
        });

        cameraHistory.Commit(camera.View, camera.Proj, camera.MotionViewProj);
        return new FrameOutputs(
            sceneTextures.SceneColor,
            postSceneColor,
            shade.MotionVectors,
            sceneTextures.SceneDepth);
    }

    public bool RegisterMesh(Handle<Mesh> handle)
    {
        if (!handle.IsValid || !_assets.TryGet(handle, out Mesh? mesh) || mesh == null)
            return false;

        return AddMesh(handle, mesh) != uint.MaxValue;
    }

    private uint AddMesh(Handle<Mesh> handle, Mesh mesh)
    {
        if (_meshRoots.TryGetValue(handle, out uint cachedRoot))
            return cachedRoot;

        uint root = _meshes.AddMesh(handle, mesh);
        if (root != uint.MaxValue)
        {
            _meshRoots[handle] = root;
            _meshHandles[MeshPages.Key(handle)] = handle;
        }

        return root;
    }

    public uint EvictPages()
    {
        uint evicted = 0;
        for (uint page = 0; page < _meshes.PageCount; page++)
        {
            if (_meshes.EvictPage(page))
                evicted++;
        }

        return evicted;
    }

    private InstanceCounts Scan(RenderWorld renderWorld, int configuredDepth)
    {
        if (_instanceCountsReady
            && _instanceCountsShapeVersion == renderWorld.InstanceShapeVersion
            && _instanceCountsConfiguredDepth == configuredDepth)
        {
            return _instanceCounts;
        }

        if (configuredDepth <= 0)
        {
            _instanceCounts = new InstanceCounts(renderWorld.CountInstances(), configuredDepth);
            _instanceCountsReady = true;
            _instanceCountsShapeVersion = renderWorld.InstanceShapeVersion;
            _instanceCountsConfiguredDepth = configuredDepth;
            return _instanceCounts;
        }

        int count = 0;
        int maxDepth = 0;
        bool useConfig = false;
        QueryHandle instanceQuery = InstanceQuery(renderWorld.World);
        foreach (QueryChunkView chunk in renderWorld.World.RunQuery(instanceQuery).Chunks)
        {
            ReadOnlySpan<RenderInstance> chunkInstances = chunk.Read<RenderInstance>();
            count += chunkInstances.Length;
            for (int i = 0; i < chunkInstances.Length; i++)
            {
                uint root = MeshRoot(chunkInstances[i].Mesh);
                if (root == uint.MaxValue)
                    continue;
                if (!_meshes.TryDepth(root, out int depth))
                {
                    useConfig = true;
                    continue;
                }

                maxDepth = Math.Max(maxDepth, depth);
            }
        }

        int max = count == 0
            ? configuredDepth
            : useConfig
                ? configuredDepth
                : Math.Min(configuredDepth, maxDepth);
        _instanceCounts = new InstanceCounts(count, max);
        _instanceCountsReady = true;
        _instanceCountsShapeVersion = renderWorld.InstanceShapeVersion;
        _instanceCountsConfiguredDepth = configuredDepth;
        return _instanceCounts;
    }

    private void WriteRoots(RenderWorld renderWorld, InstanceHeaderData headers)
    {
        if (_rootsReady && _rootsShapeVersion == renderWorld.InstanceShapeVersion)
        {
            WriteDirtyRoots(renderWorld, headers);
            return;
        }

        QueryHandle instanceQuery = InstanceQuery(renderWorld.World);
        foreach (QueryChunkView chunk in renderWorld.World.RunQuery(instanceQuery).Chunks)
        {
            ReadOnlySpan<RenderInstance> chunkInstances = chunk.Read<RenderInstance>();
            for (int i = 0; i < chunkInstances.Length; i++)
            {
                RenderInstance instance = chunkInstances[i];
                headers.SetU32(
                    instance.InstanceIndex,
                    InstanceHeaderLayout.BVHRootIndex,
                    MeshRoot(instance.Mesh));
            }
        }

        _rootsReady = true;
        _rootsShapeVersion = renderWorld.InstanceShapeVersion;
    }

    private void WriteDirtyRoots(RenderWorld renderWorld, InstanceHeaderData headers)
    {
        QueryHandle instanceQuery = DirtyInstanceQuery(renderWorld.World);
        foreach (QueryChunkView chunk in renderWorld.World.RunQuery(instanceQuery).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                InstanceDirty dirty = chunk.Read<InstanceDirty>(row);
                if ((dirty.Flags & InstanceDirtyFlags.Header) == 0)
                    continue;

                RenderInstance instance = chunk.Read<RenderInstance>(row);
                headers.SetU32(
                    instance.InstanceIndex,
                    InstanceHeaderLayout.BVHRootIndex,
                    MeshRoot(instance.Mesh));
            }
        }
    }

    private uint MeshRoot(Handle<Mesh> handle)
    {
        if (!handle.IsValid)
            return uint.MaxValue;
        if (_meshRoots.TryGetValue(handle, out uint root))
            return root;
        return _assets.TryGet(handle, out Mesh? mesh) && mesh != null
            ? AddMesh(handle, mesh)
            : uint.MaxValue;
    }

    private static QueryHandle InstanceQuery(World world)
        => world.Query(
            new QueryDefinitionBuilder()
                .Read<RenderInstance>());

    private static QueryHandle DirtyInstanceQuery(World world)
        => world.Query(
            new QueryDefinitionBuilder()
                .Read<RenderInstance>()
                .Read<InstanceDirty>()
                .Enabled<InstanceDirty>());

    private Shader ShaderFor(Handle<Shader> handle)
        => _assets.Get(handle);

    private void AddFaults(
        RenderGraph graph,
        ClusterBuffers buffers,
        List<BufferCopyRequest>? copyRequests = null)
    {
        if (!buffers.PageFault.IsValid)
            throw new ArgumentException("cluster page fault readback requires valid page fault buffers.", nameof(buffers));
        if (!buffers.PageFaultReadback.IsValid)
            throw new ArgumentException("cluster page fault readback requires a valid persistent readback buffer.", nameof(buffers));

        var request = new BufferCopyRequest(
            buffers.PageFault,
            buffers.PageFaultReadback,
            0,
            0,
            PageFaults.ByteCount);
        if (copyRequests == null)
            BufferCopyPasses.AddCopyBatch(graph, "Cluster Page Fault Readback", [request]);
        else
            copyRequests.Add(request);
        graph.ExtractBuffer(
            buffers.PageFaultReadback,
            ResourceState.CopyDestination,
            (buffer, _) => ReadFaults(buffer));
    }

    private RenderGraphHandle AddTemporalOutput(
        RenderGraph graph,
        SceneTextures sceneTextures,
        ViewHistory histories,
        RenderGraphHandle motionVectors,
        TemporalState temporalState,
        ClusterConfig config)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(histories);
        ArgumentNullException.ThrowIfNull(temporalState);

        if (!config.UseTemporal())
        {
            temporalState.SetReady(false);
            return sceneTextures.SceneColor;
        }

        IDevice device = _context.GraphicsDevice
            ?? throw new InvalidOperationException("ClusterPipeline temporal output requires an initialized RHI device.");
        RenderHistoryTexture sceneHistory = histories.ImportTemporalSceneColor(
            graph,
            device,
            FrameResources.TemporalColorTexture(sceneTextures.View),
            ResourceState.ShaderResource);
        RenderHistoryTexture motionHistory = histories.ImportTemporalMotionVectors(
            graph,
            device,
            FrameResources.TemporalMotionTexture(sceneTextures.View),
            ResourceState.ShaderResource);
        RenderHistoryTexture depthHistory = histories.ImportTemporalSceneDepth(
            graph,
            device,
            FrameResources.TemporalDepthTexture(sceneTextures.View),
            ResourceState.ShaderResource);

        bool hasPrevious = sceneHistory.HasPrevious
            && motionHistory.HasPrevious
            && depthHistory.HasPrevious;
        temporalState.SetReady(hasPrevious);

        RenderGraphHandle postScene = sceneTextures.SceneColor;
        if (hasPrevious)
        {
            postScene = graph.CreateTexture(
                "TemporalResolvedSceneColor",
                FrameResources.SceneColorTexture(sceneTextures.View) with
                {
                    Name = "TemporalResolvedSceneColor",
                });
            _outputStage.AddTemporal(
                graph,
                sceneTextures.SceneColor,
                sceneHistory.Previous,
                motionVectors,
                motionHistory.Previous,
                sceneTextures.SceneDepth,
                depthHistory.Previous,
                postScene,
                config.TemporalSettings);
        }

        TextureCopyPasses.AddCopyBatch(
            graph,
            "Update Temporal Histories",
            [
                FullCopy(graph, postScene, sceneHistory.Current),
                FullCopy(graph, motionVectors, motionHistory.Current),
                FullCopy(graph, sceneTextures.SceneDepth, depthHistory.Current),
            ]);
        return postScene;
    }

    private static TextureCopyRequest FullCopy(
        RenderGraph graph,
        RenderGraphHandle source,
        RenderGraphHandle destination)
    {
        TextureDesc sourceDesc = graph.GetTextureDesc(source);
        TextureDesc destinationDesc = graph.GetTextureDesc(destination);
        uint width = Math.Min(sourceDesc.Width, destinationDesc.Width);
        uint height = Math.Min(sourceDesc.Height, destinationDesc.Height);
        var region = new TextureCopyRegion(0, 0, 0, 0, 0, width, height, 1);
        return new TextureCopyRequest(source, region, destination, region);
    }

    private void ReadFaults(BufferHandle readback)
    {
        var device = _context.GraphicsDevice
            ?? throw new InvalidOperationException("cluster page fault readback requires an initialized RHI device.");

        ReadOnlySpan<uint> faults = _pageFaults.Read(
            device,
            readback,
            checked((int)PageFaults.ByteCount),
            PageFaults.MaxCount);
        _pageStream.Push(faults);
    }

    public static void ResetTemporal(TemporalState temporalState)
    {
        ArgumentNullException.ThrowIfNull(temporalState);
        temporalState.RequestReset();
    }

    public void Dispose()
    {
        PipelineCache store = _context.PipelineCache
            ?? throw new InvalidOperationException("cluster pipeline requires a PipelineCache to release pipelines.");
        _outputStage.Dispose(store);
        _shadeStage.Dispose(store);
        _rasterStage.Dispose(store);
        _drawUniforms.Dispose();
        _sceneStage.Dispose(store);
        _materialsSource.Dispose();
        _materials.Dispose();
        _materialFallbacks.Dispose(_context);
        _instanceGpu.Dispose();
        _gpuResources.Dispose();
        if (_clusterReadOffsetArgsZero.IsValid && _context.GraphicsDevice != null)
        {
            var device = _context.GraphicsDevice;
            foreach (var pair in _clusterReadOffsetArgsZeroViews)
                device.Destroy(pair.Value);
            _clusterReadOffsetArgsZeroViews.Clear();
            device.Destroy(_clusterReadOffsetArgsZero);
            _clusterReadOffsetArgsZero = default;
        }
        _meshes.Dispose();
    }

    private DrawUniforms MakeDraw(in ClusterCameraData camera)
    {
        ClusterConfig modes = _options.Frame;
        return new()
        {
            ViewProj = Matrix4x4.Transpose(camera.View * camera.Proj),
            View = Matrix4x4.Transpose(camera.View),
            PageTableSize = _meshes.PageCount,
            DebugMode = (uint)modes.Debug,
            ScreenWidth = Math.Max(camera.ScreenWidth, 1u),
            ScreenHeight = Math.Max(camera.ScreenHeight, 1u),
        };
    }

    private ShadeUniforms MakeShade(
        in ClusterCameraData camera,
        uint width,
        uint height,
        uint materialCount,
        CameraHistory cameraHistory)
    {
        ArgumentNullException.ThrowIfNull(cameraHistory);
        ClusterConfig modes = _options.Frame;
        return new()
        {
            ViewProj = Matrix4x4.Transpose(camera.View * camera.Proj),
            View = Matrix4x4.Transpose(camera.View),
            PrevViewProj = Matrix4x4.Transpose(camera.PrevViewProj),
            MotionViewProj = Matrix4x4.Transpose(camera.MotionViewProj),
            PrevMotionViewProj = Matrix4x4.Transpose(camera.PrevMotionViewProj),
            PageTableSize = _meshes.PageCount,
            DebugMode = (uint)modes.Debug,
            ScreenWidth = width,
            ScreenHeight = height,
            ShadingBin = 0,
            MaterialCount = materialCount,
            LightLayerMask = SceneLights.DefaultLightLayerMask,
            CameraPos = camera.CameraPos,
            HasPreviousFrame = cameraHistory.HasPrevious ? 1u : 0u,
            WriteMotionVectors = 1u,
        };
    }

    public static TextureDesc HiZDesc(uint width, uint height, uint mipCount)
        => new()
        {
            Name = RenderHistoryNames.HiZ,
            Dimension = ResourceDimension.Texture2D,
            Width = Math.Max(width, 1u),
            Height = Math.Max(height, 1u),
            MipLevels = Math.Max(mipCount, 1u),
            Format = Format.R32Float,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess | BindFlags.CopySource,
            InitialState = ResourceState.Undefined,
        };

    private static RenderGraphHandle HiZSeed(RenderGraph graph, uint width, uint height, uint mipCount)
        => graph.CreateTexture(
            "HiZSeed",
            HiZDesc(width, height, mipCount) with
            {
                Name = "HiZSeed",
                InitialState = ResourceState.ShaderResource,
            });

    private static DeformCacheResources CreateDeformCache(RenderGraph graph, ulong requestedByteCapacity)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ulong requested = requestedByteCapacity == 0
            ? ClusterLimits.DefaultDeformBytes
            : requestedByteCapacity;
        ulong capacity = Math.Min(Math.Max(requested, 16UL), ClusterDeformPass.MaxCacheBytes);
        RenderGraphHandle cache = graph.CreateBuffer(
            "DeformCache",
            new BufferDesc
            {
                Name = "DeformCache",
                SizeInBytes = capacity,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                InitialState = ResourceState.UnorderedAccess,
                Raw = true,
            });
        RenderGraphHandle offsets = graph.CreateBuffer(
            "CacheOffsets",
            new BufferDesc
            {
                Name = "CacheOffsets",
                SizeInBytes = ClusterLimits.MaxDraws * sizeof(uint),
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                InitialState = ResourceState.UnorderedAccess,
                StrideInBytes = sizeof(uint),
            });
        RenderGraphHandle counter = CreateCounterBuffer(graph, "CacheAllocationCounter");
        return new DeformCacheResources(cache, offsets, counter, capacity);
    }

    private static RenderGraphHandle CreateCounterBuffer(RenderGraph graph, string name)
        => graph.CreateBuffer(
            name,
            new BufferDesc
            {
                Name = name,
                SizeInBytes = sizeof(uint),
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                InitialState = ResourceState.UnorderedAccess,
                StrideInBytes = sizeof(uint),
            });

    private RenderGraphHandle ZeroRaw(RenderGraph graph, string name, ulong byteSize)
    {
        if (byteSize != 16)
            throw new ArgumentOutOfRangeException(nameof(byteSize), "cluster zero raw buffer currently supports the 16-byte read offset args payload.");

        var desc = new BufferDesc
        {
            Name = name,
            SizeInBytes = byteSize,
            Memory = MemoryClass.CpuUpload,
            BindFlags = BindFlags.ShaderResource,
            InitialState = ResourceState.ShaderResource,
            Raw = true,
        };

        if (!_clusterReadOffsetArgsZero.IsValid)
        {
            var device = _context.GraphicsDevice
                ?? throw new InvalidOperationException("cluster zero raw buffer requires an initialized RHI device.");
            Span<byte> zero = stackalloc byte[16];
            _clusterReadOffsetArgsZero = device.CreateBuffer(desc, zero);
        }

        return graph.ImportBuffer(
            name,
            _clusterReadOffsetArgsZero,
            desc,
            new ImportDesc(ResourceState.ShaderResource),
            _clusterReadOffsetArgsZeroViews);
    }

}
