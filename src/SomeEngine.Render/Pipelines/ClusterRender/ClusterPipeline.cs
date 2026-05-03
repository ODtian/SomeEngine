using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using Friflo.Engine.ECS;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Level 2 preset pipeline that assembles cluster render stages.
/// </summary>
public class ClusterPipeline : IRenderFeature
{
    public string Name { get; }

    // ─── Owned resources ───
    private readonly RenderContext _context;
    private readonly ClusterResourceManager _clusterMgr;
    private readonly InstanceDataManager _instanceMgr;
    private readonly RenderWorld? _renderWorld;
    private readonly RenderWorldExtractor? _renderWorldExtractor;
    private readonly ClusterMaterialSlotPreparer? _materialSlotPreparer;
    private readonly ClusterUploadStage _uploadStage;
    private readonly ClusterBVHTraversePass _bvhTraversePass;
    private readonly ClusterHiZ.Resources _hizResources = new();
    private readonly ClusterShade.Resources _shadeResources = new();
    private readonly ClusterStreamer _clusterStreamer;
    private readonly PingPongHandle _hizPingPong = new();
    private readonly GlobalPsoCache _psoCache;
    private readonly BinSpace _binSpace = new();
    public BinSpace BinSpace => _binSpace;
    private int _rasterBinFieldIndex,
        _shadingBinFieldIndex,
        _vertexEvalFieldIndex;

