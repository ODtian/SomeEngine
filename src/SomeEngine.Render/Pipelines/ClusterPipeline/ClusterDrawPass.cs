using SomeEngine.Core.Diagnostics;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;
using System.Collections.Generic;

namespace SomeEngine.Render.Pipelines;

internal sealed class ClusterDrawPass
{
    private const string VertexEntryPoint = "VSVisBuffer";
    private const string PixelEntryPoint = "PSVisBuffer";
    private const string PassName = "Cluster Draw VisBuffer";

    private readonly RenderContext _renderContext;
    private readonly ShaderBindingTable _layout;
    private readonly Dictionary<PipelineLayoutHandle, BindingPlan> _bindingPlans = [];
    private PipelineTicket _pipeline;
    private readonly UniformPool<DrawDispatchUniforms> _dispatchUniforms;
    private DrawDispatchUniforms[] _dispatchData = [];
    private bool _disposed;

    public ClusterDrawPass(RenderContext renderContext, Shader shader)
    {
        _renderContext = renderContext ?? throw new ArgumentNullException(nameof(renderContext));
        IDevice device = renderContext.GraphicsDevice ?? throw new InvalidOperationException("cluster draw dispatch uniforms requires an initialized render context.");
        _layout = ShaderBindings.Create(
            device,
            PassName,
            shader,
            [
                "Uniforms",
                "DispatchUniforms",
                "Instances",
                "InstanceHeaders",
                "InstanceDataHeap",
                "PageHeap",
                "BinnedClusterIndexBuffer",
                "VisibleClusters",
                "VisibleClusterMeta",
                "DeformCache",
                "CacheOffsets",
            ],
            VertexEntryPoint,
            PixelEntryPoint);
        _pipeline = _renderContext.PipelineCache!.QueueGraphics(
            new GraphicsState
            {
                Name = "Cluster Draw VisBuffer Pipeline",
                VertexShader = shader,
                VertexEntry = VertexEntryPoint,
                PixelShader = shader,
                PixelEntry = PixelEntryPoint,
                Layout = _layout.PipelineLayout,
                Bindings = _layout.Key,
                Topology = PrimitiveTopology.TriangleList,
                Rasterizer = new RasterizerDesc
                {
                    CullMode = CullMode.Back,
                    FrontCounterClockwise = true,
                },
                DepthStencil = new DepthStencilDesc
                {
                    DepthEnable = true,
                    DepthWriteEnable = true,
                    DepthCompare = CompareOp.LessOrEqual,
                },
                ColorFormats = [Format.R32UInt],
                DepthStencilFormat = Format.D32Float,
            },
            ClusterSources.Builtins);
        _dispatchUniforms = new UniformPool<DrawDispatchUniforms>(device);
    }

