using System.Runtime.InteropServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;
using System.Collections.Generic;

namespace SomeEngine.Render.Pipelines;

internal sealed class SwRasterPass : IDisposable
{
    private const string PassName = "Cluster SW Raster";
    private const int DebugMaxEntries = 8192;
    private const int DebugEntryBytes = 24;
    private const ulong DebugBufferSize = 4 + DebugMaxEntries * DebugEntryBytes;

    private readonly Dictionary<PipelineLayoutHandle, BindingPlan> _bindingPlans = [];
    private bool _disposed;

    public SwRasterPass(RenderContext renderContext)
    {
        ArgumentNullException.ThrowIfNull(renderContext);
    }

    public ClusterRasterOutput AddPasses(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        RasterBinFrame rasterBin,
        ClusterCullOutput cull,
        in DrawUniforms rasterData,
        RenderGraphHandle depthTarget,
        IReadOnlyList<MaterialBin>? states,
        bool clearTargets = true,
        RenderGraphHandle outputVisBuffer = default,
        RenderGraphHandle outputDepthUav = default,
        RenderGraphHandle deformCache = default,
        RenderGraphHandle cacheOffsets = default,
        MaterialResourceFallbacks? materialFallbacks = null,
        bool debugDump = false,
        bool debugSWHWView = false,
        uint rasterBinCount = 0,
        string? tag = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!buffers.PageHeap.IsValid
            || !instances.Transform.IsValid
            || !instances.Header.IsValid
            || !instances.Data.IsValid)
        {
            throw new ArgumentException("SW raster requires valid cluster buffers and instance frame.", nameof(buffers));
        }
        if (!rasterBin.BinnedClusterIndex.IsValid
            || !rasterBin.RasterBinMeta.IsValid
            || !rasterBin.BinnedSWDispatchArgs.IsValid)
        {
            throw new ArgumentException("SW raster requires valid raster bin output.", nameof(rasterBin));
        }
        if (!cull.VisibleClusters.IsValid)
            throw new ArgumentException("SW raster requires valid cull output.", nameof(cull));
        if (!depthTarget.IsValid)
            throw new ArgumentException("SW raster requires a valid HW depth target for depth testing.", nameof(depthTarget));
        if (rasterData.ScreenWidth == 0 || rasterData.ScreenHeight == 0)
            throw new ArgumentOutOfRangeException(nameof(rasterData), "SW raster target dimensions must be non-zero.");

        string prefix = tag != null ? $"{tag}_" : string.Empty;
        uint width = rasterData.ScreenWidth;
        uint height = rasterData.ScreenHeight;
        bool createVisBuffer = !outputVisBuffer.IsValid;
        bool createDepthUav = !outputDepthUav.IsValid;
        ValidateDepthTarget(graph, depthTarget, width, height);
        if (outputVisBuffer.IsValid)
            ValidateOutputTexture(graph, outputVisBuffer, width, height, Format.R32UInt, nameof(outputVisBuffer));
        if (outputDepthUav.IsValid)
            ValidateOutputTexture(graph, outputDepthUav, width, height, Format.R32UInt, nameof(outputDepthUav));
        uint maxBins = CountBins(rasterBin, states, rasterBinCount);
        RenderGraphHandle visBuffer;
        RenderGraphHandle depthUav;
        using (Profiler.BeginScope("SwRasterPass.Targets"))
        {
            visBuffer = createVisBuffer
                ? graph.CreateTexture(
                    $"{prefix}SW VisBuffer",
                    new TextureDesc
                    {
                        Name = $"{prefix}SW VisBuffer",
                        Dimension = ResourceDimension.Texture2D,
                        Width = width,
                        Height = height,
                        Format = Format.R32UInt,
                        BindFlags = BindFlags.UnorderedAccess
                            | BindFlags.ShaderResource
                            | BindFlags.RenderTarget,
                        InitialState = ResourceState.RenderTarget,
                        OptimizedClearValue = ClearValue.FromColor(Format.R32UInt, new Color(0, 0, 0, 0)),
                    })
                : outputVisBuffer;

            depthUav = createDepthUav
                ? graph.CreateTexture(
                    $"{prefix}SW DepthUAV",
                    new TextureDesc
                    {
                        Name = $"{prefix}SW DepthUAV",
                        Dimension = ResourceDimension.Texture2D,
                        Width = width,
                        Height = height,
                        Format = Format.R32UInt,
                        BindFlags = BindFlags.UnorderedAccess
                            | BindFlags.ShaderResource
                            | BindFlags.RenderTarget,
                        InitialState = ResourceState.RenderTarget,
                        OptimizedClearValue = ClearValue.FromColor(Format.R32UInt, new Color(0, 0, 0, 0)),
                    })
                : outputDepthUav;
        }

