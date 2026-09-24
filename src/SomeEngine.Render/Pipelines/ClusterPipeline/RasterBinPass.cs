using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Pipelines;

internal readonly record struct RasterDeformBins(
    RasterBinFrame Raster,
    DeformBinFrame Deform);

[StructLayout(LayoutKind.Sequential)]
internal struct CombinedBinningUniforms
{
    public uint RasterMaxBins;
    public uint DeformMaxBins;
    public uint SlotCapacity;
    public uint RasterBinFieldIndex;
    public uint DeformBinFieldIndex;
    public uint MaxVisibleClusters;
    public uint ResetCacheAllocationState;
    public uint Pad0;
}

internal sealed class RasterBinPass
{
    private const uint ReserveBlockSize = 128;

    private readonly RenderContext _renderContext;
    private readonly ShaderBindingTable _clearPrepareLayout;
    private readonly ShaderBindingTable _chainLayout;
    private readonly ShaderBindingTable _combinedClearPrepareLayout;
    private readonly ShaderBindingTable _combinedChainLayout;
    private readonly ReflectedBinding[] _clearPrepareBindings;
    private readonly ReflectedBinding[] _chainBindings;
    private readonly ReflectedBinding[] _combinedClearPrepareBindings;
    private readonly ReflectedBinding[] _combinedChainBindings;
    private readonly BindingLayoutHandle _clearPrepareBindingLayout;
    private readonly BindingLayoutHandle _chainBindingLayout;
    private readonly BindingLayoutHandle _combinedClearPrepareBindingLayout;
    private readonly BindingLayoutHandle _combinedChainBindingLayout;
    private readonly uint _clearPrepareSet;
    private readonly uint _chainSet;
    private readonly uint _combinedClearPrepareSet;
    private readonly uint _combinedChainSet;
    private readonly UniformPool<BinningUniforms> _binningUniforms;
    private readonly UniformPool<CombinedBinningUniforms> _combinedUniforms;
    private PipelineTicket _clearPreparePipeline;
    private PipelineTicket _countPipeline;
    private PipelineTicket _reservePipeline;
    private PipelineTicket _scatterPipeline;
    private PipelineTicket _combinedClearPreparePipeline;
    private PipelineTicket _combinedCountPipeline;
    private PipelineTicket _combinedReservePipeline;
    private PipelineTicket _combinedScatterPipeline;
    private bool _disposed;