    internal ClusterDebugReadbackPass? _debugReadbackPass;
    private MaterialPSOGroup[] _MaterialPSOGroups = [];
    private MaterialPSOGroup[] _swRasterPSOGroups = [];
    private MaterialPSOGroup[] _hwDrawPSOGroups = [];
    private MaterialPSOGroup[] _deformPSOGroups = [];
    private uint _lastBinSpaceVersion = uint.MaxValue;
    private uint _lastPreparedRenderWorldVersion = uint.MaxValue;
    private Entity[] _shadeFeatureEntities = [];
    private int _shadeFeatureEntityCount;
    private Entity[] _rasterFeatureEntities = [];
    private int _rasterFeatureEntityCount;
    private Entity[] _deformFeatureEntities = [];
    private int _deformFeatureEntityCount;
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
    public ulong DeformCacheByteCapacity { get; set; } = ClusterLimits.DefaultDeformCacheByteCapacity;

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
    public uint DebugDrawSWCount => _debugReadbackPass?.DrawSWCount ?? 0;
    public uint DebugDrawHWCount => _debugReadbackPass?.DrawHWCount ?? 0;
    public uint DebugDrawInstanceCount => _debugReadbackPass?.DrawInstanceCount ?? 0;
    public uint DebugPhase2DrawVertexCount => _debugReadbackPass?.Phase2DrawVertexCount ?? 0;
    public uint DebugPhase2DrawSWCount => _debugReadbackPass?.Phase2DrawSWCount ?? 0;
    public uint DebugPhase2DrawHWCount => _debugReadbackPass?.Phase2DrawHWCount ?? 0;
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
        GlobalPsoCache psoCache,
        bool includeTransparent,
        RenderWorld? renderWorld = null
    )
    {
        Name = name;
        _context = context;
        _clusterMgr = clusterMgr;
        _instanceMgr = instanceMgr;
        _renderWorld = renderWorld;
        _renderWorldExtractor = renderWorld != null ? new RenderWorldExtractor(renderWorld) : null;
        _materialSlotPreparer = renderWorld != null ? new ClusterMaterialSlotPreparer(_binSpace, renderWorld.Store, _instanceMgr) : null;
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
        GlobalPsoCache pc,
        RenderWorld? renderWorld = null
    ) => new("ClusterPipeline.Opaque", ctx, cm, im, pc, false, renderWorld);

    public static ClusterPipeline OpaqueAndTransparent(
        RenderContext ctx,
        ClusterResourceManager cm,
        InstanceDataManager im,
        GlobalPsoCache pc,
        RenderWorld? renderWorld = null
    ) => new("ClusterPipeline.OpaqueAndTransparent", ctx, cm, im, pc, true, renderWorld);

    // ─── Public API ───

    public void PrepareFrame(EntityStore sourceStore, Func<AssetGuid, Material?> materialResolver)
    {
        if (_renderWorldExtractor == null || _materialSlotPreparer == null)
            throw new InvalidOperationException("ClusterPipeline requires RenderWorld-backed prepare path.");

        _renderWorldExtractor.Rebuild(sourceStore, materialResolver);
        bool renderWorldChanged = _lastPreparedRenderWorldVersion != _renderWorldExtractor.Version;
        if (renderWorldChanged)
        {
            GatherFeatureEntities();
            _lastPreparedRenderWorldVersion = _renderWorldExtractor.Version;
        }

        bool slotsChanged = _materialSlotPreparer.Prepare(
            _rasterBinFieldIndex,
            _shadingBinFieldIndex,
            _vertexEvalFieldIndex,
            _renderWorldExtractor.Version);
        if (slotsChanged)
        {
            _binSpace.MarkDirty();
        }

        _binSpace.RebuildIfDirty();
        if (_binSpace.Version != _lastBinSpaceVersion)
        {
            RebuildPSOGroups();
        }
    }

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

        _binSpace.RegisterGroup(_rasterBinFieldIndex, new BinQueue.BinGroup
        {
            Query = () => _rasterFeatureEntities.AsSpan(0, _rasterFeatureEntityCount),
            OrderKey = static _ => 0,
            SignatureFunc = ComputeRasterDispatchSignature,
        });
        _binSpace.RegisterGroup(_shadingBinFieldIndex, new BinQueue.BinGroup
        {
            Query = () => _shadeFeatureEntities.AsSpan(0, _shadeFeatureEntityCount),
            OrderKey = static entity => entity.TryGetComponent<OverlayShade>(out OverlayShade overlay)
                ? 1 + overlay.Layer
                : 0,
            SignatureFunc = static entity => ComputeMaterialDispatchSignature(
                entity,
                entity.GetComponent<ClusterShadeComponent>().Default),
        });

        if (_vertexEvalFieldIndex >= 0)
        {
            _binSpace.RegisterGroup(_vertexEvalFieldIndex, new BinQueue.BinGroup
            {
                Query = () => _deformFeatureEntities.AsSpan(0, _deformFeatureEntityCount),
                OrderKey = static _ => 0,
                SignatureFunc = static entity => ComputeMaterialDispatchSignature(
                    entity,
                    entity.GetComponent<ClusterDeform>().Default),
            });
        }

        _binSpace.FreezeLayout();
    }

    // ─── PSO Group Management ───

    private void RebuildPSOGroups()
    {
        _lastBinSpaceVersion = _binSpace.Version;

        DisposePSOGroups(_MaterialPSOGroups);
        DisposePSOGroups(_swRasterPSOGroups);
        DisposePSOGroups(_hwDrawPSOGroups);
        DisposePSOGroups(_deformPSOGroups);

        _MaterialPSOGroups = ClusterShade.BuildPSOGroups(
            _binSpace,
            _shadingBinFieldIndex,
            _psoCache,
            _context);

        _swRasterPSOGroups = RasterPSOBuilder.BuildComputePSOGroups(
            _binSpace,
            _rasterBinFieldIndex,
            _psoCache,
            _context,
            "SWRaster",
            SelectSWRasterVariant);

        _hwDrawPSOGroups = RasterPSOBuilder.BuildGraphicsPSOGroups(
            _binSpace,
            _rasterBinFieldIndex,
            _psoCache,
            _context,
            "HWClusterDraw",
            SelectHWVSVariant,
            SelectHWPSVariant,
            useVisBuffer: true,
            depthWrite: true);

        _deformPSOGroups = _vertexEvalFieldIndex >= 0
            ? RasterPSOBuilder.BuildComputePSOGroups(
                _binSpace,
                _vertexEvalFieldIndex,
                _psoCache,
                _context,
                "ClusterDeform",
                static entity => entity.GetComponent<ClusterDeform>().Default)
            : [];
    }

    private static void DisposePSOGroups(MaterialPSOGroup[] groups)
    {
        foreach (var group in groups)
            group.Dispose();
    }

    // ─── Main pipeline assembly ───

    public void AddPasses(RenderGraph graph)
    {
        AddPasses(graph, null);
    }

    public void AddPasses(RenderGraph graph, FrameTargetRegistry? frameTargets)
    {
        _clusterStreamer.Update();

        var camera = _freezeCullingCamera ? _frozenCamera : _camera;
        if (frameTargets != null && !frameTargets.TryGetDeclaration(StandardFrameTargets.HiZ, out _))
        {
            frameTargets.DeclareTexture(
                StandardFrameTargets.HiZ,
                _ => ClusterHiZ.CreateHiZTextureDesc(camera),
                FrameTargetLifetime.History,
                ResourceState.Unknown,
                "HiZ"
            );
        }

        var colorTarget = frameTargets?.ResolveTexture(StandardFrameTargets.SceneColor)
            ?? ResolveGraphTarget(graph, "SceneColor", "ColorTarget");
        var depthTarget = frameTargets?.ResolveTexture(StandardFrameTargets.SceneDepth)
            ?? ResolveGraphTarget(graph, "SceneDepth", "DepthTarget");

        // Reset HiZ history on mode change
        if (HiZMode != _prevHiZMode)
        {
            if (frameTargets != null)
                frameTargets.Invalidate(StandardFrameTargets.HiZ);
            else
                _hizPingPong.Reset();
            _prevHiZMode = HiZMode;
        }

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
            _hizResources.Cull,
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
            _hizResources,
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
                DeformCacheByteCapacity = DeformCacheByteCapacity,
                QuantStep = _clusterMgr.QuantStep,
                QuantOrigin = _clusterMgr.QuantOrigin,
            },
            (uint)_instanceMgr.Count,
            deformPSOGroups: _deformPSOGroups,
            swRasterPSOGroups: _swRasterPSOGroups,
            hwDrawPSOGroups: _hwDrawPSOGroups,
            frameTargets: frameTargets
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
                _shadeResources,
                LastOpaqueRasterOutput,
                hizResult.Cull,
                globals,
                hDrawUB,
                hMaterialSlots,
                _MaterialPSOGroups,
                colorTarget,
                depthTarget,
                _binSpace,
                _shadingBinFieldIndex,
                (uint)_shadeFeatureEntityCount,
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
            _debugReadbackPass.AddPasses(graph, traverseOut, hizResult.Cull, DumpNextFrame);
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
        DisposePSOGroups(_MaterialPSOGroups);
        DisposePSOGroups(_swRasterPSOGroups);
        DisposePSOGroups(_hwDrawPSOGroups);
        DisposePSOGroups(_deformPSOGroups);
        _hizResources.Dispose();
        _shadeResources.Dispose();
        _uploadStage.Dispose();
        _bvhTraversePass.Dispose();
        _binSpace.Dispose();
    }

    private void GatherFeatureEntities()
    {
        if (_renderWorld == null)
        {
            _shadeFeatureEntityCount = 0;
            _rasterFeatureEntityCount = 0;
            _deformFeatureEntityCount = 0;
            return;
        }

        int shadeCount = 0;
        int rasterCount = 0;
        int deformCount = 0;

        foreach (Entity entity in _renderWorld.Store.Entities)
        {
            if (entity.TryGetComponent<ClusterShadeComponent>(out ClusterShadeComponent shade)
                && !shade.Default.IsEmpty)
            {
                EnsureEntityCapacity(ref _shadeFeatureEntities, shadeCount + 1);
                _shadeFeatureEntities[shadeCount++] = entity;
            }

            if (entity.TryGetComponent<ClusterRaster>(out _)
                && HasRasterVariant(entity))
            {
                EnsureEntityCapacity(ref _rasterFeatureEntities, rasterCount + 1);
                _rasterFeatureEntities[rasterCount++] = entity;
            }

            if (_vertexEvalFieldIndex >= 0
                && entity.TryGetComponent<ClusterDeform>(out ClusterDeform deform)
                && !deform.Default.IsEmpty)
            {
                EnsureEntityCapacity(ref _deformFeatureEntities, deformCount + 1);
                _deformFeatureEntities[deformCount++] = entity;
            }
        }

        _shadeFeatureEntityCount = shadeCount;
        _rasterFeatureEntityCount = rasterCount;
        _deformFeatureEntityCount = deformCount;
    }

    private static RenderGraphHandle ResolveGraphTarget(
        RenderGraph graph,
        string primaryName,
        string legacyName)
    {
        var handle = graph.GetResourceHandle(primaryName);
        return handle.IsValid ? handle : graph.GetResourceHandle(legacyName);
    }

    private ShaderVariantRef SelectSWRasterVariant(Entity entity)
    {
        ClusterRaster raster = entity.GetComponent<ClusterRaster>();
        if (_useDeformCache && !raster.SWCached.IsEmpty)
        {
            return raster.SWCached;
        }

        if (!raster.SWInline.IsEmpty)
        {
            return raster.SWInline;
        }

        return raster.SWCached;
    }

    private bool HasRasterVariant(Entity entity)
    {
        ShaderVariantRef sw = SelectSWRasterVariant(entity);
        ShaderVariantRef hwVS = SelectHWVSVariant(entity);
        ShaderVariantRef hwPS = SelectHWPSVariant(entity);
        return !sw.IsEmpty || (!hwVS.IsEmpty && !hwPS.IsEmpty);
    }

    private ShaderVariantRef SelectHWVSVariant(Entity entity)
    {
        ClusterRaster raster = entity.GetComponent<ClusterRaster>();
        if (_useDeformCache && !raster.HWVSCached.IsEmpty)
        {
            return raster.HWVSCached;
        }

        if (!raster.HWVSInline.IsEmpty)
        {
            return raster.HWVSInline;
        }

        return raster.HWVSCached;
    }

    private static ShaderVariantRef SelectHWPSVariant(Entity entity)
        => entity.GetComponent<ClusterRaster>().HWPS;

    private ulong ComputeRasterDispatchSignature(Entity entity)
    {
        ulong hash = 14695981039346656037UL;
        hash = HashValue(hash, ComputeMaterialDispatchSignature(entity, SelectSWRasterVariant(entity)));
        hash = HashValue(hash, ComputeMaterialDispatchSignature(entity, SelectHWVSVariant(entity)));
        hash = HashValue(hash, ComputeMaterialDispatchSignature(entity, SelectHWPSVariant(entity)));
        return hash;
    }

    private static ulong ComputeMaterialDispatchSignature(Entity entity, ShaderVariantRef variantRef)
    {
        ulong hash = 14695981039346656037UL;
        hash = HashString(hash, variantRef.Shader?.AssetGuid);
        hash = HashString(hash, variantRef.EntryPoint);

        if (entity.TryGetComponent<MaterialRef>(out MaterialRef materialRef)
            && materialRef.Owner != null)
        {
            ulong paramSignature = materialRef.Owner.Params.GetFilteredSignatureHash(
                EnumerateShaderBindingNames(variantRef.Shader),
                includeScalars: true);
            hash = HashValue(hash, paramSignature);
        }

        return hash;
    }

    private static IEnumerable<string> EnumerateShaderBindingNames(ShaderAsset? shader)
    {
        if (shader?.Metadata?.MaterialBindings is { Count: > 0 } materialBindings)
        {
            foreach (var binding in materialBindings)
            {
                if (!string.IsNullOrWhiteSpace(binding.Name))
                {
                    yield return binding.Name;
                }
            }

            yield break;
        }

        if (shader?.Reflections == null)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var backendReflection in shader.Reflections)
        {
            if (backendReflection.Reflection?.Resources == null)
            {
                continue;
            }

            foreach (var resource in backendReflection.Reflection.Resources)
            {
                if (!string.IsNullOrWhiteSpace(resource.Name) && seen.Add(resource.Name))
                {
                    yield return resource.Name;
                }
            }
        }
    }

    private static ulong HashString(ulong hash, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return HashValue(hash, 0UL);
        }

        foreach (char c in value)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static ulong HashValue<T>(ulong hash, T value)
    {
        int valueHash = value is null ? 0 : EqualityComparer<T>.Default.GetHashCode(value);
        hash ^= unchecked((ulong)valueHash);
        hash *= 1099511628211UL;
        return hash;
    }

    private static void EnsureEntityCapacity(ref Entity[] entities, int count)
    {
        if (entities.Length < count)
        {
            Array.Resize(ref entities, Math.Max(count, entities.Length == 0 ? 16 : entities.Length * 2));
        }
    }

}