        using (Profiler.BeginScope("SwRasterPass.Clears"))
        {
            AddClearPass(
                graph,
                depthTarget,
                visBuffer,
                depthUav,
                clearTargets,
                clearTargets || createVisBuffer,
                clearTargets || createDepthUav,
                prefix);
        }

        uint debugFlags = (debugDump ? 1u : 0u) | (debugSWHWView ? 0x2u : 0u);
        bool captureDebugOutput = debugFlags != 0;
        var uniformData = new SwRasterUniforms
        {
            ViewProj = rasterData.ViewProj,
            ScreenWidth = width,
            ScreenHeight = height,
            MaxBins = maxBins,
            DebugDump = debugFlags,
        };

        RenderGraphHandle debugOutput = default;
        BufferViewHandle debugOutputView = default;
        using (Profiler.BeginScope("SwRasterPass.DebugOutput"))
        {
            if (captureDebugOutput || materialFallbacks?.DefaultRawUnorderedAccessView.IsValid != true)
            {
                debugOutput = graph.CreateBuffer(
                    $"{prefix}SWRaster DebugOutput",
                    new BufferDesc
                    {
                        Name = $"{prefix}SWRaster DebugOutput",
                        SizeInBytes = DebugBufferSize,
                        BindFlags = BindFlags.UnorderedAccess
                            | BindFlags.ShaderResource
                            | BindFlags.CopyDestination
                            | BindFlags.CopySource,
                        InitialState = ResourceState.CopyDestination,
                        Raw = true,
                    });
                BufferUploadPasses.AddUploadPass(graph, $"{prefix}Clear SWRaster DebugOutput", debugOutput, 0, Zero4());
            }
            else
            {
                debugOutputView = materialFallbacks.DefaultRawUnorderedAccessView;
            }
        }

        RenderGraphHandle boundDeformCache = deformCache.IsValid && cacheOffsets.IsValid
            ? deformCache
            : RenderGraphHandle.Invalid;
        RenderGraphHandle boundCacheOffsets = deformCache.IsValid && cacheOffsets.IsValid
            ? cacheOffsets
            : RenderGraphHandle.Invalid;

        SwDispatch[] dispatches;
        using (Profiler.BeginScope("SwRasterPass.Dispatches"))
        {
            dispatches = CreateDispatches(
                states,
                uniformData,
                maxBins);
        }

        using (Profiler.BeginScope("SwRasterPass.AddRasterPass"))
        {
            AddRasterPass(
                graph,
                buffers,
                instances,
                rasterBin,
                cull,
                depthTarget,
                visBuffer,
                depthUav,
                debugOutput,
                debugOutputView,
                dispatches,
                states,
                boundDeformCache,
                boundCacheOffsets,
                materialFallbacks,
                maxBins,
                prefix);
        }

