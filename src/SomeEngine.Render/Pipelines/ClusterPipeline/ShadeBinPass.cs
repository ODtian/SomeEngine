using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal sealed class ShadeBinPass
{
    private const uint ReserveBlockSize = 128;
    private const int ShadeResourceCount = 15;

    private readonly RenderContext _renderContext;
    private readonly ShaderBindingTable _clearPrepareLayout;
    private readonly ShaderBindingTable _layout;
    private readonly BindingStep[] _clearPrepareSteps;
    private readonly BindingStep[] _chainSteps;
    private readonly UniformGpu<ShadeBinUniforms> _uniforms;
    private PipelineTicket _clearPreparePipeline;
    private PipelineTicket _countPipeline;
    private PipelineTicket _reservePipeline;
    private PipelineTicket _scatterPipeline;
    private bool _disposed;

    public ShadeBinPass(RenderContext renderContext, Shader shader)
    {
        _renderContext = renderContext ?? throw new ArgumentNullException(nameof(renderContext));
        IDevice device = renderContext.GraphicsDevice ?? throw new InvalidOperationException("cluster shade bin pass requires an initialized render context.");
        _clearPrepareLayout = ShaderBindings.Create(
            device,
            "Cluster Shade Bin Clear Prepare",
            shader,
            ["Uniforms", "BinCounts", "BinScatterCount", "ReserveCounters"],
            "CSBinClearPrepare");
        _clearPreparePipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _clearPrepareLayout,
            "CSBinClearPrepare",
            "Cluster Shade Bin Clear Prepare",
            source: ClusterSources.Builtins);
        _clearPrepareSteps = CreateSteps(_clearPrepareLayout.Bindings);
        _layout = ShaderBindings.Create(
            device,
            "Cluster Shade Bin Chain",
            shader,
            "CSBinCount",
            "CSBinReserve",
            "CSBinScatter");
        _chainSteps = CreateSteps(_layout.Bindings);
        _countPipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _layout,
            "CSBinCount",
            "Cluster Shade Bin Count",
            source: ClusterSources.Builtins);
        _reservePipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _layout,
            "CSBinReserve",
            "Cluster Shade Bin Reserve",
            source: ClusterSources.Builtins);
        _scatterPipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _layout,
            "CSBinScatter",
            "Cluster Shade Bin Scatter",
            source: ClusterSources.Builtins);
        _uniforms = new UniformGpu<ShadeBinUniforms>(device);
    }

    public void AddTickets(List<PipelineTicket> tickets)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        tickets.Add(_clearPreparePipeline);
        tickets.Add(_countPipeline);
        tickets.Add(_reservePipeline);
        tickets.Add(_scatterPipeline);
    }

    public ShadeBinFrame AddPasses(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterRasterOutput raster,
        ClusterCullOutput cull,
        RenderGraphHandle slotBuffer,
        uint slotCapacity,
        uint shadingBinFieldIndex,
        uint shadingBinCount,
        uint width,
        uint height)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!buffers.PageHeap.IsValid)
            throw new ArgumentException("shade binning requires valid global page heap.", nameof(buffers));
        if (!raster.VisBuffer.IsValid)
            throw new ArgumentException("shade binning requires a valid vis buffer.", nameof(raster));
        if (!cull.VisibleClusters.IsValid)
            throw new ArgumentException("shade binning requires valid cull output.", nameof(cull));
        if (!slotBuffer.IsValid)
            throw new ArgumentException("shade binning requires a valid material slot buffer.", nameof(slotBuffer));
        if (slotCapacity == 0)
            throw new ArgumentOutOfRangeException(nameof(slotCapacity), "shade binning slot capacity must be non-zero.");

        uint binCount = Math.Max(shadingBinCount, 1u);
        uint screenWidth = Math.Max(width, 1u);
        uint screenHeight = Math.Max(height, 1u);
        uint pixelCapacity = checked(screenWidth * screenHeight);
        var pixelCoordBuffer = ClusterBinGpu.Uint(
            graph,
            "ShadePixelCoordBuffer",
            pixelCapacity,
            copy: false);
        var binOffsets = ClusterBinGpu.Uint(graph, "ShadeBinOffsets", binCount, copy: false);
        var binCounts = ClusterBinGpu.Uint(
            graph,
            "ShadeBinCounts",
            binCount,
            copy: false,
            lifetime: ResourceLifetime.Transient);
        var binScatterCount = ClusterBinGpu.Uint(
            graph,
            "ShadeBinScatterCount",
            binCount,
            copy: false,
            lifetime: ResourceLifetime.Transient);
        var binIndirectArgs = ClusterBinGpu.Args(
            graph,
            "ShadeBinIndirectArgs",
            binCount * IndirectArgumentSize.Dispatch);
        var reserveCounters = ClusterBinGpu.Uint(
            graph,
            "ShadeBinReserveCounters",
            1,
            copy: false,
            lifetime: ResourceLifetime.Transient);
        var uniforms = _uniforms.Add(
            graph,
            [
                new ShadeBinUniforms
                {
                    ScreenWidth = screenWidth,
                    ScreenHeight = screenHeight,
                    MaterialCount = binCount,
                    SlotCapacity = slotCapacity,
                    BinFieldIndex = shadingBinFieldIndex,
                }
            ],
            "ShadeBinUniforms").Buffer;
        var resources = new ShadeBinRes(
            uniforms,
            raster.VisBuffer,
            cull.VisibleClusters,
            slotBuffer,
            instances.Transform,
            instances.PrevTransform,
            instances.Header,
            instances.Data,
            buffers.PageHeap,
            binCounts,
            binOffsets,
            binIndirectArgs,
            binScatterCount,
            reserveCounters,
            pixelCoordBuffer);

        AddBinChain(graph, resources, screenWidth, screenHeight, binCount);

        return new ShadeBinFrame(pixelCoordBuffer, binOffsets, binCounts, binIndirectArgs);
    }

    public void Dispose(PipelineCache store)
    {
        if (_disposed)
            return;
        ArgumentNullException.ThrowIfNull(store);

        IDevice? device = _renderContext.GraphicsDevice;
        store.ReleaseIdle(ref _clearPreparePipeline);
        store.ReleaseIdle(ref _scatterPipeline);
        store.ReleaseIdle(ref _reservePipeline);
        store.ReleaseIdle(ref _countPipeline);
        ShaderBindings.Destroy(device, _clearPrepareLayout);
        ShaderBindings.Destroy(device, _layout);
        _uniforms.Dispose();
        _disposed = true;
    }

    private void AddBinChain(
        RenderGraph graph,
        ShadeBinRes resources,
        uint width,
        uint height,
        uint binCount)
    {
        graph.AddComputePass(
            "Cluster Shade Bin Chain",
            builder =>
            {
                AddUseUnion(builder, resources, _clearPrepareSteps, _chainSteps);
            },
            (context, pass) =>
            {
                var clearBindings = BuildBindings(context, _clearPrepareLayout, _clearPrepareSteps, resources);
                var bindings = BuildBindings(context, _layout, _chainSteps, resources);

                pass.SetPipeline(context.GetPipeline(_clearPreparePipeline));
                pass.SetParameters(clearBindings.Set, clearBindings.Parameters);
                uint clearGroups = checked((binCount + ReserveBlockSize - 1u) / ReserveBlockSize);
                pass.Dispatch(clearGroups, 1, 1);

                pass.Barrier(
                    [
                        new UavBarrier(resources.BinCounts),
                        new UavBarrier(resources.ReserveCounters),
                    ]);

                pass.SetPipeline(context.GetPipeline(_countPipeline));
                pass.SetParameters(bindings.Set, bindings.Parameters);
                uint dispatchWidth = checked((width + 7u) / 8u);
                uint dispatchHeight = checked((height + 7u) / 8u);
                pass.Dispatch(dispatchWidth, dispatchHeight, 1);

                pass.Barrier(
                    [
                        new UavBarrier(resources.BinCounts),
                        new UavBarrier(resources.ReserveCounters),
                    ]);

                pass.SetPipeline(context.GetPipeline(_reservePipeline));
                pass.SetParameters(bindings.Set, bindings.Parameters);
                uint reserveGroups = checked((binCount + ReserveBlockSize - 1u) / ReserveBlockSize);
                pass.Dispatch(
                    reserveGroups,
                    1,
                    1);

                pass.Barrier(
                    [
                        new UavBarrier(resources.BinOffsets),
                        new UavBarrier(resources.BinScatterCount),
                    ]);

                pass.SetPipeline(context.GetPipeline(_scatterPipeline));
                pass.SetParameters(bindings.Set, bindings.Parameters);
                pass.Dispatch(dispatchWidth, dispatchHeight, 1);
            });
    }

    private static void AddUseUnion(
        RenderGraphBuilder builder,
        ShadeBinRes resources,
        ReadOnlySpan<BindingStep> clearPrepareSteps,
        ReadOnlySpan<BindingStep> chainSteps)
    {
        Span<ShaderResourceUse> uses = stackalloc ShaderResourceUse[ShadeResourceCount];
        int useCount = 0;
        AddUses(resources, clearPrepareSteps, uses, ref useCount);
        AddUses(resources, chainSteps, uses, ref useCount);

        for (int i = 0; i < useCount; i++)
        {
            ShaderResourceUse use = uses[i];
            switch (use.Access)
            {
                case RenderGraphAccess.ReadOnly:
                    builder.Read(use.Handle, use.State);
                    break;
                case RenderGraphAccess.WriteOnly:
                    builder.Write(use.Handle, use.State);
                    break;
                case RenderGraphAccess.ReadWrite:
                    builder.ReadWrite(use.Handle, use.State);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"cluster shade binning produced unsupported access mode '{use.Access}'.");
            }
        }
    }

    private static void AddUses(
        ShadeBinRes resources,
        ReadOnlySpan<BindingStep> steps,
        Span<ShaderResourceUse> uses,
        ref int useCount)
    {
        for (int stepIndex = 0; stepIndex < steps.Length; stepIndex++)
        {
            BindingStep step = steps[stepIndex];
            RenderGraphHandle handle = ResolveHandle(step.Kind, resources);
            MergeUse(uses, ref useCount, handle, step.State, step.Access);
        }
    }

    private static void MergeUse(
        Span<ShaderResourceUse> uses,
        ref int useCount,
        RenderGraphHandle handle,
        ResourceState state,
        RenderGraphAccess access)
    {
        for (int i = 0; i < useCount; i++)
        {
            ShaderResourceUse existing = uses[i];
            if (!existing.Handle.Equals(handle))
                continue;

            if (existing.State == state)
            {
                uses[i] = existing with { Access = existing.Access | access };
                return;
            }

            if (existing.State == ResourceState.UnorderedAccess || state == ResourceState.UnorderedAccess)
            {
                uses[i] = existing with
                {
                    State = ResourceState.UnorderedAccess,
                    Access = RenderGraphAccess.ReadWrite,
                };
                return;
            }

            throw new InvalidOperationException(
                $"cluster shade binning resource '{handle}' is declared with incompatible read states {existing.State} and {state}.");
        }

        if (useCount >= uses.Length)
            throw new InvalidOperationException("cluster shade binning reflected more resources than its caller-side resource map supports.");

        uses[useCount++] = new ShaderResourceUse(handle, state, access);
    }

    private static RenderGraphHandle ResolveHandle(ShadeBindingKind kind, ShadeBinRes resources)
        => kind switch
        {
            ShadeBindingKind.Uniforms => resources.Uniforms,
            ShadeBindingKind.VisBuffer => resources.VisBuffer,
            ShadeBindingKind.VisibleClusters => resources.VisibleClusters,
            ShadeBindingKind.SlotBuffer => resources.SlotBuffer,
            ShadeBindingKind.Instances => resources.Instances,
            ShadeBindingKind.PreviousInstances => resources.PreviousInstances,
            ShadeBindingKind.InstanceHeaders => resources.InstanceHeaders,
            ShadeBindingKind.InstanceDataHeap => resources.InstanceDataHeap,
            ShadeBindingKind.PageHeap => resources.PageHeap,
            ShadeBindingKind.BinCounts => resources.BinCounts,
            ShadeBindingKind.BinOffsets => resources.BinOffsets,
            ShadeBindingKind.BinIndirectArgs => resources.BinIndirectArgs,
            ShadeBindingKind.BinScatterCount => resources.BinScatterCount,
            ShadeBindingKind.ReserveCounters => resources.ReserveCounters,
            ShadeBindingKind.PixelCoordBuffer => resources.PixelCoordBuffer,
            _ => throw new InvalidOperationException(
                $"cluster shade binning has no render graph resource mapping for binding kind '{kind}'."),
        };

    private static (uint Set, PassBindings Parameters) BuildBindings(
        RenderGraphContext context,
        ShaderBindingTable layout,
        ReadOnlySpan<BindingStep> steps,
        ShadeBinRes resources)
    {
        uint? bindingSet = null;
        PassBindings parameters = null!;

        for (int stepIndex = 0; stepIndex < steps.Length; stepIndex++)
        {
            BindingStep step = steps[stepIndex];
            BindInput.AddResource(
                context,
                layout,
                ref parameters,
                ref bindingSet,
                step.Binding,
                CreateBindingResource(context, step, resources),
                step.Access,
                "cluster shade binning pass",
                layout.Bindings.Length);
        }

        if (!bindingSet.HasValue)
            throw new InvalidOperationException("cluster shade binning shader reflected no bindable resources.");

        return (bindingSet.Value, parameters);
    }

    private static BindingResourceDesc CreateBindingResource(
        RenderGraphContext context,
        BindingStep step,
        ShadeBinRes resources)
        => step.Kind switch
        {
            ShadeBindingKind.Uniforms => step.Binding.Buffer(context, resources.Uniforms, step.Access),
            ShadeBindingKind.VisBuffer => step.Binding.Texture(
                context,
                resources.VisBuffer,
                new TextureViewDesc
                {
                    Kind = ViewKind.ShaderResource,
                    Dimension = TextureViewDimension.Texture2D,
                    Format = Format.R32UInt,
                    MipCount = 1,
                    SliceCount = 1,
                },
                step.Access),
            ShadeBindingKind.VisibleClusters => step.Binding.Buffer(context, resources.VisibleClusters, step.Access),
            ShadeBindingKind.SlotBuffer => step.Binding.Buffer(context, resources.SlotBuffer, step.Access),
            ShadeBindingKind.Instances => step.Binding.Buffer(context, resources.Instances, step.Access),
            ShadeBindingKind.PreviousInstances => step.Binding.Buffer(context, resources.PreviousInstances, step.Access),
            ShadeBindingKind.InstanceHeaders => step.Binding.Buffer(context, resources.InstanceHeaders, step.Access),
            ShadeBindingKind.InstanceDataHeap => step.Binding.Buffer(context, resources.InstanceDataHeap, step.Access),
            ShadeBindingKind.PageHeap => step.Binding.Buffer(context, resources.PageHeap, step.Access),
            ShadeBindingKind.BinCounts => step.Binding.Buffer(context, resources.BinCounts, step.Access),
            ShadeBindingKind.BinOffsets => step.Binding.Buffer(context, resources.BinOffsets, step.Access),
            ShadeBindingKind.BinIndirectArgs => step.Binding.Buffer(context, resources.BinIndirectArgs, step.Access),
            ShadeBindingKind.BinScatterCount => step.Binding.Buffer(context, resources.BinScatterCount, step.Access),
            ShadeBindingKind.ReserveCounters => step.Binding.Buffer(context, resources.ReserveCounters, step.Access),
            ShadeBindingKind.PixelCoordBuffer => step.Binding.Buffer(context, resources.PixelCoordBuffer, step.Access),
            _ => throw new InvalidOperationException(
                $"cluster shade binning has no caller-side resource mapping for binding kind '{step.Kind}'."),
        };

    private static ResourceState GetReadState(BindingType type)
    {
        BindStateInfo info = BindRules.Resolve(type);
        if (info.State == ResourceState.UnorderedAccess)
            throw new InvalidOperationException($"Binding type {type} is not a read-only shader resource.");
        return info.State;
    }

    private static byte[] ZeroBytes(uint uintCount)
        => new byte[checked((int)Math.Max(uintCount, 1u) * sizeof(uint))];

    private static BindingStep[] CreateSteps(IReadOnlyList<ReflectedBinding> bindings)
    {
        var steps = new BindingStep[bindings.Count];
        for (int i = 0; i < bindings.Count; i++)
        {
            ReflectedBinding binding = bindings[i];
            BindStateInfo info = BindRules.Resolve(binding.Type);
            if (info.State == ResourceState.UnorderedAccess)
            {
                steps[i] = new BindingStep(
                    binding,
                    Classify(binding.Name),
                    ResourceState.UnorderedAccess,
                    AccessForWriteTarget(binding.Name));
                continue;
            }

            steps[i] = new BindingStep(
                binding,
                Classify(binding.Name),
                GetReadState(binding.Type),
                RenderGraphAccess.ReadOnly);
        }

        return steps;
    }

    private static ShadeBindingKind Classify(string name)
        => name switch
        {
            "Uniforms" => ShadeBindingKind.Uniforms,
            "VisBuffer" => ShadeBindingKind.VisBuffer,
            "VisibleClusters" => ShadeBindingKind.VisibleClusters,
            "SlotBuffer" => ShadeBindingKind.SlotBuffer,
            "Instances" => ShadeBindingKind.Instances,
            "PreviousInstances" => ShadeBindingKind.PreviousInstances,
            "InstanceHeaders" => ShadeBindingKind.InstanceHeaders,
            "InstanceDataHeap" => ShadeBindingKind.InstanceDataHeap,
            "PageHeap" => ShadeBindingKind.PageHeap,
            "BinCounts" => ShadeBindingKind.BinCounts,
            "BinOffsets" => ShadeBindingKind.BinOffsets,
            "BinIndirectArgs" => ShadeBindingKind.BinIndirectArgs,
            "BinScatterCount" => ShadeBindingKind.BinScatterCount,
            "ReserveCounters" => ShadeBindingKind.ReserveCounters,
            "PixelCoordBuffer" => ShadeBindingKind.PixelCoordBuffer,
            _ => throw new InvalidOperationException(
                $"cluster shade binning has no binding classification for reflected shader resource '{name}'."),
        };

    private static RenderGraphAccess AccessForWriteTarget(string name)
        => name switch
        {
            "BinIndirectArgs" or "PixelCoordBuffer" => RenderGraphAccess.WriteOnly,
            _ => RenderGraphAccess.ReadWrite,
        };

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ShadeBinPass));
    }

    private readonly record struct ShadeBinRes(
        RenderGraphHandle Uniforms,
        RenderGraphHandle VisBuffer,
        RenderGraphHandle VisibleClusters,
        RenderGraphHandle SlotBuffer,
        RenderGraphHandle Instances,
        RenderGraphHandle PreviousInstances,
        RenderGraphHandle InstanceHeaders,
        RenderGraphHandle InstanceDataHeap,
        RenderGraphHandle PageHeap,
        RenderGraphHandle BinCounts,
        RenderGraphHandle BinOffsets,
        RenderGraphHandle BinIndirectArgs,
        RenderGraphHandle BinScatterCount,
        RenderGraphHandle ReserveCounters,
        RenderGraphHandle PixelCoordBuffer);

    private readonly record struct ShaderResourceUse(
        RenderGraphHandle Handle,
        ResourceState State,
        RenderGraphAccess Access);

    private readonly record struct BindingStep(
        ReflectedBinding Binding,
        ShadeBindingKind Kind,
        ResourceState State,
        RenderGraphAccess Access);

    private enum ShadeBindingKind
    {
        Uniforms,
        VisBuffer,
        VisibleClusters,
        SlotBuffer,
        Instances,
        PreviousInstances,
        InstanceHeaders,
        InstanceDataHeap,
        PageHeap,
        BinCounts,
        BinOffsets,
        BinIndirectArgs,
        BinScatterCount,
        ReserveCounters,
        PixelCoordBuffer,
    }
}