    public RasterBinPass(RenderContext renderContext, Shader shader)
    {
        _renderContext = renderContext ?? throw new ArgumentNullException(nameof(renderContext));
        IDevice device = renderContext.GraphicsDevice ?? throw new InvalidOperationException("cluster raster binning pass requires an initialized render context.");

        _clearPrepareLayout = ShaderBindings.Create(
            device,
            "Cluster Binning Clear Prepare",
            shader,
            ["Uniforms", "RasterBinMeta", "DrawArgs", "BinningDispatchArgs", "ReserveCounters"],
            "CSBinningClearPrepare");
        _clearPreparePipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _clearPrepareLayout,
            "CSBinningClearPrepare",
            "Cluster Binning Clear Prepare",
            source: ClusterSources.Builtins);
        _clearPrepareBindings =
        [
            _clearPrepareLayout.GetRequired("Uniforms"),
            _clearPrepareLayout.GetRequired("RasterBinMeta"),
            _clearPrepareLayout.GetRequired("DrawArgs"),
            _clearPrepareLayout.GetRequired("BinningDispatchArgs"),
            _clearPrepareLayout.GetRequired("ReserveCounters"),
        ];
        _clearPrepareSet = BindInput.GetSetIndex(
            "cluster binning pass",
            _clearPrepareBindings[0],
            _clearPrepareBindings[1],
            _clearPrepareBindings[2],
            _clearPrepareBindings[3],
            _clearPrepareBindings[4]);
        _clearPrepareBindingLayout = _clearPrepareLayout.Layout(_clearPrepareSet);
        _chainLayout = ShaderBindings.Create(
            device,
            "Cluster Binning Chain",
            shader,
            [
                "Uniforms",
                "VisibleClusters",
                "InstanceHeaders",
                "DrawArgs",
                "ClusterReadOffsetArgs",
                "SlotBuffer",
                "PageHeap",
                "RasterBinMeta",
                "BinnedDrawArgs",
                "BinnedHWDrawArgs",
                "BinnedSWDispatchArgs",
                "ReserveCounters",
                "BinnedClusterIndexBuffer",
            ],
            "CSBinningCount",
            "CSBinningReserve",
            "CSBinningScatter");
        _countPipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _chainLayout,
            "CSBinningCount",
            "Cluster Binning Count",
            source: ClusterSources.Builtins);
        _reservePipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _chainLayout,
            "CSBinningReserve",
            "Cluster Binning Reserve",
            source: ClusterSources.Builtins);
        _scatterPipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _chainLayout,
            "CSBinningScatter",
            "Cluster Binning Scatter",
            source: ClusterSources.Builtins);
        _chainBindings =
        [
            _chainLayout.GetRequired("Uniforms"),
            _chainLayout.GetRequired("VisibleClusters"),
            _chainLayout.GetRequired("InstanceHeaders"),
            _chainLayout.GetRequired("DrawArgs"),
            _chainLayout.GetRequired("ClusterReadOffsetArgs"),
            _chainLayout.GetRequired("SlotBuffer"),
            _chainLayout.GetRequired("PageHeap"),
            _chainLayout.GetRequired("RasterBinMeta"),
            _chainLayout.GetRequired("BinnedDrawArgs"),
            _chainLayout.GetRequired("BinnedHWDrawArgs"),
            _chainLayout.GetRequired("BinnedSWDispatchArgs"),
            _chainLayout.GetRequired("ReserveCounters"),
            _chainLayout.GetRequired("BinnedClusterIndexBuffer"),
        ];
        _chainSet = BindInput.GetSetIndex(
            "cluster binning pass",
            _chainBindings[0],
            _chainBindings[1],
            _chainBindings[2],
            _chainBindings[3],
            _chainBindings[4],
            _chainBindings[5],
            _chainBindings[6],
            _chainBindings[7],
            _chainBindings[8],
            _chainBindings[9],
            _chainBindings[10],
            _chainBindings[11],
            _chainBindings[12]);
        _chainBindingLayout = _chainLayout.Layout(_chainSet);
        _combinedClearPrepareLayout = ShaderBindings.Create(
            device,
            "Cluster Raster+Deform Bin Clear Prepare",
            shader,
            [
                "CombinedUniforms",
                "RasterBinMeta",
                "DeformBinMeta",
                "DrawArgs",
                "BinningDispatchArgs",
                "ReserveCounters",
                "DeformReserveCounters",
                "CacheAllocationCounter",
            ],
            "CSRasterDeformBinClearPrepare");
        _combinedClearPreparePipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _combinedClearPrepareLayout,
            "CSRasterDeformBinClearPrepare",
            "Cluster Raster+Deform Bin Clear Prepare",
            source: ClusterSources.Builtins);
        _combinedClearPrepareBindings =
        [
            _combinedClearPrepareLayout.GetRequired("CombinedUniforms"),
            _combinedClearPrepareLayout.GetRequired("RasterBinMeta"),
            _combinedClearPrepareLayout.GetRequired("DeformBinMeta"),
            _combinedClearPrepareLayout.GetRequired("DrawArgs"),
            _combinedClearPrepareLayout.GetRequired("BinningDispatchArgs"),
            _combinedClearPrepareLayout.GetRequired("ReserveCounters"),
            _combinedClearPrepareLayout.GetRequired("DeformReserveCounters"),
            _combinedClearPrepareLayout.GetRequired("CacheAllocationCounter"),
        ];
        _combinedClearPrepareSet = BindInput.GetSetIndex(
            "cluster binning pass",
            _combinedClearPrepareBindings[0],
            _combinedClearPrepareBindings[1],
            _combinedClearPrepareBindings[2],
            _combinedClearPrepareBindings[3],
            _combinedClearPrepareBindings[4],
            _combinedClearPrepareBindings[5],
            _combinedClearPrepareBindings[6],
            _combinedClearPrepareBindings[7]);
        _combinedClearPrepareBindingLayout = _combinedClearPrepareLayout.Layout(_combinedClearPrepareSet);
        _combinedChainLayout = ShaderBindings.Create(
            device,
            "Cluster Raster+Deform Bin Chain",
            shader,
            [
                "CombinedUniforms",
                "VisibleClusters",
                "InstanceHeaders",
                "DrawArgs",
                "ClusterReadOffsetArgs",
                "SlotBuffer",
                "PageHeap",
                "RasterBinMeta",
                "DeformBinMeta",
                "CacheOffsetsWrite",
                "BinnedDrawArgs",
                "BinnedHWDrawArgs",
                "BinnedSWDispatchArgs",
                "PreDeformDispatchArgs",
                "ReserveCounters",
                "DeformReserveCounters",
                "BinnedClusterIndexBuffer",
                "DeformBinnedClusterIndexBuffer",
            ],
            "CSRasterDeformBinCount",
            "CSRasterDeformBinReserve",
            "CSRasterDeformBinScatter");
        _combinedCountPipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _combinedChainLayout,
            "CSRasterDeformBinCount",
            "Cluster Raster+Deform Bin Count",
            source: ClusterSources.Builtins);
        _combinedReservePipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _combinedChainLayout,
            "CSRasterDeformBinReserve",
            "Cluster Raster+Deform Bin Reserve",
            source: ClusterSources.Builtins);
        _combinedScatterPipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _combinedChainLayout,
            "CSRasterDeformBinScatter",
            "Cluster Raster+Deform Bin Scatter",
            source: ClusterSources.Builtins);
        _combinedChainBindings =
        [
            _combinedChainLayout.GetRequired("CombinedUniforms"),
            _combinedChainLayout.GetRequired("VisibleClusters"),
            _combinedChainLayout.GetRequired("InstanceHeaders"),
            _combinedChainLayout.GetRequired("DrawArgs"),
            _combinedChainLayout.GetRequired("ClusterReadOffsetArgs"),
            _combinedChainLayout.GetRequired("SlotBuffer"),
            _combinedChainLayout.GetRequired("PageHeap"),
            _combinedChainLayout.GetRequired("RasterBinMeta"),
            _combinedChainLayout.GetRequired("DeformBinMeta"),
            _combinedChainLayout.GetRequired("CacheOffsetsWrite"),
            _combinedChainLayout.GetRequired("BinnedDrawArgs"),
            _combinedChainLayout.GetRequired("BinnedHWDrawArgs"),
            _combinedChainLayout.GetRequired("BinnedSWDispatchArgs"),
            _combinedChainLayout.GetRequired("PreDeformDispatchArgs"),
            _combinedChainLayout.GetRequired("ReserveCounters"),
            _combinedChainLayout.GetRequired("DeformReserveCounters"),
            _combinedChainLayout.GetRequired("BinnedClusterIndexBuffer"),
            _combinedChainLayout.GetRequired("DeformBinnedClusterIndexBuffer"),
        ];
        _combinedChainSet = BindInput.GetSetIndex(
            "cluster binning pass",
            _combinedChainBindings[0],
            _combinedChainBindings[1],
            _combinedChainBindings[2],
            _combinedChainBindings[3],
            _combinedChainBindings[4],
            _combinedChainBindings[5],
            _combinedChainBindings[6],
            _combinedChainBindings[7],
            _combinedChainBindings[8],
            _combinedChainBindings[9],
            _combinedChainBindings[10],
            _combinedChainBindings[11],
            _combinedChainBindings[12],
            _combinedChainBindings[13],
            _combinedChainBindings[14],
            _combinedChainBindings[15],
            _combinedChainBindings[16],
            _combinedChainBindings[17]);
        _combinedChainBindingLayout = _combinedChainLayout.Layout(_combinedChainSet);
        _binningUniforms = new UniformPool<BinningUniforms>(device);
        _combinedUniforms = new UniformPool<CombinedBinningUniforms>(device);
    }

    public void AddTickets(List<PipelineTicket> tickets)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        tickets.Add(_clearPreparePipeline);
        tickets.Add(_countPipeline);
        tickets.Add(_reservePipeline);
        tickets.Add(_scatterPipeline);
        tickets.Add(_combinedClearPreparePipeline);
        tickets.Add(_combinedCountPipeline);
        tickets.Add(_combinedReservePipeline);
        tickets.Add(_combinedScatterPipeline);
    }

    public RasterBinFrame AddPasses(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterCullOutput cull,
        RenderGraphHandle drawArgs,
        RenderGraphHandle clusterReadOffsetArgs,
        RenderGraphHandle slotBuffer,
        uint slotCapacity,
        uint rasterBinFieldIndex,
        uint totalBins,
        string? tag = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!buffers.PageHeap.IsValid || !instances.Header.IsValid)
            throw new ArgumentException("cluster binning requires valid cluster buffers and instance frame.", nameof(buffers));
        if (!cull.VisibleClusters.IsValid || !cull.DrawArgs.IsValid)
            throw new ArgumentException("cluster binning requires valid cull output.", nameof(cull));
        if (!drawArgs.IsValid)
            throw new ArgumentException("cluster binning requires a valid draw args buffer.", nameof(drawArgs));
        if (!clusterReadOffsetArgs.IsValid)
            throw new ArgumentException("cluster binning requires a valid cluster read-offset args buffer.", nameof(clusterReadOffsetArgs));
        if (!slotBuffer.IsValid)
            throw new ArgumentException("cluster binning requires a valid material slot buffer.", nameof(slotBuffer));
        if (slotCapacity == 0)
            throw new ArgumentOutOfRangeException(nameof(slotCapacity), "cluster binning slot capacity must be non-zero.");

        string prefix = tag != null ? $"{tag}_" : string.Empty;
        uint maxBins = totalBins > 0 ? totalBins : 1u;

        var rasterBinMeta = ClusterBinGpu.Meta(
            graph,
            $"{prefix}RasterBinMeta",
            maxBins,
            ClusterBinGpu.RasterMetaStride);
        var binnedClusterIndex = ClusterBinGpu.Index(graph, $"{prefix}BinnedClusterIndexBuffer");
        var binnedDrawArgs = ClusterBinGpu.Args(graph, $"{prefix}BinnedDrawArgs", maxBins * 16ul, copySource: true);
        var binnedHWDrawArgs = ClusterBinGpu.Args(graph, $"{prefix}BinnedHWDrawArgs", maxBins * 16ul, copySource: true);
        var binningDispatchArgs = ClusterBinGpu.Args(graph, $"{prefix}BinningDispatchArgs", 16, copySource: true);
        var binnedSWDispatchArgs = ClusterBinGpu.Args(graph, $"{prefix}BinnedSWDispatchArgs", maxBins * 24ul, copySource: true);
        var reserveCounters = ClusterBinGpu.Uint(
            graph,
            $"{prefix}RasterBinReserveCounters",
            2,
            copy: false,
            lifetime: ResourceLifetime.Transient);
        var uniforms = _binningUniforms.Add(
            graph,
            [
                new BinningUniforms
                {
                    MaxBins = maxBins,
                    SlotCapacity = slotCapacity,
                    BinFieldIndex = rasterBinFieldIndex,
                    MaxVisibleClusters = ClusterLimits.MaxDraws,
                }
            ],
            $"{prefix}BinningUniforms").Buffer;

        AddBinChain(
            graph,
            buffers,
            instances,
            cull,
            uniforms,
            drawArgs,
            clusterReadOffsetArgs,
            slotBuffer,
            binningDispatchArgs,
            rasterBinMeta,
            binnedDrawArgs,
            binnedHWDrawArgs,
            binnedSWDispatchArgs,
            reserveCounters,
            binnedClusterIndex,
            maxBins,
            prefix);

        return new RasterBinFrame(
            binnedClusterIndex,
            binnedDrawArgs,
            binnedHWDrawArgs,
            rasterBinMeta,
            binningDispatchArgs,
            binnedSWDispatchArgs)
        {
            DrawBinCount = maxBins,
        };
    }

    public RasterDeformBins AddRasterDeform(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterCullOutput cull,
        RenderGraphHandle drawArgs,
        RenderGraphHandle clusterReadOffsetArgs,
        RenderGraphHandle slotBuffer,
        uint slotCapacity,
        uint rasterBinFieldIndex,
        uint rasterBinCount,
        uint vertexEvalFieldIndex,
        uint vertexEvalBinCount,
        RenderGraphHandle cacheOffsets,
        RenderGraphHandle cacheAllocationCounter,
        bool resetCacheAllocationState,
        string? tag = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!buffers.PageHeap.IsValid || !instances.Header.IsValid)
            throw new ArgumentException("combined binning requires valid cluster buffers and instance frame.", nameof(buffers));
        if (!cull.VisibleClusters.IsValid || !cull.DrawArgs.IsValid)
            throw new ArgumentException("combined binning requires valid cull output.", nameof(cull));
        if (!drawArgs.IsValid || !clusterReadOffsetArgs.IsValid)
            throw new ArgumentException("combined binning requires valid draw and read-offset args.", nameof(drawArgs));
        if (!slotBuffer.IsValid)
            throw new ArgumentException("combined binning requires a valid material slot buffer.", nameof(slotBuffer));
        if (!cacheOffsets.IsValid || !cacheAllocationCounter.IsValid)
            throw new ArgumentException("combined binning requires valid cache offset and allocation counter buffers.", nameof(cacheOffsets));
        if (slotCapacity == 0)
            throw new ArgumentOutOfRangeException(nameof(slotCapacity), "combined binning slot capacity must be non-zero.");
        if (vertexEvalFieldIndex == uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(vertexEvalFieldIndex), "combined binning requires a valid vertex-eval bin field.");

        string prefix = tag != null ? $"{tag}_" : string.Empty;
        uint rasterMaxBins = rasterBinCount > 0 ? rasterBinCount : 1u;
        uint deformMaxBins = vertexEvalBinCount > 0 ? vertexEvalBinCount : 1u;
        uint clearReserveBins = Math.Max(rasterMaxBins, deformMaxBins);

        var rasterBinMeta = ClusterBinGpu.Meta(
            graph,
            $"{prefix}RasterBinMeta",
            rasterMaxBins,
            ClusterBinGpu.RasterMetaStride);
        var binnedClusterIndex = ClusterBinGpu.Index(graph, $"{prefix}BinnedClusterIndexBuffer");
        var binnedDrawArgs = ClusterBinGpu.Args(graph, $"{prefix}BinnedDrawArgs", rasterMaxBins * 16ul, copySource: true);
        var binnedHWDrawArgs = ClusterBinGpu.Args(graph, $"{prefix}BinnedHWDrawArgs", rasterMaxBins * 16ul, copySource: true);
        var binningDispatchArgs = ClusterBinGpu.Args(graph, $"{prefix}BinningDispatchArgs", 16, copySource: true);
        var binnedSWDispatchArgs = ClusterBinGpu.Args(graph, $"{prefix}BinnedSWDispatchArgs", rasterMaxBins * 24ul, copySource: true);
        var reserveCounters = ClusterBinGpu.Uint(
            graph,
            $"{prefix}RasterBinReserveCounters",
            2,
            copy: false,
            lifetime: ResourceLifetime.Transient);

        var deformBinMeta = ClusterBinGpu.Meta(
            graph,
            $"{prefix}DeformBinMeta",
            deformMaxBins,
            ClusterBinGpu.DeformMetaStride);
        var deformBinnedClusterIndex = ClusterBinGpu.Index(graph, $"{prefix}DeformBinnedClusterIndexBuffer");
        var preDeformDispatchArgs = ClusterBinGpu.Args(
            graph,
            $"{prefix}PreDeformDispatchArgs",
            checked((ulong)deformMaxBins * IndirectArgumentSize.Dispatch),
            copySource: true);
        var deformReserveCounters = ClusterBinGpu.Uint(
            graph,
            $"{prefix}DeformBinReserveCounters",
            1,
            copy: false,
            lifetime: ResourceLifetime.Transient);
        var uniforms = _combinedUniforms.Add(
            graph,
            [
                new CombinedBinningUniforms
                {
                    RasterMaxBins = rasterMaxBins,
                    DeformMaxBins = deformMaxBins,
                    SlotCapacity = slotCapacity,
                    RasterBinFieldIndex = rasterBinFieldIndex,
                    DeformBinFieldIndex = vertexEvalFieldIndex,
                    MaxVisibleClusters = ClusterLimits.MaxDraws,
                    ResetCacheAllocationState = resetCacheAllocationState ? 1u : 0u,
                }
            ],
            $"{prefix}CombinedBinningUniforms").Buffer;

        AddComboBins(
            graph,
            buffers,
            instances,
            cull,
            uniforms,
            drawArgs,
            clusterReadOffsetArgs,
            slotBuffer,
            binningDispatchArgs,
            rasterBinMeta,
            binnedDrawArgs,
            binnedHWDrawArgs,
            binnedSWDispatchArgs,
            reserveCounters,
            binnedClusterIndex,
            deformBinMeta,
            preDeformDispatchArgs,
            deformReserveCounters,
            cacheAllocationCounter,
            cacheOffsets,
            deformBinnedClusterIndex,
            clearReserveBins,
            resetCacheAllocationState,
            prefix);

        var raster = new RasterBinFrame(
            binnedClusterIndex,
            binnedDrawArgs,
            binnedHWDrawArgs,
            rasterBinMeta,
            binningDispatchArgs,
            binnedSWDispatchArgs)
        {
            DrawBinCount = rasterMaxBins,
        };
        var deform = new DeformBinFrame(deformBinnedClusterIndex, deformBinMeta, preDeformDispatchArgs);
        return new RasterDeformBins(raster, deform);
    }

    public void Dispose(PipelineCache store)
    {
        if (_disposed)
            return;
        ArgumentNullException.ThrowIfNull(store);

        IDevice? device = _renderContext.GraphicsDevice;
        store.ReleaseIdle(ref _combinedScatterPipeline);
        store.ReleaseIdle(ref _combinedReservePipeline);
        store.ReleaseIdle(ref _combinedCountPipeline);
        store.ReleaseIdle(ref _combinedClearPreparePipeline);
        store.ReleaseIdle(ref _scatterPipeline);
        store.ReleaseIdle(ref _reservePipeline);
        store.ReleaseIdle(ref _countPipeline);
        store.ReleaseIdle(ref _clearPreparePipeline);
        ShaderBindings.Destroy(device, _combinedChainLayout);
        ShaderBindings.Destroy(device, _combinedClearPrepareLayout);
        ShaderBindings.Destroy(device, _chainLayout);
        ShaderBindings.Destroy(device, _clearPrepareLayout);
        _combinedUniforms.Dispose();
        _binningUniforms.Dispose();
        _disposed = true;
    }

    private void AddBinChain(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterCullOutput cull,
        RenderGraphHandle uniforms,
        RenderGraphHandle drawArgs,
        RenderGraphHandle clusterReadOffsetArgs,
        RenderGraphHandle slotBuffer,
        RenderGraphHandle binningDispatchArgs,
        RenderGraphHandle rasterBinMeta,
        RenderGraphHandle binnedDrawArgs,
        RenderGraphHandle binnedHWDrawArgs,
        RenderGraphHandle binnedSWDispatchArgs,
        RenderGraphHandle reserveCounters,
        RenderGraphHandle binnedClusterIndex,
        uint maxBins,
        string prefix)
    {
        graph.AddComputePass(
            $"{prefix}Cluster Binning Chain",
            builder =>
            {
                builder.Read(uniforms, ResourceState.ConstantBuffer);
                builder.Read(cull.VisibleClusters, ResourceState.ShaderResource);
                builder.Read(instances.Header, ResourceState.ShaderResource);
                builder.Read(drawArgs, ResourceState.ShaderResource);
                builder.Read(clusterReadOffsetArgs, ResourceState.ShaderResource);
                builder.Read(slotBuffer, ResourceState.ShaderResource);
                builder.Read(buffers.PageHeap, ResourceState.ShaderResource);
                builder.Use(
                    binningDispatchArgs,
                    ResourceState.UnorderedAccess,
                    ResourceState.IndirectArgument,
                    RenderGraphAccess.ReadWrite);
                builder.ReadWrite(rasterBinMeta, ResourceState.UnorderedAccess);
                builder.Write(binnedDrawArgs, ResourceState.UnorderedAccess);
                builder.Write(binnedHWDrawArgs, ResourceState.UnorderedAccess);
                builder.Write(binnedSWDispatchArgs, ResourceState.UnorderedAccess);
                builder.ReadWrite(reserveCounters, ResourceState.UnorderedAccess);
                builder.Write(binnedClusterIndex, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                var bindings = _chainBindings;
                var uniformsView = bindings[0].BufferView(context, uniforms);
                var visibleView = bindings[1].BufferView(context, cull.VisibleClusters);
                var headersView = bindings[2].BufferView(context, instances.Header);
                var drawView = bindings[3].BufferView(context, drawArgs);
                var readOffsetsView = bindings[4].BufferView(context, clusterReadOffsetArgs);
                var slotsView = bindings[5].BufferView(context, slotBuffer);
                var pageHeapView = bindings[6].BufferView(context, buffers.PageHeap);
                var rasterBinMetaView = bindings[7].BufferView(context, rasterBinMeta);
                var binnedDrawArgsView = bindings[8].BufferView(context, binnedDrawArgs);
                var binnedHWDrawArgsView = bindings[9].BufferView(context, binnedHWDrawArgs);
                var binnedSWDispatchArgsView = bindings[10].BufferView(context, binnedSWDispatchArgs);
                var reserveCountersView = bindings[11].BufferView(context, reserveCounters);
                var binnedClusterIndexView = bindings[12].BufferView(context, binnedClusterIndex);
                var parameters = context.Bindings(_chainBindingLayout)
                    .Reserve(13)
                    .Buffer(bindings[0], uniformsView)
                    .Buffer(bindings[1], visibleView)
                    .Buffer(bindings[2], headersView)
                    .Buffer(bindings[3], drawView)
                    .Buffer(bindings[4], readOffsetsView)
                    .Buffer(bindings[5], slotsView)
                    .Buffer(bindings[6], pageHeapView)
                    .Buffer(bindings[7], rasterBinMetaView)
                    .Buffer(bindings[8], binnedDrawArgsView)
                    .Buffer(bindings[9], binnedHWDrawArgsView)
                    .Buffer(bindings[10], binnedSWDispatchArgsView)
                    .Buffer(bindings[11], reserveCountersView)
                    .Buffer(bindings[12], binnedClusterIndexView);

                var clearBindings = _clearPrepareBindings;
                var clearParameters = context.Bindings(_clearPrepareBindingLayout)
                    .Reserve(5)
                    .Buffer(clearBindings[0], uniforms)
                    .Buffer(clearBindings[1], rasterBinMeta)
                    .Buffer(clearBindings[2], drawArgs)
                    .Buffer(clearBindings[3], binningDispatchArgs)
                    .Buffer(clearBindings[4], reserveCounters);

                pass.SetPipeline(context.GetPipeline(_clearPreparePipeline));
                pass.SetParameters(_clearPrepareSet, clearParameters);
                pass.Dispatch(checked((maxBins + 63u) / 64u), 1, 1);

                FlushBarriers(
                    pass,
                    [new UavBarrier(rasterBinMeta)]);
                pass.BufferBarrier(
                    binningDispatchArgs,
                    ResourceState.UnorderedAccess,
                    ResourceState.IndirectArgument);

                pass.SetPipeline(context.GetPipeline(_countPipeline));
                pass.SetParameters(_chainSet, parameters);
                pass.DispatchIndirect(binningDispatchArgs);

                FlushBarriers(
                    pass,
                    [new UavBarrier(rasterBinMeta)]);

                pass.SetPipeline(context.GetPipeline(_reservePipeline));
                pass.SetParameters(_chainSet, parameters);
                pass.Dispatch(checked((maxBins + ReserveBlockSize - 1u) / ReserveBlockSize), 1, 1);

                FlushBarriers(
                    pass,
                    [new UavBarrier(rasterBinMeta)]);

                pass.SetPipeline(context.GetPipeline(_scatterPipeline));
                pass.SetParameters(_chainSet, parameters);
                pass.DispatchIndirect(binningDispatchArgs);
            });
    }

    private void AddComboBins(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterCullOutput cull,
        RenderGraphHandle uniforms,
        RenderGraphHandle drawArgs,
        RenderGraphHandle clusterReadOffsetArgs,
        RenderGraphHandle slotBuffer,
        RenderGraphHandle binningDispatchArgs,
        RenderGraphHandle rasterBinMeta,
        RenderGraphHandle binnedDrawArgs,
        RenderGraphHandle binnedHWDrawArgs,
        RenderGraphHandle binnedSWDispatchArgs,
        RenderGraphHandle reserveCounters,
        RenderGraphHandle binnedClusterIndex,
        RenderGraphHandle deformBinMeta,
        RenderGraphHandle preDeformDispatchArgs,
        RenderGraphHandle deformReserveCounters,
        RenderGraphHandle cacheAllocationCounter,
        RenderGraphHandle cacheOffsets,
        RenderGraphHandle deformBinnedClusterIndex,
        uint maxBins,
        bool resetCacheAllocationState,
        string prefix)
    {
        graph.AddComputePass(
            $"{prefix}Cluster Raster+Deform Bin Chain",
            builder =>
            {
                builder.Read(uniforms, ResourceState.ConstantBuffer);
                builder.Read(cull.VisibleClusters, ResourceState.ShaderResource);
                builder.Read(instances.Header, ResourceState.ShaderResource);
                builder.Read(drawArgs, ResourceState.ShaderResource);
                builder.Read(clusterReadOffsetArgs, ResourceState.ShaderResource);
                builder.Read(slotBuffer, ResourceState.ShaderResource);
                builder.Read(buffers.PageHeap, ResourceState.ShaderResource);
                builder.Use(
                    binningDispatchArgs,
                    ResourceState.UnorderedAccess,
                    ResourceState.IndirectArgument,
                    RenderGraphAccess.ReadWrite);
                builder.ReadWrite(rasterBinMeta, ResourceState.UnorderedAccess);
                builder.Write(binnedDrawArgs, ResourceState.UnorderedAccess);
                builder.Write(binnedHWDrawArgs, ResourceState.UnorderedAccess);
                builder.Write(binnedSWDispatchArgs, ResourceState.UnorderedAccess);
                builder.ReadWrite(reserveCounters, ResourceState.UnorderedAccess);
                builder.Write(binnedClusterIndex, ResourceState.UnorderedAccess);
                builder.ReadWrite(deformBinMeta, ResourceState.UnorderedAccess);
                builder.Write(preDeformDispatchArgs, ResourceState.UnorderedAccess);
                builder.ReadWrite(deformReserveCounters, ResourceState.UnorderedAccess);
                builder.Write(cacheOffsets, ResourceState.UnorderedAccess);
                builder.Write(deformBinnedClusterIndex, ResourceState.UnorderedAccess);
                if (resetCacheAllocationState)
                    builder.Write(cacheAllocationCounter, ResourceState.UnorderedAccess);
                else
                    builder.ReadWrite(cacheAllocationCounter, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                var bindings = _combinedChainBindings;
                var uniformsView = bindings[0].BufferView(context, uniforms);
                var visibleView = bindings[1].BufferView(context, cull.VisibleClusters);
                var headersView = bindings[2].BufferView(context, instances.Header);
                var drawView = bindings[3].BufferView(context, drawArgs);
                var readOffsetsView = bindings[4].BufferView(context, clusterReadOffsetArgs);
                var slotsView = bindings[5].BufferView(context, slotBuffer);
                var pageHeapView = bindings[6].BufferView(context, buffers.PageHeap);
                var rasterBinMetaView = bindings[7].BufferView(context, rasterBinMeta);
                var deformBinMetaView = bindings[8].BufferView(context, deformBinMeta);
                var cacheOffsetsView = bindings[9].BufferView(context, cacheOffsets);
                var binnedDrawArgsView = bindings[10].BufferView(context, binnedDrawArgs);
                var binnedHWDrawArgsView = bindings[11].BufferView(context, binnedHWDrawArgs);
                var binnedSWDispatchArgsView = bindings[12].BufferView(context, binnedSWDispatchArgs);
                var preDeformDispatchArgsView = bindings[13].BufferView(context, preDeformDispatchArgs);
                var reserveCountersView = bindings[14].BufferView(context, reserveCounters);
                var deformReserveCountersView = bindings[15].BufferView(context, deformReserveCounters);
                var binnedClusterIndexView = bindings[16].BufferView(context, binnedClusterIndex);
                var deformBinnedClusterIndexView = bindings[17].BufferView(context, deformBinnedClusterIndex);
                var parameters = context.Bindings(_combinedChainBindingLayout)
                    .Reserve(18)
                    .Buffer(bindings[0], uniformsView)
                    .Buffer(bindings[1], visibleView)
                    .Buffer(bindings[2], headersView)
                    .Buffer(bindings[3], drawView)
                    .Buffer(bindings[4], readOffsetsView)
                    .Buffer(bindings[5], slotsView)
                    .Buffer(bindings[6], pageHeapView)
                    .Buffer(bindings[7], rasterBinMetaView)
                    .Buffer(bindings[8], deformBinMetaView)
                    .Buffer(bindings[9], cacheOffsetsView)
                    .Buffer(bindings[10], binnedDrawArgsView)
                    .Buffer(bindings[11], binnedHWDrawArgsView)
                    .Buffer(bindings[12], binnedSWDispatchArgsView)
                    .Buffer(bindings[13], preDeformDispatchArgsView)
                    .Buffer(bindings[14], reserveCountersView)
                    .Buffer(bindings[15], deformReserveCountersView)
                    .Buffer(bindings[16], binnedClusterIndexView)
                    .Buffer(bindings[17], deformBinnedClusterIndexView);

                var clearBindings = _combinedClearPrepareBindings;
                var clearParameters = context.Bindings(_combinedClearPrepareBindingLayout)
                    .Reserve(8)
                    .Buffer(clearBindings[0], uniforms)
                    .Buffer(clearBindings[1], rasterBinMeta)
                    .Buffer(clearBindings[2], deformBinMeta)
                    .Buffer(clearBindings[3], drawArgs)
                    .Buffer(clearBindings[4], binningDispatchArgs)
                    .Buffer(clearBindings[5], reserveCounters)
                    .Buffer(clearBindings[6], deformReserveCounters)
                    .Buffer(clearBindings[7], cacheAllocationCounter);

                pass.SetPipeline(context.GetPipeline(_combinedClearPreparePipeline));
                pass.SetParameters(_combinedClearPrepareSet, clearParameters);
                pass.Dispatch(checked((maxBins + 63u) / 64u), 1, 1);

                FlushBarriers(
                    pass,
                    [
                        new UavBarrier(rasterBinMeta),
                        new UavBarrier(deformBinMeta),
                    ]);
                pass.BufferBarrier(
                    binningDispatchArgs,
                    ResourceState.UnorderedAccess,
                    ResourceState.IndirectArgument);

                pass.SetPipeline(context.GetPipeline(_combinedCountPipeline));
                pass.SetParameters(_combinedChainSet, parameters);
                pass.DispatchIndirect(binningDispatchArgs);

                FlushBarriers(
                    pass,
                    [
                        new UavBarrier(rasterBinMeta),
                        new UavBarrier(deformBinMeta),
                    ]);

                pass.SetPipeline(context.GetPipeline(_combinedReservePipeline));
                pass.SetParameters(_combinedChainSet, parameters);
                pass.Dispatch(checked((maxBins + ReserveBlockSize - 1u) / ReserveBlockSize), 1, 1);

                FlushBarriers(
                    pass,
                    [
                        new UavBarrier(rasterBinMeta),
                        new UavBarrier(deformBinMeta),
                    ]);

                pass.SetPipeline(context.GetPipeline(_combinedScatterPipeline));
                pass.SetParameters(_combinedChainSet, parameters);
                pass.DispatchIndirect(binningDispatchArgs);
            });
    }

    private static void FlushBarriers(IComputeCommands pass, ReadOnlySpan<UavBarrier> barriers)
    {
        if (!barriers.IsEmpty)
            pass.Barrier(barriers);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RasterBinPass));
    }
}