        return new ClusterRasterOutput(visBuffer, depthTarget, depthUav);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }

    private static void AddClearPass(
        RenderGraph graph,
        RenderGraphHandle depthTarget,
        RenderGraphHandle visBuffer,
        RenderGraphHandle depthUav,
        bool clearDepthTarget,
        bool clearVisBuffer,
        bool clearDepthUav,
        string prefix)
    {
        if (!clearDepthTarget && !clearVisBuffer && !clearDepthUav)
            return;

        graph.AddRasterPass(
            $"{prefix}Clear SW Raster Targets",
            builder =>
            {
                if (clearDepthTarget)
                    builder.Write(depthTarget, ResourceState.DepthWrite);
                if (clearVisBuffer)
                    builder.Write(visBuffer, ResourceState.RenderTarget);
                if (clearDepthUav)
                    builder.Write(depthUav, ResourceState.RenderTarget);
            },
            context =>
            {
                TextureDesc desc = clearVisBuffer
                    ? context.GetTextureDesc(visBuffer)
                    : clearDepthUav
                        ? context.GetTextureDesc(depthUav)
                        : context.GetTextureDesc(depthTarget);
                var attachments = new List<ColorAttachmentDesc>(2);
                if (clearVisBuffer)
                {
                    attachments.Add(new ColorAttachmentDesc
                    {
                        View = context.GetTextureView(visBuffer, ViewKind.RenderTarget, Format.R32UInt),
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = new Color(0, 0, 0, 0),
                    });
                }
                if (clearDepthUav)
                {
                    attachments.Add(new ColorAttachmentDesc
                    {
                        View = context.GetTextureView(depthUav, ViewKind.RenderTarget, Format.R32UInt),
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = new Color(0, 0, 0, 0),
                    });
                }
                DepthAttachDesc? depthAttachment = null;
                if (clearDepthTarget)
                {
                    depthAttachment = new DepthAttachDesc
                    {
                        View = context.GetTextureView(depthTarget, ViewKind.DepthStencil, Format.D32Float),
                        DepthLoadOp = LoadOp.Clear,
                        DepthStoreOp = StoreOp.Store,
                        ClearValue = new ClearDepthStencil(1.0f, 0),
                    };
                }

                var pass = context.BeginRenderPass(new RenderPassDesc
                {
                    Name = $"{prefix}Clear SW Raster Targets",
                    RenderArea = new Rect(0, 0, checked((int)desc.Width), checked((int)desc.Height)),
                    ColorAttachments = CollectionsMarshal.AsSpan(attachments),
                    DepthStencilAttachment = depthAttachment,
                });
                pass.End();
            });
    }

    private SwDispatch[] CreateDispatches(
        IReadOnlyList<MaterialBin>? states,
        in SwRasterUniforms uniformTemplate,
        uint maxBins)
    {
        if (states == null || states.Count == 0)
            return [];

        var dispatches = new SwDispatch[states.Count];
        for (int index = 0; index < states.Count; index++)
        {
            MaterialBin state = states[index];
            state.Check("SW raster");
            if ((uint)state.ArgsIndex >= maxBins)
            {
                throw new InvalidOperationException(
                    $"SW raster pass state references args bin {state.ArgsIndex}, but max bin count is {maxBins}.");
            }

            SwRasterUniforms uniformData = uniformTemplate;
            uniformData.CurrentBin = checked((uint)state.ArgsIndex);
            dispatches[index] = new SwDispatch(index, state.ArgsIndex, state.MaterialScalarRegion, uniformData);
        }

        return dispatches;
    }

    private static void ValidateDepthTarget(
        RenderGraph graph,
        RenderGraphHandle depthTarget,
        uint width,
        uint height)
    {
        TextureDesc desc = graph.GetTextureDesc(depthTarget);
        if (desc.Width != width || desc.Height != height)
        {
            throw new InvalidOperationException(
                $"SW raster depth target dimensions {desc.Width}x{desc.Height} do not match the requested raster size {width}x{height}.");
        }

        if (desc.Format != Format.D32Float)
        {
            throw new InvalidOperationException(
                $"SW raster depth target must use Format.D32Float but was {desc.Format}.");
        }
    }

    private static void ValidateOutputTexture(
        RenderGraph graph,
        RenderGraphHandle texture,
        uint width,
        uint height,
        Format expectedFormat,
        string argumentName)
    {
        TextureDesc desc = graph.GetTextureDesc(texture);
        if (desc.Width != width || desc.Height != height)
        {
            throw new InvalidOperationException(
                $"SW raster output texture '{argumentName}' ('{desc.Name}') dimensions {desc.Width}x{desc.Height} do not match the requested raster size {width}x{height}.");
        }

        if (desc.Format != expectedFormat)
        {
            throw new InvalidOperationException(
                $"SW raster output texture '{argumentName}' ('{desc.Name}') must use format {expectedFormat} but was {desc.Format}.");
        }
    }

    private void AddRasterPass(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        RasterBinFrame rasterBin,
        ClusterCullOutput cull,
        RenderGraphHandle depthTarget,
        RenderGraphHandle visBuffer,
        RenderGraphHandle depthUav,
        RenderGraphHandle debugOutput,
        BufferViewHandle debugOutputView,
        IReadOnlyList<SwDispatch> dispatches,
        IReadOnlyList<MaterialBin>? states,
        RenderGraphHandle boundDeformCache,
        RenderGraphHandle boundCacheOffsets,
        MaterialResourceFallbacks? materialFallbacks,
        uint maxBins,
        string prefix)
    {
        graph.AddComputePass(
            $"{prefix}{PassName}",
            builder =>
            {
                foreach (SwDispatch dispatch in dispatches)
                {
                    if (dispatch.MaterialScalarRegion.IsValid)
                        builder.Read(dispatch.MaterialScalarRegion, ResourceState.ShaderResource);
                }

                builder.Read(cull.VisibleClusters, ResourceState.ShaderResource);
                builder.Read(rasterBin.BinnedClusterIndex, ResourceState.ShaderResource);
                builder.Read(rasterBin.RasterBinMeta, ResourceState.ShaderResource);
                builder.Read(instances.Transform, ResourceState.ShaderResource);
                builder.Read(instances.Header, ResourceState.ShaderResource);
                builder.Read(instances.Data, ResourceState.ShaderResource);
                builder.Read(buffers.PageHeap, ResourceState.ShaderResource);
                builder.Read(rasterBin.BinnedSWDispatchArgs, ResourceState.IndirectArgument);
                builder.Read(depthTarget, ResourceState.ShaderResource);
                if (boundDeformCache.IsValid && boundCacheOffsets.IsValid)
                {
                    builder.Read(boundDeformCache, ResourceState.ShaderResource);
                    builder.Read(boundCacheOffsets, ResourceState.ShaderResource);
                }

                builder.ReadWrite(visBuffer, ResourceState.UnorderedAccess);
                builder.ReadWrite(depthUav, ResourceState.UnorderedAccess);
                if (debugOutput.IsValid)
                    builder.ReadWrite(debugOutput, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                if (states == null || states.Count == 0 || dispatches.Count == 0)
                    return;

                for (int dispatchIndex = 0; dispatchIndex < dispatches.Count; dispatchIndex++)
                {
                    SwDispatch dispatch = dispatches[dispatchIndex];
                    MaterialBin state = states[dispatch.StateIndex];
                    PipelineHandle pipeline = context.GetPipeline(state.PipelineState, PipelineNeed.Optional);
                    if (!pipeline.IsValid)
                        continue;
                    ShaderBindingTable layout = state.Bindings
                        ?? throw new InvalidOperationException("SW raster state has no reflected layout.");

                    pass.SetPipeline(pipeline);
                    ApplyBindingPlan(
                        pass,
                        context,
                        GetBindingPlan(layout),
                        buffers,
                        instances,
                        rasterBin,
                        cull,
                        depthTarget,
                        visBuffer,
                        depthUav,
                        debugOutput,
                        debugOutputView,
                        boundDeformCache,
                        boundCacheOffsets,
                        dispatch,
                        state.MaterialBindings,
                        materialFallbacks);

                    SwRasterUniforms uniformData = dispatch.UniformData;
                    pass.SetPushConstants(
                        ShaderStageFlags.Compute,
                        0,
                        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref uniformData, 1)));
                    pass.DispatchIndirect(
                        rasterBin.BinnedSWDispatchArgs,
                        checked(((ulong)maxBins + (uint)dispatch.ArgsIndex) * IndirectArgumentSize.Dispatch));
                }
            });
    }

    private BindingPlan GetBindingPlan(ShaderBindingTable layout)
    {
        PipelineLayoutHandle key = layout.PipelineLayout;
        if (_bindingPlans.TryGetValue(key, out BindingPlan? existing))
            return existing;

        BindingPlan created = BindingPlan.Create(layout);
        _bindingPlans.Add(key, created);
        return created;
    }

    private static void ApplyBindingPlan(
        IParameterSink sink,
        RenderGraphContext context,
        BindingPlan plan,
        ClusterBuffers buffers,
        InstanceFrame instances,
        RasterBinFrame rasterBin,
        ClusterCullOutput cull,
        RenderGraphHandle depthTarget,
        RenderGraphHandle visBuffer,
        RenderGraphHandle depthUav,
        RenderGraphHandle debugOutput,
        BufferViewHandle debugOutputView,
        RenderGraphHandle boundDeformCache,
        RenderGraphHandle boundCacheOffsets,
        SwDispatch dispatch,
        MaterialBindings material,
        MaterialResourceFallbacks? fallbacks)
    {
        foreach (BindingSetExecutionPlan setPlan in plan.Sets)
        {
            PassBindings bindings = context.Bindings(setPlan.Layout).Reserve(setPlan.BindingCount);
            foreach (BindingSlotExecutionPlan slotPlan in setPlan.Slots)
            {
                if (TryResolveCommon(
                        slotPlan,
                        context,
                        buffers,
                        instances,
                        rasterBin,
                        cull,
                        depthTarget,
                        visBuffer,
                        depthUav,
                        debugOutput,
                        debugOutputView,
                        boundDeformCache,
                        boundCacheOffsets,
                        dispatch,
                        out BindingResourceDesc resource)
                    || TryResolveMaterial(slotPlan, material, out resource)
                    || TryResolveFallback(slotPlan, fallbacks, out resource))
                {
                    bindings = bindings.Resource(resource);
                    continue;
                }

                throw new InvalidOperationException(
                    $"SW raster could not bind reflected resource slot set {slotPlan.First.Set} binding {slotPlan.First.Binding}: {slotPlan.Names}.");
            }

            if (bindings.Count != 0)
                sink.SetParameters(setPlan.SetIndex, bindings);
        }
    }

    private static bool TryResolveMaterial(
        BindingSlotExecutionPlan slotPlan,
        MaterialBindings material,
        out BindingResourceDesc resource)
    {
        foreach (ReflectedBinding binding in slotPlan.Bindings)
        {
            if (material.TryGet(binding, out resource))
                return true;
        }

        resource = default;
        return false;
    }

    private static bool TryResolveFallback(
        BindingSlotExecutionPlan slotPlan,
        MaterialResourceFallbacks? fallbacks,
        out BindingResourceDesc resource)
    {
        if (fallbacks != null)
        {
            foreach (ReflectedBinding binding in slotPlan.Bindings)
            {
                if (BindInput.TryFallback(binding, fallbacks, out resource))
                    return true;
            }
        }

        resource = default;
        return false;
    }

    private static bool TryResolveCommon(
        BindingSlotExecutionPlan slotPlan,
        RenderGraphContext context,
        ClusterBuffers buffers,
        InstanceFrame instances,
        RasterBinFrame rasterBin,
        ClusterCullOutput cull,
        RenderGraphHandle depthTarget,
        RenderGraphHandle visBuffer,
        RenderGraphHandle depthUav,
        RenderGraphHandle debugOutput,
        BufferViewHandle debugOutputView,
        RenderGraphHandle boundDeformCache,
        RenderGraphHandle boundCacheOffsets,
        SwDispatch dispatch,
        out BindingResourceDesc resource)
    {
        for (int i = 0; i < slotPlan.Steps.Length; i++)
        {
            if (TryResolveCommon(
                slotPlan.Steps[i].Kind,
                slotPlan.Steps[i].Binding,
                context,
                buffers,
                instances,
                rasterBin,
                cull,
                depthTarget,
                visBuffer,
                depthUav,
                debugOutput,
                debugOutputView,
                boundDeformCache,
                boundCacheOffsets,
                dispatch,
                out resource))
            {
                return true;
            }
        }

        resource = default;
        return false;
    }

    private static bool TryResolveCommon(
        CommonBindingKind kind,
        ReflectedBinding binding,
        RenderGraphContext context,
        ClusterBuffers buffers,
        InstanceFrame instances,
        RasterBinFrame rasterBin,
        ClusterCullOutput cull,
        RenderGraphHandle depthTarget,
        RenderGraphHandle visBuffer,
        RenderGraphHandle depthUav,
        RenderGraphHandle debugOutput,
        BufferViewHandle debugOutputView,
        RenderGraphHandle boundDeformCache,
        RenderGraphHandle boundCacheOffsets,
        SwDispatch dispatch,
        out BindingResourceDesc resource)
    {
        resource = default;
        switch (kind)
        {
            case CommonBindingKind.None:
                return false;
            case CommonBindingKind.VisibleClusters:
                resource = binding.Buffer(context, cull.VisibleClusters);
                return true;
            case CommonBindingKind.BinnedClusterIndexBuffer:
                resource = binding.Buffer(context, rasterBin.BinnedClusterIndex);
                return true;
            case CommonBindingKind.RasterBinMeta:
                resource = binding.Buffer(context, rasterBin.RasterBinMeta);
                return true;
            case CommonBindingKind.PageHeap:
                resource = binding.Buffer(context, buffers.PageHeap);
                return true;
            case CommonBindingKind.DepthTarget:
                resource = binding.Texture(
                    context,
                    depthTarget,
                    new TextureViewDesc
                    {
                        Kind = ViewKind.ShaderResource,
                        Dimension = TextureViewDimension.Texture2D,
                        Format = Format.D32Float,
                        MipCount = 1,
                        SliceCount = 1,
                    });
                return true;
            case CommonBindingKind.Instances:
                resource = binding.Buffer(context, instances.Transform);
                return true;
            case CommonBindingKind.InstanceHeaders:
                resource = binding.Buffer(context, instances.Header);
                return true;
            case CommonBindingKind.InstanceDataHeap:
                resource = binding.Buffer(context, instances.Data);
                return true;
            case CommonBindingKind.VisBuffer:
                resource = binding.Texture(
                    context,
                    visBuffer,
                    new TextureViewDesc
                    {
                        Kind = ViewKind.UnorderedAccess,
                        Dimension = TextureViewDimension.Texture2D,
                        Format = Format.R32UInt,
                        MipCount = 1,
                        SliceCount = 1,
                    });
                return true;
            case CommonBindingKind.DepthUav:
                resource = binding.Texture(
                    context,
                    depthUav,
                    new TextureViewDesc
                    {
                        Kind = ViewKind.UnorderedAccess,
                        Dimension = TextureViewDimension.Texture2D,
                        Format = Format.R32UInt,
                        MipCount = 1,
                        SliceCount = 1,
                    });
                return true;
            case CommonBindingKind.DebugSwOutput:
                if (debugOutput.IsValid)
                {
                    resource = binding.Buffer(context, debugOutput);
                    return true;
                }

                if (debugOutputView.IsValid)
                {
                    resource = binding.Buffer(debugOutputView);
                    return true;
                }

                return false;
            case CommonBindingKind.DeformCache:
                RequireCacheBinding(boundDeformCache, binding.Name);
                resource = binding.Buffer(context, boundDeformCache);
                return true;
            case CommonBindingKind.CacheOffsets:
                RequireCacheBinding(boundCacheOffsets, binding.Name);
                resource = binding.Buffer(context, boundCacheOffsets);
                return true;
            case CommonBindingKind.MaterialScalarRegion:
                if (!dispatch.MaterialScalarRegion.IsValid)
                    return false;
                resource = binding.Buffer(context, dispatch.MaterialScalarRegion);
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static void RequireCacheBinding(RenderGraphHandle handle, string resourceName)
    {
        if (!handle.IsValid)
        {
            throw new InvalidOperationException(
                $"SW raster state reflects '{resourceName}', but the current pass did not provide that resource.");
        }
    }

    private static uint CountBins(
        RasterBinFrame rasterBin,
        IReadOnlyList<MaterialBin>? states,
        uint explicitRasterBinCount)
    {
        uint maxBins = explicitRasterBinCount > 0
            ? explicitRasterBinCount
            : Math.Max(rasterBin.DrawBinCount, 1u);

        if (states == null)
            return maxBins;

        for (int index = 0; index < states.Count; index++)
        {
            MaterialBin state = states[index];
            state.Check("SW raster");
            maxBins = Math.Max(maxBins, checked((uint)state.ArgsIndex + 1u));
        }

        return Math.Max(maxBins, 1u);
    }

    private static ReadOnlySpan<byte> Zero4()
        => [0, 0, 0, 0];

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SwRasterPass));
    }

    private sealed class BindingPlan
    {
        public required BindingSetExecutionPlan[] Sets { get; init; }

        public static BindingPlan Create(ShaderBindingTable layout)
        {
            BindingSetPlan[] sourceSets = BindInput.CreatePlan(layout);
            var sets = new BindingSetExecutionPlan[sourceSets.Length];
            for (int setIndex = 0; setIndex < sourceSets.Length; setIndex++)
            {
                BindingSetPlan set = sourceSets[setIndex];
                var slots = new BindingSlotExecutionPlan[set.Slots.Length];
                for (int slotIndex = 0; slotIndex < set.Slots.Length; slotIndex++)
                    slots[slotIndex] = BindingSlotExecutionPlan.Create(set.Slots[slotIndex]);

                sets[setIndex] = new BindingSetExecutionPlan(set.SetIndex, set.Layout, set.BindingCount, slots);
            }

            return new BindingPlan { Sets = sets };
        }
    }

    private readonly record struct BindingSetExecutionPlan(
        uint SetIndex,
        BindingLayoutHandle Layout,
        int BindingCount,
        BindingSlotExecutionPlan[] Slots);

    private readonly record struct BindingSlotExecutionPlan(
        ReflectedBinding[] Bindings,
        BindingStep[] Steps,
        string Names)
    {
        public ReflectedBinding First => Bindings[0];

        public static BindingSlotExecutionPlan Create(BindingSlotPlan plan)
        {
            BindingStep[] steps = new BindingStep[plan.Bindings.Length];
            for (int i = 0; i < plan.Bindings.Length; i++)
                steps[i] = new BindingStep(plan.Bindings[i], Classify(plan.Bindings[i].Name));

            return new BindingSlotExecutionPlan(
                plan.Bindings,
                steps,
                string.Join(", ", Array.ConvertAll(plan.Bindings, static binding => binding.Name)));
        }

        private static CommonBindingKind Classify(string name)
            => name switch
            {
                "Uniforms" => CommonBindingKind.Uniforms,
                "VisibleClusters" => CommonBindingKind.VisibleClusters,
                "BinnedClusterIndexBuffer" => CommonBindingKind.BinnedClusterIndexBuffer,
                "RasterBinMeta" => CommonBindingKind.RasterBinMeta,
                "PageHeap" => CommonBindingKind.PageHeap,
                "DepthTarget" => CommonBindingKind.DepthTarget,
                "Instances" => CommonBindingKind.Instances,
                "InstanceHeaders" => CommonBindingKind.InstanceHeaders,
                "InstanceDataHeap" => CommonBindingKind.InstanceDataHeap,
                "VisBuffer" => CommonBindingKind.VisBuffer,
                "DepthUAV" => CommonBindingKind.DepthUav,
                "DebugSWOutput" => CommonBindingKind.DebugSwOutput,
                "DeformCache" => CommonBindingKind.DeformCache,
                "CacheOffsets" => CommonBindingKind.CacheOffsets,
                "MaterialScalarRegion" => CommonBindingKind.MaterialScalarRegion,
                _ => CommonBindingKind.None,
            };
    }

    private readonly record struct BindingStep(
        ReflectedBinding Binding,
        CommonBindingKind Kind);

    private enum CommonBindingKind
    {
        None,
        Uniforms,
        VisibleClusters,
        BinnedClusterIndexBuffer,
        RasterBinMeta,
        PageHeap,
        DepthTarget,
        Instances,
        InstanceHeaders,
        InstanceDataHeap,
        VisBuffer,
        DepthUav,
        DebugSwOutput,
        DeformCache,
        CacheOffsets,
        MaterialScalarRegion,
    }

    private readonly record struct SwDispatch(
        int StateIndex,
        int ArgsIndex,
        RenderGraphHandle MaterialScalarRegion,
        SwRasterUniforms UniformData);
}

