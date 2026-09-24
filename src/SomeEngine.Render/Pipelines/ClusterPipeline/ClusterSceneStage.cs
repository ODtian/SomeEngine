using System.Runtime.InteropServices;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal sealed class ClusterCullPass
{
    private readonly RenderContext _renderContext;
    private readonly ShaderBindingTable _singleLayout;
    private readonly ShaderBindingTable _phase1Layout;
    private readonly ShaderBindingTable _phase2Layout;
    private PipelineTicket _singlePipeline;
    private PipelineTicket _phase1Pipeline;
    private PipelineTicket _phase2Pipeline;
    private bool _disposed;

    public ClusterCullPass(RenderContext renderContext, Shader shader)
    {
        _renderContext = renderContext ?? throw new ArgumentNullException(nameof(renderContext));
        IDevice device = renderContext.GraphicsDevice ?? throw new InvalidOperationException("cluster cull stage requires an initialized render context.");
        _singleLayout = ShaderBindings.Create(
            device,
            "Cluster Cull Main",
            shader,
            [
                "Uniforms",
                "PageHeap",
                "CandidateClusters",
                "CandidateCount",
                "DrawArgs",
                "VisibleClusters",
                "Instances",
                "InstanceHeaders",
            ],
            "main_single");
        _singlePipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _singleLayout,
            "main_single",
            "Cluster Cull Main",
            source: ClusterSources.Builtins);
        _phase1Layout = ShaderBindings.Create(
            device,
            "Cluster Cull Phase1",
            shader,
            [
                "Uniforms",
                "PageHeap",
                "CandidateClusters",
                "CandidateCount",
                "DrawArgs",
                "VisibleClusters",
                "HiZTexture",
                "Phase2CandidateClusters",
                "Phase2CandidateCount",
                "Phase2CandidateArgs",
                "Instances",
                "InstanceHeaders",
            ],
            "main_phase1");
        _phase1Pipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _phase1Layout,
            "main_phase1",
            "Cluster Cull Phase1",
            source: ClusterSources.Builtins);
        _phase2Layout = ShaderBindings.Create(
            device,
            "Cluster Cull Phase2",
            shader,
            [
                "Uniforms",
                "PageHeap",
                "CandidateClusters",
                "CandidateCount",
                "DrawArgs",
                "VisibleClusters",
                "HiZTexture",
                "Phase2DrawArgs",
                "Instances",
                "InstanceHeaders",
            ],
            "main_phase2");
        _phase2Pipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _phase2Layout,
            "main_phase2",
            "Cluster Cull Phase2",
            source: ClusterSources.Builtins);
    }

    public void AddTickets(List<PipelineTicket> tickets)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        tickets.Add(_singlePipeline);
        tickets.Add(_phase1Pipeline);
        tickets.Add(_phase2Pipeline);
    }

    public ClusterCullOutput AddCull1(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterTraverseOutput traverse,
        RenderGraphHandle hiZTexture,
        bool enablePhase2 = true)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!buffers.PageHeap.IsValid || !instances.Transform.IsValid || !instances.Header.IsValid)
            throw new ArgumentException("cluster cull phase1 requires valid cluster buffers and instance frame.", nameof(buffers));
        if (!traverse.CandidateClusters.IsValid
            || !traverse.CandidateArgs.IsValid
            || !traverse.CullingUniforms.IsValid
            || !traverse.IndirectDrawArgs.IsValid)
        {
            throw new ArgumentException("cluster cull requires valid traverse output.", nameof(traverse));
        }
        if (!hiZTexture.IsValid)
            throw new ArgumentException("cluster cull phase1 requires a valid HiZ texture.", nameof(hiZTexture));

        uint maxDraws = ClusterLimits.MaxDraws;
        var visibleClusters = graph.CreateBuffer(
            "VisibleClusters",
            new BufferDesc
            {
                Name = "VisibleClusters",
                SizeInBytes = maxDraws * 16ul,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                InitialState = ResourceState.UnorderedAccess,
                StrideInBytes = 16,
            });
        RenderGraphHandle phase2CandidateClusters = RenderGraphHandle.Invalid;
        RenderGraphHandle phase2CandidateCount = RenderGraphHandle.Invalid;
        RenderGraphHandle phase2CandidateArgs = RenderGraphHandle.Invalid;
        RenderGraphHandle phase2DrawArgs = RenderGraphHandle.Invalid;
        if (enablePhase2)
        {
            phase2CandidateClusters = graph.CreateBuffer(
                "Phase2CandidateClusters",
                new BufferDesc
                {
                    Name = "Phase2CandidateClusters",
                    SizeInBytes = maxDraws * 12ul,
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                    InitialState = ResourceState.UnorderedAccess,
                    StrideInBytes = 12,
                });
            phase2CandidateCount = graph.CreateBuffer(
                "Phase2CandidateCount",
                new BufferDesc
                {
                    Name = "Phase2CandidateCount",
                    SizeInBytes = sizeof(uint),
                    BindFlags = BindFlags.UnorderedAccess
                        | BindFlags.ShaderResource
                        | BindFlags.CopyDestination,
                    InitialState = ResourceState.CopyDestination,
                    StrideInBytes = sizeof(uint),
                },
                ResourceLifetime.Transient);
            phase2CandidateArgs = RawArgs(graph, "Phase2CandidateArgs", 16, ResourceState.CopyDestination, ResourceLifetime.Transient);
            phase2DrawArgs = RawArgs(graph, "Phase2DrawArgs", 256, ResourceState.CopyDestination, ResourceLifetime.Transient);
        }
        if (enablePhase2)
        {
            CullPhaseOne(
                graph,
                buffers,
                instances,
                traverse,
                hiZTexture,
                visibleClusters,
                phase2CandidateClusters,
                phase2CandidateCount,
                phase2CandidateArgs,
                phase2DrawArgs);
        }
        else
        {
            AddCull(
                graph,
                buffers,
                instances,
                traverse,
                visibleClusters);
        }

        return new ClusterCullOutput(
            visibleClusters,
            traverse.IndirectDrawArgs,
            phase2DrawArgs,
            phase2CandidateClusters,
            phase2CandidateCount,
            phase2CandidateArgs);
    }

    public void AddCull2(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterTraverseOutput traverse,
        ClusterCullOutput cull,
        RenderGraphHandle hiZTexture,
        RenderGraphHandle cullingUniforms = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!buffers.PageHeap.IsValid || !instances.Transform.IsValid || !instances.Header.IsValid)
            throw new ArgumentException("cluster cull phase2 requires valid cluster buffers and instance frame.", nameof(buffers));
        if (!cull.Phase2CandidateClusters.IsValid || !cull.Phase2CandidateArgs.IsValid)
            throw new ArgumentException("cluster cull phase2 requires valid phase2 candidate output.", nameof(cull));
        if (!hiZTexture.IsValid)
            throw new ArgumentException("cluster cull phase2 requires a valid HiZ texture.", nameof(hiZTexture));

        var phase2CullingUniforms = cullingUniforms.IsValid ? cullingUniforms : traverse.CullingUniforms;
        if (!phase2CullingUniforms.IsValid)
            throw new ArgumentException("cluster cull phase2 requires valid culling uniforms.", nameof(cullingUniforms));

        CullPhaseTwo(graph, buffers, instances, traverse, cull, hiZTexture, phase2CullingUniforms);
    }

    public void Dispose(PipelineCache store)
    {
        if (_disposed)
            return;
        ArgumentNullException.ThrowIfNull(store);

        IDevice? device = _renderContext.GraphicsDevice;
        store.ReleaseIdle(ref _phase2Pipeline);
        store.ReleaseIdle(ref _phase1Pipeline);
        store.ReleaseIdle(ref _singlePipeline);
        ShaderBindings.Destroy(device, _phase2Layout);
        ShaderBindings.Destroy(device, _phase1Layout);
        ShaderBindings.Destroy(device, _singleLayout);
        _disposed = true;
    }

    private static RenderGraphHandle RawArgs(RenderGraph graph, string name, ulong sizeInBytes)
        => RawArgs(graph, name, sizeInBytes, ResourceState.Undefined);

    private static RenderGraphHandle RawArgs(
        RenderGraph graph,
        string name,
        ulong sizeInBytes,
        ResourceState initialState)
        => RawArgs(
            graph,
            name,
            sizeInBytes,
            initialState,
            initialState == ResourceState.CopyDestination
                ? ResourceLifetime.Transient
                : ResourceLifetime.Pooled);

    private static RenderGraphHandle RawArgs(
        RenderGraph graph,
        string name,
        ulong sizeInBytes,
        ResourceState initialState,
        ResourceLifetime lifetime)
        => graph.CreateBuffer(
            name,
            new BufferDesc
            {
                Name = name,
                SizeInBytes = sizeInBytes,
                BindFlags = BindFlags.UnorderedAccess
                    | BindFlags.ShaderResource
                    | BindFlags.IndirectArgument
                    | BindFlags.CopyDestination
                    | BindFlags.CopySource,
                InitialState = initialState,
                Raw = true,
            },
            lifetime);

    private void AddCull(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterTraverseOutput traverse,
        RenderGraphHandle visibleClusters)
    {
        BufferUploadPasses.AddUploadPass(
            graph,
            "Clear Cluster Cull Args",
            traverse.IndirectDrawArgs,
            0,
            CullDrawArgsBytes());
        graph.AddComputePass(
            "Cluster Cull Main",
            builder =>
            {
                builder.Read(traverse.CandidateClusters, ResourceState.ShaderResource);
                builder.Read(traverse.CandidateArgs, ResourceState.IndirectArgument);
                builder.Read(traverse.CandidateCount, ResourceState.ShaderResource);
                builder.Read(traverse.CullingUniforms, ResourceState.ConstantBuffer);
                builder.Read(instances.Transform, ResourceState.ShaderResource);
                builder.Read(instances.Header, ResourceState.ShaderResource);
                builder.Read(buffers.PageHeap, ResourceState.ShaderResource);
                builder.Write(visibleClusters, ResourceState.UnorderedAccess);
                builder.ReadWrite(traverse.IndirectDrawArgs, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                var uniforms = _singleLayout.GetRequired("Uniforms");
                var pageHeap = _singleLayout.GetRequired("PageHeap");
                var candidates = _singleLayout.GetRequired("CandidateClusters");
                var candidateCount = _singleLayout.GetRequired("CandidateCount");
                var drawArgs = _singleLayout.GetRequired("DrawArgs");
                var visible = _singleLayout.GetRequired("VisibleClusters");
                var instanceBinding = _singleLayout.GetRequired("Instances");
                var headers = _singleLayout.GetRequired("InstanceHeaders");
                uint set = BindInput.GetSetIndex(
                    "cluster cull pass",
                    uniforms,
                    pageHeap,
                    candidates,
                    candidateCount,
                    drawArgs,
                    visible,
                    instanceBinding,
                    headers);

                pass.SetPipeline(context.GetPipeline(_singlePipeline));
                pass.SetParameters(
                    set,
                    context.Bindings(_singleLayout.Layout(set))
                        .Reserve(8)
                        .Buffer(uniforms, traverse.CullingUniforms)
                        .Buffer(pageHeap, buffers.PageHeap)
                        .Buffer(candidates, traverse.CandidateClusters)
                        .Buffer(candidateCount, traverse.CandidateCount)
                        .Buffer(drawArgs, traverse.IndirectDrawArgs)
                        .Buffer(visible, visibleClusters)
                        .Buffer(instanceBinding, instances.Transform)
                        .Buffer(headers, instances.Header));
                pass.DispatchIndirect(traverse.CandidateArgs);
            });
    }

    private void CullPhaseOne(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterTraverseOutput traverse,
        RenderGraphHandle hiZTexture,
        RenderGraphHandle visibleClusters,
        RenderGraphHandle phase2CandidateClusters,
        RenderGraphHandle phase2CandidateCount,
        RenderGraphHandle phase2CandidateArgs,
        RenderGraphHandle phase2DrawArgs)
    {
        var clears = new BufferUploadBatch(graph, "Clear Cluster Cull Args");
        clears.AddUpload(traverse.IndirectDrawArgs, 0, CullDrawArgsBytes());
        clears.AddUpload(phase2CandidateCount, 0, new byte[sizeof(uint)]);
        clears.AddUpload(phase2CandidateArgs, 0, DispatchArgsBytes(0u, 1u, 1u));
        clears.AddUpload(phase2DrawArgs, 0, CullDrawArgsBytes());
        clears.AddPass();
        graph.AddComputePass(
            "Cluster Cull Phase1",
            builder =>
            {
                builder.Read(traverse.CandidateClusters, ResourceState.ShaderResource);
                builder.Read(traverse.CandidateArgs, ResourceState.IndirectArgument);
                builder.Read(traverse.CandidateCount, ResourceState.ShaderResource);
                builder.Read(traverse.CullingUniforms, ResourceState.ConstantBuffer);
                builder.Read(instances.Transform, ResourceState.ShaderResource);
                builder.Read(instances.Header, ResourceState.ShaderResource);
                builder.Read(buffers.PageHeap, ResourceState.ShaderResource);
                builder.Read(hiZTexture, ResourceState.ShaderResource);
                builder.Write(visibleClusters, ResourceState.UnorderedAccess);
                builder.ReadWrite(traverse.IndirectDrawArgs, ResourceState.UnorderedAccess);
                builder.Write(phase2CandidateClusters, ResourceState.UnorderedAccess);
                builder.ReadWrite(phase2CandidateCount, ResourceState.UnorderedAccess);
                builder.ReadWrite(phase2CandidateArgs, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                var uniforms = _phase1Layout.GetRequired("Uniforms");
                var pageHeap = _phase1Layout.GetRequired("PageHeap");
                var candidates = _phase1Layout.GetRequired("CandidateClusters");
                var candidateCount = _phase1Layout.GetRequired("CandidateCount");
                var drawArgs = _phase1Layout.GetRequired("DrawArgs");
                var visible = _phase1Layout.GetRequired("VisibleClusters");
                var hiZ = _phase1Layout.GetRequired("HiZTexture");
                var phase2Candidates = _phase1Layout.GetRequired("Phase2CandidateClusters");
                var phase2Count = _phase1Layout.GetRequired("Phase2CandidateCount");
                var phase2Args = _phase1Layout.GetRequired("Phase2CandidateArgs");
                var instanceBinding = _phase1Layout.GetRequired("Instances");
                var headers = _phase1Layout.GetRequired("InstanceHeaders");
                uint set = BindInput.GetSetIndex(
                    "cluster cull pass",
                    uniforms,
                    pageHeap,
                    candidates,
                    candidateCount,
                    drawArgs,
                    visible,
                    hiZ,
                    phase2Candidates,
                    phase2Count,
                    phase2Args,
                    instanceBinding,
                    headers);

                pass.SetPipeline(context.GetPipeline(_phase1Pipeline));
                pass.SetParameters(
                    set,
                    context.Bindings(_phase1Layout.Layout(set))
                        .Reserve(11)
                        .Buffer(uniforms, traverse.CullingUniforms)
                        .Buffer(pageHeap, buffers.PageHeap)
                        .Buffer(candidates, traverse.CandidateClusters)
                        .Buffer(candidateCount, traverse.CandidateCount)
                        .Buffer(drawArgs, traverse.IndirectDrawArgs)
                        .Buffer(visible, visibleClusters)
                        .Texture(hiZ, HiZView(context, hiZTexture))
                        .Buffer(phase2Candidates, phase2CandidateClusters)
                        .Buffer(phase2Count, phase2CandidateCount)
                        .Buffer(phase2Args, phase2CandidateArgs)
                        .Buffer(instanceBinding, instances.Transform)
                        .Buffer(headers, instances.Header));
                pass.DispatchIndirect(traverse.CandidateArgs);
            });
    }

    private void CullPhaseTwo(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterTraverseOutput traverse,
        ClusterCullOutput cull,
        RenderGraphHandle hiZTexture,
        RenderGraphHandle cullingUniforms)
    {
        graph.AddComputePass(
            "Cluster Cull Phase2",
            builder =>
            {
                builder.Read(cull.Phase2CandidateClusters, ResourceState.ShaderResource);
                builder.Read(cull.Phase2CandidateArgs, ResourceState.IndirectArgument);
                builder.Read(cull.Phase2CandidateCount, ResourceState.ShaderResource);
                builder.Read(cullingUniforms, ResourceState.ConstantBuffer);
                builder.Read(instances.Transform, ResourceState.ShaderResource);
                builder.Read(instances.Header, ResourceState.ShaderResource);
                builder.Read(buffers.PageHeap, ResourceState.ShaderResource);
                builder.Read(hiZTexture, ResourceState.ShaderResource);
                builder.Write(cull.VisibleClusters, ResourceState.UnorderedAccess);
                builder.ReadWrite(cull.DrawArgs, ResourceState.UnorderedAccess);
                builder.ReadWrite(cull.Phase2DrawArgs, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                var uniforms = _phase2Layout.GetRequired("Uniforms");
                var pageHeap = _phase2Layout.GetRequired("PageHeap");
                var candidates = _phase2Layout.GetRequired("CandidateClusters");
                var candidateCount = _phase2Layout.GetRequired("CandidateCount");
                var drawArgs = _phase2Layout.GetRequired("DrawArgs");
                var visible = _phase2Layout.GetRequired("VisibleClusters");
                var hiZ = _phase2Layout.GetRequired("HiZTexture");
                var phase2DrawArgs = _phase2Layout.GetRequired("Phase2DrawArgs");
                var instanceBinding = _phase2Layout.GetRequired("Instances");
                var headers = _phase2Layout.GetRequired("InstanceHeaders");
                uint set = BindInput.GetSetIndex(
                    "cluster cull pass",
                    uniforms,
                    pageHeap,
                    candidates,
                    candidateCount,
                    drawArgs,
                    visible,
                    hiZ,
                    phase2DrawArgs,
                    instanceBinding,
                    headers);

                pass.SetPipeline(context.GetPipeline(_phase2Pipeline));
                pass.SetParameters(
                    set,
                    context.Bindings(_phase2Layout.Layout(set))
                        .Reserve(10)
                        .Buffer(uniforms, cullingUniforms)
                        .Buffer(pageHeap, buffers.PageHeap)
                        .Buffer(candidates, cull.Phase2CandidateClusters)
                        .Buffer(candidateCount, cull.Phase2CandidateCount)
                        .Buffer(drawArgs, cull.DrawArgs)
                        .Buffer(visible, cull.VisibleClusters)
                        .Texture(hiZ, HiZView(context, hiZTexture))
                        .Buffer(phase2DrawArgs, cull.Phase2DrawArgs)
                        .Buffer(instanceBinding, instances.Transform)
                        .Buffer(headers, instances.Header));
                pass.DispatchIndirect(cull.Phase2CandidateArgs);
            });
    }

    private static TextureViewHandle HiZView(
        RenderGraphContext context,
        RenderGraphHandle hiZTexture)
    {
        TextureDesc desc = context.GetTextureDesc(hiZTexture);
        if (desc.MipLevels == 0)
            throw new InvalidOperationException("HiZ texture must expose at least one mip level.");

        return context.GetTextureView(
            hiZTexture,
            ViewKind.ShaderResource,
            Format.R32Float,
            mipCount: desc.MipLevels);
    }

    private static byte[] DispatchArgsBytes(uint groupX, uint groupY, uint groupZ)
    {
        Span<uint> words = stackalloc uint[4] { groupX, groupY, groupZ, 0u };
        return MemoryMarshal.AsBytes(words).ToArray();
    }

    private static byte[] CullDrawArgsBytes()
    {
        byte[] data = new byte[256];
        Span<byte> bytes = data;
        Span<uint> words = MemoryMarshal.Cast<byte, uint>(bytes);
        words[0] = 96u;
        return data;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ClusterCullPass));
    }
}