    public void AddTickets(List<PipelineTicket> tickets)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        tickets.Add(_pipeline);
    }

    public ClusterRasterOutput AddPasses(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        RasterBinFrame rasterBin,
        ClusterCullOutput cull,
        UniformFrame drawUniforms,
        uint screenWidth,
        uint screenHeight,
        bool clearTargets = true,
        RenderGraphHandle outputVisBuffer = default,
        RenderGraphHandle outputDepth = default,
        RenderGraphHandle deformCache = default,
        RenderGraphHandle cacheOffsets = default,
        IReadOnlyList<MaterialBin>? states = null,
        MaterialResourceFallbacks? materialFallbacks = null,
        bool useHWDrawArgs = false,
        string? tag = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!buffers.PageHeap.IsValid || !instances.Transform.IsValid || !instances.Header.IsValid)
            throw new ArgumentException("cluster draw requires valid cluster buffers and instance frame.", nameof(buffers));
        RenderGraphHandle drawArgsBuffer = useHWDrawArgs ? rasterBin.BinnedHWDrawArgs : rasterBin.BinnedDrawArgs;
        if (!rasterBin.BinnedClusterIndex.IsValid || !drawArgsBuffer.IsValid)
            throw new ArgumentException("cluster draw requires valid raster bin output.", nameof(rasterBin));
        if (!cull.VisibleClusters.IsValid)
            throw new ArgumentException("cluster draw requires valid cull output.", nameof(cull));
        if (!drawUniforms.IsValid)
            throw new ArgumentException("cluster draw requires a valid draw uniform frame.", nameof(drawUniforms));
        if (screenWidth == 0 || screenHeight == 0)
            throw new ArgumentOutOfRangeException(nameof(screenWidth), "cluster draw target dimensions must be non-zero.");

        string prefix = tag != null ? $"{tag}_" : string.Empty;
        RenderGraphHandle visBuffer;
        RenderGraphHandle depth;
        using (Profiler.BeginScope("ClusterDrawPass.Targets"))
        {
            visBuffer = outputVisBuffer.IsValid
                ? outputVisBuffer
                : graph.CreateTexture(
                    $"{prefix}VisBuffer",
                    new TextureDesc
                    {
                        Name = $"{prefix}VisBuffer",
                        Dimension = ResourceDimension.Texture2D,
                        Width = screenWidth,
                        Height = screenHeight,
                        Format = Format.R32UInt,
                        BindFlags = BindFlags.RenderTarget
                            | BindFlags.ShaderResource
                            | BindFlags.UnorderedAccess,
                        InitialState = ResourceState.RenderTarget,
                        OptimizedClearValue = ClearValue.FromColor(Format.R32UInt, new Color(0, 0, 0, 0)),
                    });
            depth = outputDepth.IsValid
                ? outputDepth
                : graph.CreateTexture(
                    $"{prefix}ClusterDepth",
                    new TextureDesc
                    {
                        Name = $"{prefix}ClusterDepth",
                        Dimension = ResourceDimension.Texture2D,
                        Width = screenWidth,
                        Height = screenHeight,
                        Format = Format.D32Float,
                        BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
                        InitialState = ResourceState.DepthWrite,
                        OptimizedClearValue = ClearValue.FromDepthStencil(Format.D32Float, new ClearDepthStencil(1.0f, 0)),
                    });
        }
        uint drawBinCount = MaxArgs(states, Math.Max(rasterBin.DrawBinCount, 1u), "cluster draw");
        UniformFrame dispatchUniforms;
        using (Profiler.BeginScope("ClusterDrawPass.Dispatches"))
        {
            dispatchUniforms = AddDispatches(graph, prefix, drawBinCount);
        }
        RenderGraphHandle boundDeformCache = deformCache.IsValid && cacheOffsets.IsValid
            ? deformCache
            : RenderGraphHandle.Invalid;
        RenderGraphHandle boundCacheOffsets = deformCache.IsValid && cacheOffsets.IsValid
            ? cacheOffsets
            : RenderGraphHandle.Invalid;

        using (Profiler.BeginScope("ClusterDrawPass.AddDrawPass"))
        {
            AddDrawPass(
                graph,
                buffers,
                instances,
                rasterBin,
                cull,
                drawArgsBuffer,
                drawUniforms,
                dispatchUniforms,
                boundDeformCache,
                boundCacheOffsets,
                visBuffer,
                depth,
                states,
                materialFallbacks,
                clearTargets,
                prefix);
        }

        return new ClusterRasterOutput(visBuffer, depth, depth);
    }

    private UniformFrame AddDispatches(RenderGraph graph, string prefix, uint count)
    {
        int binCount = checked((int)count);
        if (_dispatchData.Length < binCount)
            Array.Resize(ref _dispatchData, binCount);

        for (uint bin = 0; bin < count; bin++)
        {
            _dispatchData[checked((int)bin)] = new DrawDispatchUniforms
            {
                DrawArgsByteOffset = checked(bin * (uint)IndirectArgumentSize.Draw),
            };
        }

        return _dispatchUniforms.Add(
            graph,
            _dispatchData.AsSpan(0, binCount),
            $"{prefix}DrawDispatchUniforms");
    }

    public void Dispose(PipelineCache store)
    {
        if (_disposed)
            return;
        ArgumentNullException.ThrowIfNull(store);

        store.ReleaseIdle(ref _pipeline);
        var device = _renderContext.GraphicsDevice;
        if (device != null)
            ShaderBindings.Destroy(device, _layout);

        _dispatchUniforms.Dispose();
        _disposed = true;
    }

    private void AddDrawPass(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        RasterBinFrame rasterBin,
        ClusterCullOutput cull,
        RenderGraphHandle drawArgsBuffer,
        UniformFrame drawUniforms,
        UniformFrame dispatchUniforms,
        RenderGraphHandle deformCache,
        RenderGraphHandle cacheOffsets,
        RenderGraphHandle visBuffer,
        RenderGraphHandle depth,
        IReadOnlyList<MaterialBin>? states,
        MaterialResourceFallbacks? materialFallbacks,
        bool clearTargets,
        string prefix)
    {
        graph.AddRasterPass(
            $"{prefix}{PassName}",
            builder =>
            {
                builder.Read(drawUniforms.Buffer, ResourceState.ConstantBuffer);
                builder.Read(dispatchUniforms.Buffer, ResourceState.ConstantBuffer);
                builder.Read(instances.Transform, ResourceState.ShaderResource);
                builder.Read(instances.Header, ResourceState.ShaderResource);
                builder.Read(instances.Data, ResourceState.ShaderResource);
                builder.Read(buffers.PageHeap, ResourceState.ShaderResource);
                builder.Read(rasterBin.BinnedClusterIndex, ResourceState.ShaderResource);
                builder.Read(cull.VisibleClusters, ResourceState.ShaderResource);
                builder.Read(drawArgsBuffer, ResourceState.IndirectArgument);
                if (deformCache.IsValid && cacheOffsets.IsValid)
                {
                    builder.Read(deformCache, ResourceState.ShaderResource);
                    builder.Read(cacheOffsets, ResourceState.ShaderResource);
                }
                if (clearTargets)
                {
                    builder.Write(visBuffer, ResourceState.RenderTarget);
                    builder.Write(depth, ResourceState.DepthWrite);
                }
                else
                {
                    builder.ReadWrite(visBuffer, ResourceState.RenderTarget);
                    builder.ReadWrite(depth, ResourceState.DepthWrite);
                }
            },
            context =>
            {
                var visDesc = context.GetTextureDesc(visBuffer);
                bool usePassStates = states != null && states.Count > 0;

                var rtv = context.GetTextureView(visBuffer, ViewKind.RenderTarget, Format.R32UInt);
                var dsv = context.GetTextureView(depth, ViewKind.DepthStencil, Format.D32Float);
                Span<ColorAttachmentDesc> colorAttachments = stackalloc ColorAttachmentDesc[1];
                colorAttachments[0] = new ColorAttachmentDesc
                {
                    View = rtv,
                    LoadOp = clearTargets ? LoadOp.Clear : LoadOp.Load,
                    StoreOp = StoreOp.Store,
                    ClearColor = new Color(0, 0, 0, 0),
                };

                var pass = context.BeginRenderPass(new RenderPassDesc
                {
                    Name = $"{prefix}{PassName}",
                    RenderArea = new Rect(0, 0, checked((int)visDesc.Width), checked((int)visDesc.Height)),
                    ColorAttachments = colorAttachments,
                    DepthStencilAttachment = new DepthAttachDesc
                    {
                        View = dsv,
                        DepthLoadOp = clearTargets ? LoadOp.Clear : LoadOp.Load,
                        DepthStoreOp = StoreOp.Store,
                        ClearValue = new ClearDepthStencil(1.0f, 0),
                    },
                });

                pass.SetViewport(new Viewport(0, 0, visDesc.Width, visDesc.Height));
                pass.SetScissor(new Rect(0, 0, checked((int)visDesc.Width), checked((int)visDesc.Height)));
                if (usePassStates)
                {
                    for (int index = 0; index < states!.Count; index++)
                    {
                        MaterialBin state = states[index];
                        state.Check("cluster draw");
                        ShaderBindingTable? bindings = state.Bindings;
                        PipelineHandle pipeline = context.GetPipeline(state.PipelineState, PipelineNeed.Optional);
                        if (!pipeline.IsValid
                            || !bindings.HasValue
                            || bindings.Value.SetCount == 0)
                        {
                            continue;
                        }

                        pass.SetPipeline(pipeline);
                        if ((uint)state.ArgsIndex >= (uint)dispatchUniforms.Count)
                            continue;

                        ApplyMaterialBindingPlan(
                            pass,
                            context,
                            GetBindingPlan(bindings.Value),
                            buffers,
                            instances,
                            rasterBin,
                            cull,
                            drawArgsBuffer,
                            drawUniforms,
                            dispatchUniforms,
                            state.ArgsIndex,
                            deformCache,
                            cacheOffsets,
                            state.MaterialBindings,
                            materialFallbacks);
                        pass.DrawIndirect(
                            drawArgsBuffer,
                            checked((ulong)state.ArgsIndex * IndirectArgumentSize.Draw));
                    }
                }
                else
                {
                    pass.SetPipeline(context.GetPipeline(_pipeline));
                    for (int bin = 0; bin < dispatchUniforms.Count; bin++)
                    {
                        ApplyInlineBindingPlan(
                            pass,
                            context,
                            GetBindingPlan(_layout),
                            buffers,
                            instances,
                            rasterBin,
                            cull,
                            drawArgsBuffer,
                            drawUniforms,
                            dispatchUniforms,
                            bin,
                            deformCache,
                            cacheOffsets);
                        pass.DrawIndirect(
                            drawArgsBuffer,
                            checked((ulong)bin * IndirectArgumentSize.Draw));
                    }
                }
                pass.End();
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

    private static void ApplyInlineBindingPlan(
        IParameterSink sink,
        RenderGraphContext context,
        BindingPlan plan,
        ClusterBuffers buffers,
        InstanceFrame instances,
        RasterBinFrame rasterBin,
        ClusterCullOutput cull,
        RenderGraphHandle drawArgsBuffer,
        UniformFrame drawUniforms,
        UniformFrame dispatchUniforms,
        int argsBin,
        RenderGraphHandle deformCache,
        RenderGraphHandle cacheOffsets)
    {
        foreach (BindingSetExecutionPlan setPlan in plan.Sets)
        {
            PassBindings bindings = context.Bindings(setPlan.Layout).Reserve(setPlan.BindingCount);
            foreach (BindingSlotExecutionPlan slotPlan in setPlan.Slots)
            {
                if (!TryResolveCommon(
                        slotPlan,
                        context,
                        buffers,
                        instances,
                        rasterBin,
                        cull,
                        drawArgsBuffer,
                        drawUniforms,
                        dispatchUniforms,
                        argsBin,
                        deformCache,
                        cacheOffsets,
                        out BindingResourceDesc resource))
                {
                    throw new InvalidOperationException(
                        $"cluster draw inline pipeline could not bind reflected resource slot set {slotPlan.First.Set} binding {slotPlan.First.Binding}: {slotPlan.Names}.");
                }

                bindings = bindings.Resource(resource);
            }

            if (bindings.Count != 0)
                sink.SetParameters(setPlan.SetIndex, bindings);
        }
    }

    private static void ApplyMaterialBindingPlan(
        IParameterSink sink,
        RenderGraphContext context,
        BindingPlan plan,
        ClusterBuffers buffers,
        InstanceFrame instances,
        RasterBinFrame rasterBin,
        ClusterCullOutput cull,
        RenderGraphHandle drawArgsBuffer,
        UniformFrame drawUniforms,
        UniformFrame dispatchUniforms,
        int argsIndex,
        RenderGraphHandle deformCache,
        RenderGraphHandle cacheOffsets,
        MaterialBindings material,
        MaterialResourceFallbacks? materialFallbacks)
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
                        drawArgsBuffer,
                        drawUniforms,
                        dispatchUniforms,
                        argsIndex,
                        deformCache,
                        cacheOffsets,
                        out BindingResourceDesc resource)
                    || TryResolveMaterial(slotPlan, material, out resource)
                    || TryResolveFallback(slotPlan, materialFallbacks, out resource))
                {
                    bindings = bindings.Resource(resource);
                    continue;
                }

                throw new InvalidOperationException(
                    $"cluster draw could not bind reflected resource slot set {slotPlan.First.Set} binding {slotPlan.First.Binding}: {slotPlan.Names}.");
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
        RenderGraphHandle drawArgsBuffer,
        UniformFrame drawUniforms,
        UniformFrame dispatchUniforms,
        int argsBin,
        RenderGraphHandle deformCache,
        RenderGraphHandle cacheOffsets,
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
                drawArgsBuffer,
                drawUniforms,
                dispatchUniforms,
                argsBin,
                deformCache,
                cacheOffsets,
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
        RenderGraphHandle drawArgsBuffer,
        UniformFrame drawUniforms,
        UniformFrame dispatchUniforms,
        int argsBin,
        RenderGraphHandle deformCache,
        RenderGraphHandle cacheOffsets,
        out BindingResourceDesc resource)
    {
        resource = default;
        switch (kind)
        {
            case CommonBindingKind.None:
                return false;
            case CommonBindingKind.Uniforms:
                resource = binding.Buffer(
                    context,
                    drawUniforms.Buffer,
                    new BufferViewDesc
                    {
                        Kind = ViewKind.ConstantBuffer,
                        Offset = drawUniforms.Offset(0),
                        SizeInBytes = drawUniforms.SlotBytes,
                    });
                return true;
            case CommonBindingKind.DispatchUniforms:
                resource = binding.Buffer(
                    context,
                    dispatchUniforms.Buffer,
                    new BufferViewDesc
                    {
                        Kind = ViewKind.ConstantBuffer,
                        Offset = dispatchUniforms.Offset(argsBin),
                        SizeInBytes = dispatchUniforms.SlotBytes,
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
            case CommonBindingKind.PageHeap:
                resource = binding.Buffer(context, buffers.PageHeap);
                return true;
            case CommonBindingKind.BinnedClusterIndexBuffer:
                resource = binding.Buffer(context, rasterBin.BinnedClusterIndex);
                return true;
            case CommonBindingKind.VisibleClusters:
                resource = binding.Buffer(context, cull.VisibleClusters);
                return true;
            case CommonBindingKind.VisibleClusterMeta:
                resource = binding.Buffer(context, drawArgsBuffer);
                return true;
            case CommonBindingKind.DeformCache:
                if (!deformCache.IsValid)
                    throw new InvalidOperationException("cluster draw state reflects 'DeformCache', but the current pass did not provide it.");
                resource = binding.Buffer(context, deformCache);
                return true;
            case CommonBindingKind.CacheOffsets:
                if (!cacheOffsets.IsValid)
                    throw new InvalidOperationException("cluster draw state reflects 'CacheOffsets', but the current pass did not provide it.");
                resource = binding.Buffer(context, cacheOffsets);
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static uint MaxArgs(
        IReadOnlyList<MaterialBin>? states,
        uint minimum,
        string owner)
    {
        uint maxArgs = Math.Max(minimum, 1u);
        if (states == null)
            return maxArgs;

        for (int index = 0; index < states.Count; index++)
        {
            MaterialBin state = states[index];
            state.Check(owner);
            maxArgs = Math.Max(maxArgs, checked((uint)state.ArgsIndex + 1u));
        }

        return maxArgs;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ClusterDrawPass));
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
                "DispatchUniforms" => CommonBindingKind.DispatchUniforms,
                "Instances" => CommonBindingKind.Instances,
                "InstanceHeaders" => CommonBindingKind.InstanceHeaders,
                "InstanceDataHeap" => CommonBindingKind.InstanceDataHeap,
                "PageHeap" => CommonBindingKind.PageHeap,
                "BinnedClusterIndexBuffer" => CommonBindingKind.BinnedClusterIndexBuffer,
                "VisibleClusters" => CommonBindingKind.VisibleClusters,
                "VisibleClusterMeta" => CommonBindingKind.VisibleClusterMeta,
                "DeformCache" => CommonBindingKind.DeformCache,
                "CacheOffsets" => CommonBindingKind.CacheOffsets,
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
        DispatchUniforms,
        Instances,
        InstanceHeaders,
        InstanceDataHeap,
        PageHeap,
        BinnedClusterIndexBuffer,
        VisibleClusters,
        VisibleClusterMeta,
        DeformCache,
        CacheOffsets,
    }

}

