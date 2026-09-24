using System.Runtime.InteropServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal sealed class ClusterTraversePass
{
    private readonly RenderContext _renderContext;
    private readonly ShaderBindingTable _traverseLayout;
    private readonly ReflectedBinding _traverseGlobalBvh;
    private readonly ReflectedBinding _traverseUniforms;
    private readonly ReflectedBinding _traverseCandidates;
    private readonly ReflectedBinding _traverseCandidateCount;
    private readonly ReflectedBinding _traverseCandidateArgs;
    private readonly ReflectedBinding _traversePageFault;
    private readonly ReflectedBinding _traverseInstances;
    private readonly ReflectedBinding _traverseHeaders;
    private readonly uint _traverseSet;
    private readonly UniformGpu<CullingUniforms> _uniforms;
    private PipelineTicket _traversePipeline;
    private bool _disposed;

    public ClusterTraversePass(RenderContext renderContext, Shader shader)
    {
        _renderContext = renderContext ?? throw new ArgumentNullException(nameof(renderContext));
        IDevice device = renderContext.GraphicsDevice ?? throw new InvalidOperationException("cluster traverse stage requires an initialized render context.");
        _traverseLayout = ShaderBindings.Create(
            device,
            "Cluster BVH Traverse",
            shader,
            [
                "GlobalBVH",
                "Uniforms",
                "CandidateClusters",
                "CandidateCount",
                "CandidateArgs",
                "PageFaultBuffer",
                "Instances",
                "InstanceHeaders",
            ],
            "main");
        _traverseGlobalBvh = _traverseLayout.GetRequired("GlobalBVH");
        _traverseUniforms = _traverseLayout.GetRequired("Uniforms");
        _traverseCandidates = _traverseLayout.GetRequired("CandidateClusters");
        _traverseCandidateCount = _traverseLayout.GetRequired("CandidateCount");
        _traverseCandidateArgs = _traverseLayout.GetRequired("CandidateArgs");
        _traversePageFault = _traverseLayout.GetRequired("PageFaultBuffer");
        _traverseInstances = _traverseLayout.GetRequired("Instances");
        _traverseHeaders = _traverseLayout.GetRequired("InstanceHeaders");
        _traverseSet = BindInput.GetSetIndex(
            "cluster BVH pass",
            _traverseGlobalBvh,
            _traverseUniforms,
            _traverseCandidates,
            _traverseCandidateCount,
            _traverseCandidateArgs,
            _traversePageFault,
            _traverseInstances,
            _traverseHeaders);
        _traversePipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _traverseLayout,
            "main",
            "Cluster BVH Traverse",
            source: ClusterSources.Builtins);
        _uniforms = new UniformGpu<CullingUniforms>(device);
    }

    public void AddTickets(List<PipelineTicket> tickets)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        tickets.Add(_traversePipeline);
    }

    public ClusterTraverseOutput AddPasses(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        in CullingUniforms cullingData,
        int instanceCount,
        int maxDepth,
        List<BufferCopyRequest>? copyRequests = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!buffers.GlobalBVH.IsValid
            || !buffers.PageFault.IsValid
            || !instances.Transform.IsValid
            || !instances.Header.IsValid)
        {
            throw new ArgumentException("cluster traverse requires valid cluster buffers and instance frame.", nameof(buffers));
        }
        if (instanceCount < 0)
            throw new ArgumentOutOfRangeException(nameof(instanceCount), "cluster traverse instance count must not be negative.");
        if (maxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "cluster traverse max depth must not be negative.");

        uint maxDraws = ClusterLimits.MaxDraws;
        var traverseUniforms = cullingData;
        traverseUniforms.MaxTraversalDepth = checked((uint)maxDepth);
        RenderGraphHandle cullingUniforms;
        using (Profiler.BeginScope("ClusterTraversePass.Uniforms"))
        {
            cullingUniforms = _uniforms.Add(graph, [traverseUniforms], "Cluster CullingUniforms").Buffer;
        }

        RenderGraphHandle candidateClusters;
        RenderGraphHandle candidateCount;
        RenderGraphHandle candidateArgs;
        RenderGraphHandle indirectDrawArgs;
        using (Profiler.BeginScope("ClusterTraversePass.Targets"))
        {
            candidateClusters = graph.CreateBuffer(
                "CandidateClusters",
                new BufferDesc
                {
                    Name = "CandidateClusters",
                    SizeInBytes = maxDraws * 12ul,
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                    InitialState = ResourceState.UnorderedAccess,
                    StrideInBytes = 12,
                });
            candidateCount = graph.CreateBuffer(
                "CandidateCount",
                new BufferDesc
                {
                    Name = "CandidateCount",
                    SizeInBytes = sizeof(uint),
                    BindFlags = BindFlags.UnorderedAccess
                        | BindFlags.ShaderResource
                        | BindFlags.CopyDestination,
                    InitialState = ResourceState.CopyDestination,
                    StrideInBytes = sizeof(uint),
                },
                ResourceLifetime.Transient);
            candidateArgs = RawArgs(graph, "CandidateArgs", 16, ResourceState.CopyDestination, ResourceLifetime.Transient);
            indirectDrawArgs = RawArgs(graph, "IndirectDrawArgs", 256, ResourceState.CopyDestination, ResourceLifetime.Transient);
        }

        bool hasTraversal = instanceCount > 0 && maxDepth > 0;
        if (hasTraversal)
        {
            var clears = new BufferUploadBatch(graph, "Clear Cluster Traverse Args");
            clears.AddUpload(candidateCount, 0, new byte[sizeof(uint)]);
            clears.AddUpload(candidateArgs, 0, DispatchArgsBytes(0u, 1u, 1u));
            clears.AddUpload(buffers.PageFault, 0, new byte[checked((int)PageFaults.ByteCount)]);
            if (copyRequests == null)
            {
                clears.AddPass();
            }
            else
            {
                clears.AddCopiesTo(copyRequests);
                if (copyRequests.Count != 0)
                {
                    BufferCopyPasses.AddCopyBatch(graph, "Cluster Frame Uploads", [.. copyRequests]);
                    copyRequests.Clear();
                }
            }
            using (Profiler.BeginScope("ClusterTraversePass.Layers"))
            {
                AddTraverseLayers(
                    graph,
                    cullingUniforms,
                    buffers,
                    instances,
                    candidateClusters,
                    candidateCount,
                    candidateArgs,
                    instanceCount);
            }
        }

        return new ClusterTraverseOutput(
            candidateClusters,
            candidateCount,
            candidateArgs,
            cullingUniforms,
            indirectDrawArgs);
    }

    public void Dispose(PipelineCache store)
    {
        if (_disposed)
            return;
        ArgumentNullException.ThrowIfNull(store);

        IDevice? device = _renderContext.GraphicsDevice;
        store.ReleaseIdle(ref _traversePipeline);
        ShaderBindings.Destroy(device, _traverseLayout);
        _uniforms.Dispose();
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

    private static byte[] DispatchArgsBytes(uint groupX, uint groupY, uint groupZ)
    {
        Span<uint> words = stackalloc uint[4] { groupX, groupY, groupZ, 0u };
        return MemoryMarshal.AsBytes(words).ToArray();
    }

    private void AddTraverseLayers(
        RenderGraph graph,
        RenderGraphHandle cullingUniforms,
        ClusterBuffers buffers,
        InstanceFrame instances,
        RenderGraphHandle candidateClusters,
        RenderGraphHandle candidateCount,
        RenderGraphHandle candidateArgs,
        int instanceCount)
    {
        graph.AddComputePass(
            "Cluster BVH Traverse",
            builder =>
            {
                builder.Read(buffers.GlobalBVH, ResourceState.ShaderResource);
                builder.Read(cullingUniforms, ResourceState.ConstantBuffer);
                builder.Read(instances.Transform, ResourceState.ShaderResource);
                builder.Read(instances.Header, ResourceState.ShaderResource);
                builder.Write(candidateClusters, ResourceState.UnorderedAccess);
                builder.ReadWrite(candidateCount, ResourceState.UnorderedAccess);
                builder.ReadWrite(candidateArgs, ResourceState.UnorderedAccess);
                builder.ReadWrite(buffers.PageFault, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                pass.SetPipeline(context.GetPipeline(_traversePipeline));
                pass.SetParameters(
                    _traverseSet,
                    context.Bindings(_traverseLayout.Layout(_traverseSet))
                        .Reserve(8)
                        .Buffer(_traverseGlobalBvh, buffers.GlobalBVH)
                        .Buffer(_traverseUniforms, cullingUniforms)
                        .Buffer(_traverseCandidates, candidateClusters)
                        .Buffer(_traverseCandidateCount, candidateCount)
                        .Buffer(_traverseCandidateArgs, candidateArgs)
                        .Buffer(_traversePageFault, buffers.PageFault)
                        .Buffer(_traverseInstances, instances.Transform)
                        .Buffer(_traverseHeaders, instances.Header));
                pass.Dispatch(checked((uint)((instanceCount + 63) / 64)), 1, 1);
            });
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ClusterTraversePass));
    }
}