internal struct ClusterSceneStage
{
    private BvhPatchPass? _bvhPatch;
    private ClusterTraversePass? _traverse;
    private ClusterCullPass? _cull;

    public ClusterSceneStage(
        RenderContext renderContext,
        Shader bvhPatchShader,
        Shader traverseShader,
        Shader cullShader)
    {
        _bvhPatch = new BvhPatchPass(renderContext, bvhPatchShader);
        _traverse = new ClusterTraversePass(renderContext, traverseShader);
        _cull = new ClusterCullPass(renderContext, cullShader);
    }

    public void AddTickets(List<PipelineTicket> tickets)
    {
        ThrowIfDisposed();
        _bvhPatch!.AddTickets(tickets);
        _traverse!.AddTickets(tickets);
        _cull!.AddTickets(tickets);
    }

    public bool AddPatches(
        RenderGraph graph,
        RenderGraphHandle globalBvh,
        IReadOnlyList<ClusterBvhPatch> patches)
    {
        ThrowIfDisposed();
        return _bvhPatch!.AddTo(graph, globalBvh, patches);
    }

    public ClusterTraverseOutput AddTraverse(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        in CullingUniforms cullingData,
        int instanceCount,
        int maxDepth,
        List<BufferCopyRequest>? copyRequests = null)
    {
        ThrowIfDisposed();
        return _traverse!.AddPasses(graph, buffers, instances, cullingData, instanceCount, maxDepth, copyRequests);
    }

    public ClusterCullOutput AddCull1(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterTraverseOutput traverse,
        RenderGraphHandle hiZTexture,
        bool enablePhase2 = true)
    {
        ThrowIfDisposed();
        return _cull!.AddCull1(graph, buffers, instances, traverse, hiZTexture, enablePhase2);
    }

    public void AddCull2(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterTraverseOutput traverse,
        ClusterCullOutput cull,
        RenderGraphHandle hiZTexture,
        RenderGraphHandle cullingUniforms = default)
    {
        ThrowIfDisposed();
        _cull!.AddCull2(graph, buffers, instances, traverse, cull, hiZTexture, cullingUniforms);
    }

    public void Dispose(PipelineCache store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _cull?.Dispose(store);
        _traverse?.Dispose(store);
        _bvhPatch?.Dispose(store);
        _cull = null;
        _traverse = null;
        _bvhPatch = null;
    }

    private void ThrowIfDisposed()
    {
        if (_bvhPatch == null || _traverse == null || _cull == null)
            throw new ObjectDisposedException(nameof(ClusterSceneStage));
    }
}
