using SomeEngine.Assets;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;
using System.Collections.Generic;

namespace SomeEngine.Render.Pipelines;

internal sealed class MaterialShadePass : IDisposable
{
    private const string PassName = "Cluster Material Shade";

    private readonly UniformGpu<ShadeUniforms> _uniforms;
    private readonly UniformGpu<LightCounts> _lightCounts;
    private readonly UniformGpu<LightGridUniforms> _lightGridUniforms;
    private readonly LightBuffer _lightBuffer;
    private readonly Dictionary<PipelineLayoutHandle, BindingPlan> _bindingPlans = [];
    private bool _disposed;

    public MaterialShadePass(RenderContext renderContext)
    {
        ArgumentNullException.ThrowIfNull(renderContext);
        IDevice device = renderContext.GraphicsDevice
            ?? throw new InvalidOperationException("material shade uniforms requires an initialized render context.");
        _uniforms = new UniformGpu<ShadeUniforms>(device);
        _lightCounts = new UniformGpu<LightCounts>(device);
        _lightGridUniforms = new UniformGpu<LightGridUniforms>(device);
        _lightBuffer = new LightBuffer(device);
    }

    public ClusterShadeOutput AddPasses(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterRasterOutput raster,
        ClusterCullOutput cull,
        ShadeBinFrame shadeBin,
        IReadOnlyList<MaterialBin> states,
        MaterialResourceFallbacks materialFallbacks,
        AssetStore assets,
        in ShadeUniforms shadeData,
        in SceneLights sceneLights,
        uint lightVersion,
        uint screenWidth,
        uint screenHeight,
        uint depthSliceCount,
        RenderGraphHandle outputColor = default,
        RenderGraphHandle outputMotionVectors = default,
        RenderGraphHandle deformCache = default,
        RenderGraphHandle cacheOffsets = default,
        bool clearTargets = true,
        string? tag = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(materialFallbacks);
        ArgumentNullException.ThrowIfNull(assets);
        if (screenWidth == 0 || screenHeight == 0)
            throw new ArgumentOutOfRangeException(nameof(screenWidth), "material shade target dimensions must be non-zero.");
        ValidateInputs(buffers, instances, raster, cull, shadeBin);

        string prefix = tag != null ? $"{tag}_" : string.Empty;
        RenderGraphHandle color;
        RenderGraphHandle motionVectors;
        using (Profiler.BeginScope("MaterialShadePass.Targets"))
        {
            color = outputColor.IsValid
                ? outputColor
                : CreateColorTarget(graph, $"{prefix}MaterialShadeColor", screenWidth, screenHeight);
            motionVectors = outputMotionVectors.IsValid
                ? outputMotionVectors
                : MotionTarget(graph, $"{prefix}MaterialShadeMotionVectors", screenWidth, screenHeight);
        }

        if (clearTargets)
        {
            using (Profiler.BeginScope("MaterialShadePass.Clears"))
            {
                AddClears(graph, color, motionVectors, prefix);
            }
        }

        RenderGraphHandle boundDeformCache = deformCache.IsValid && cacheOffsets.IsValid
            ? deformCache
            : RenderGraphHandle.Invalid;
        RenderGraphHandle boundCacheOffsets = deformCache.IsValid && cacheOffsets.IsValid
            ? cacheOffsets
            : RenderGraphHandle.Invalid;

        MaterialShadeDispatch[] dispatches;
        UniformFrame uniforms;
        using (Profiler.BeginScope("MaterialShadePass.CreateDispatches"))
        {
            dispatches = CreateDispatches(
                graph,
                states,
                shadeData,
                prefix,
                out uniforms);
        }

        if (dispatches.Length == 0)
            return new ClusterShadeOutput(color, motionVectors);

        LightGrid lights;
        using (Profiler.BeginScope("MaterialShadePass.LightGrid"))
        {
            lights = _lightBuffer.AddFrame(graph, sceneLights, lightVersion, shadeData, screenWidth, screenHeight, depthSliceCount);
        }

        UniformFrame lightCounts;
        UniformFrame lightGridUniforms;
        using (Profiler.BeginScope("MaterialShadePass.LightUniforms"))
        {
            lightCounts = _lightCounts.Add(
                graph,
                [lights.Counts],
                $"{prefix}LightCounts");
            lightGridUniforms = _lightGridUniforms.Add(
                graph,
                [lights.Uniforms],
                $"{prefix}LightGridUniforms");
        }

        TextureViewHandle lightCookieAtlas;
        SamplerHandle lightCookieSampler;
        using (Profiler.BeginScope("MaterialShadePass.LightCookie"))
        {
            lightCookieAtlas = GetLightCookieTexture(sceneLights, materialFallbacks, assets);
            lightCookieSampler = materialFallbacks.LightCookieSampler.IsValid
                ? materialFallbacks.LightCookieSampler
                : materialFallbacks.DefaultSampler;
        }

        var executionResources = new ShadeResources(
            buffers,
            instances,
            raster,
            cull,
            shadeBin,
            uniforms,
            lights,
            lightCounts,
            lightGridUniforms,
            color,
            motionVectors,
            boundDeformCache,
            boundCacheOffsets,
            lightCookieAtlas,
            lightCookieSampler);

        using (Profiler.BeginScope("MaterialShadePass.AddComputePass"))
        {
            graph.AddComputePass(
                $"{prefix}{PassName}",
                builder =>
                {
                    using (Profiler.BeginScope("MaterialShadePass.AddComputePass.Builder"))
                    {
                        builder.Read(instances.Transform, ResourceState.ShaderResource);
                        builder.Read(instances.PrevTransform, ResourceState.ShaderResource);
                        builder.Read(instances.Header, ResourceState.ShaderResource);
                        builder.Read(instances.Data, ResourceState.ShaderResource);
                        builder.Read(buffers.PageHeap, ResourceState.ShaderResource);
                        builder.Read(raster.VisBuffer, ResourceState.ShaderResource);
                        builder.Read(cull.VisibleClusters, ResourceState.ShaderResource);
                        builder.Read(shadeBin.PixelCoordBuffer, ResourceState.ShaderResource);
                        builder.Read(shadeBin.BinOffsets, ResourceState.ShaderResource);
                        builder.Read(shadeBin.BinCounts, ResourceState.ShaderResource);
                        builder.Read(shadeBin.BinIndirectArgs, ResourceState.IndirectArgument);
                        builder.Read(lights.Buffer, ResourceState.ShaderResource);
                        builder.Read(lights.ClusterLightGrid, ResourceState.ShaderResource);
                        builder.Read(lights.LightIndexList, ResourceState.ShaderResource);
                        builder.Read(lightCounts.Buffer, ResourceState.ConstantBuffer);
                        builder.Read(lightGridUniforms.Buffer, ResourceState.ConstantBuffer);
                        if (boundDeformCache.IsValid && boundCacheOffsets.IsValid)
                        {
                            builder.Read(boundDeformCache, ResourceState.ShaderResource);
                            builder.Read(boundCacheOffsets, ResourceState.ShaderResource);
                        }
                        builder.Read(uniforms.Buffer, ResourceState.ConstantBuffer);
                        for (int i = 0; i < dispatches.Length; i++)
                        {
                            builder.Read(dispatches[i].MaterialScalarRegion, ResourceState.ShaderResource);
                        }
                        builder.ReadWrite(color, ResourceState.UnorderedAccess);
                        builder.ReadWrite(motionVectors, ResourceState.UnorderedAccess);
                    }
                },
                (context, pass) =>
                {
                    PipelineHandle currentPipeline = default;
                    for (int i = 0; i < dispatches.Length; i++)
                    {
                        MaterialShadeDispatch dispatch = dispatches[i];
                        MaterialBin state = states[dispatch.StateIndex];
                        PipelineHandle pipeline = context.GetPipeline(state.PipelineState, PipelineNeed.Optional);
                        if (!pipeline.IsValid)
                            continue;
                        ShaderBindingTable layout = state.Bindings
                            ?? throw new InvalidOperationException("material shade state has no reflected layout.");

                        if (!currentPipeline.Equals(pipeline))
                        {
                            pass.SetPipeline(pipeline);
                            currentPipeline = pipeline;
                        }

                        ApplyBindingPlan(
                            pass,
                            context,
                            GetBindingPlan(layout),
                            executionResources,
                            dispatch,
                            state.MaterialBindings,
                            materialFallbacks);

                        pass.DispatchIndirect(
                            shadeBin.BinIndirectArgs,
                            checked((ulong)dispatch.ArgsIndex * IndirectArgumentSize.Dispatch));
                    }
                });
        }

        return new ClusterShadeOutput(color, motionVectors);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _uniforms.Dispose();
        _lightCounts.Dispose();
        _lightGridUniforms.Dispose();
        _lightBuffer.Dispose();
        _disposed = true;
    }

    private static void ValidateInputs(
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterRasterOutput raster,
        ClusterCullOutput cull,
        ShadeBinFrame shadeBin)
    {
        if (!instances.Transform.IsValid
            || !instances.PrevTransform.IsValid
            || !instances.Header.IsValid
            || !instances.Data.IsValid
            || !buffers.PageHeap.IsValid)
        {
            throw new ArgumentException("material shade requires valid cluster buffers and instance frame.", nameof(buffers));
        }
        if (!raster.VisBuffer.IsValid)
            throw new ArgumentException("material shade requires a valid raster vis buffer.", nameof(raster));
        if (!cull.VisibleClusters.IsValid)
            throw new ArgumentException("material shade requires valid cull output.", nameof(cull));
        if (!shadeBin.PixelCoordBuffer.IsValid
            || !shadeBin.BinOffsets.IsValid
            || !shadeBin.BinCounts.IsValid
            || !shadeBin.BinIndirectArgs.IsValid)
        {
            throw new ArgumentException("material shade requires valid shade bin output.", nameof(shadeBin));
        }
    }

    private static RenderGraphHandle CreateColorTarget(
        RenderGraph graph,
        string name,
        uint width,
        uint height)
        => graph.CreateTexture(
            name,
            new TextureDesc
            {
                Name = name,
                Dimension = ResourceDimension.Texture2D,
                Width = width,
                Height = height,
                MipLevels = 1,
                Format = Format.Rgba16Float,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                InitialState = ResourceState.RenderTarget,
            });

    private static RenderGraphHandle MotionTarget(
        RenderGraph graph,
        string name,
        uint width,
        uint height)
        => graph.CreateTexture(
            name,
            new TextureDesc
            {
                Name = name,
                Dimension = ResourceDimension.Texture2D,
                Width = width,
                Height = height,
                MipLevels = 1,
                Format = Format.Rg16Float,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                InitialState = ResourceState.RenderTarget,
            });

    private static void AddClearTex(
        RenderGraph graph,
        RenderGraphHandle target,
        Color clearColor,
        string name)
    {
        graph.AddRasterPass(
            name,
            builder => builder.Write(target, ResourceState.RenderTarget),
            context =>
            {
                TextureDesc desc = context.GetTextureDesc(target);
                ColorAttachmentDesc[] colorAttachments =
                [
                    new ColorAttachmentDesc
                    {
                        View = context.GetTextureView(target, ViewKind.RenderTarget, desc.Format),
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = clearColor,
                    },
                ];

                var pass = context.BeginRenderPass(new RenderPassDesc
                {
                    Name = name,
                    RenderArea = new Rect(0, 0, checked((int)desc.Width), checked((int)desc.Height)),
                    ColorAttachments = colorAttachments,
                });
                pass.End();
            });
    }

    private static void AddClears(
        RenderGraph graph,
        RenderGraphHandle color,
        RenderGraphHandle motionVectors,
        string prefix)
    {
        Color clear = new(0, 0, 0, 0);
        if (!CanClearTogether(graph, color, motionVectors))
        {
            AddClearTex(graph, color, clear, $"{prefix}Clear Material Shade Color");
            AddClearTex(graph, motionVectors, clear, $"{prefix}Clear Material Shade MotionVectors");
            return;
        }

        graph.AddRasterPass(
            $"{prefix}Clear Material Shade Targets",
            builder =>
            {
                builder.Write(color, ResourceState.RenderTarget);
                builder.Write(motionVectors, ResourceState.RenderTarget);
            },
            context =>
            {
                TextureDesc colorDesc = context.GetTextureDesc(color);
                TextureDesc motionDesc = context.GetTextureDesc(motionVectors);
                ColorAttachmentDesc[] colorAttachments =
                [
                    new ColorAttachmentDesc
                    {
                        View = context.GetTextureView(color, ViewKind.RenderTarget, colorDesc.Format),
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = clear,
                    },
                    new ColorAttachmentDesc
                    {
                        View = context.GetTextureView(motionVectors, ViewKind.RenderTarget, motionDesc.Format),
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = clear,
                    },
                ];

                var pass = context.BeginRenderPass(new RenderPassDesc
                {
                    Name = $"{prefix}Clear Material Shade Targets",
                    RenderArea = new Rect(0, 0, checked((int)colorDesc.Width), checked((int)colorDesc.Height)),
                    ColorAttachments = colorAttachments,
                });
                pass.End();
            });
    }

    private static bool CanClearTogether(
        RenderGraph graph,
        RenderGraphHandle first,
        RenderGraphHandle second)
    {
        if (first == second)
            return false;

        TextureDesc firstDesc = graph.GetTextureDesc(first);
        TextureDesc secondDesc = graph.GetTextureDesc(second);
        return firstDesc.Width == secondDesc.Width
            && firstDesc.Height == secondDesc.Height
            && firstDesc.SampleCount == secondDesc.SampleCount;
    }

    private MaterialShadeDispatch[] CreateDispatches(
        RenderGraph graph,
        IReadOnlyList<MaterialBin> states,
        in ShadeUniforms shadeData,
        string prefix,
        out UniformFrame uniforms)
    {
        if (states.Count == 0)
        {
            uniforms = default;
            return [];
        }

        var dispatches = new MaterialShadeDispatch[states.Count];
        var uniformData = new ShadeUniforms[states.Count];
        for (int index = 0; index < states.Count; index++)
        {
            MaterialBin state = states[index];
            state.Check("material shade");
            if (state.Compute.IsEmpty)
                throw new InvalidOperationException("material shade state has no compute shader entry.");

            ShadeUniforms perBinUniforms = shadeData;
            perBinUniforms.ShadingBin = checked((uint)state.BinIndex);
            int uniformIndex = index;
            uniformData[uniformIndex] = perBinUniforms;

            dispatches[index] = new MaterialShadeDispatch(
                index,
                state.ArgsIndex,
                uniformIndex,
                state.MaterialScalarRegion);
        }

        uniforms = _uniforms.Add(
            graph,
            uniformData,
            $"{prefix}ShadeUniforms");
        return dispatches;
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
        ShadeResources common,
        MaterialShadeDispatch dispatch,
        MaterialBindings material,
        MaterialResourceFallbacks? fallbacks)
    {
        foreach (BindingSetExecutionPlan setPlan in plan.Sets)
        {
            PassBindings bindings = context.Bindings(setPlan.Layout).Reserve(setPlan.BindingCount);
            foreach (BindingSlotExecutionPlan slotPlan in setPlan.Slots)
            {
                if (TryResolveCommon(slotPlan, context, common, dispatch, out BindingResourceDesc resource)
                    || TryResolveMaterial(slotPlan, material, out resource)
                    || TryResolveFallback(slotPlan, fallbacks, out resource))
                {
                    bindings = bindings.Resource(resource);
                    continue;
                }

                throw new InvalidOperationException(
                    $"material shade could not bind reflected resource slot set {slotPlan.First.Set} binding {slotPlan.First.Binding}: {slotPlan.Names}.");
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
        ShadeResources common,
        MaterialShadeDispatch dispatch,
        out BindingResourceDesc resource)
    {
        for (int i = 0; i < slotPlan.Steps.Length; i++)
        {
            if (TryResolveCommon(
                slotPlan.Steps[i].Kind,
                slotPlan.Steps[i].Binding,
                context,
                common,
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
        ShadeResources common,
        MaterialShadeDispatch dispatch,
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
                    common.Uniforms.Buffer,
                    new BufferViewDesc
                    {
                        Kind = ViewKind.ConstantBuffer,
                        Offset = common.Uniforms.Offset(dispatch.UniformIndex),
                        SizeInBytes = common.Uniforms.SlotBytes,
                    });
                return true;
            case CommonBindingKind.Instances:
                resource = binding.Buffer(context, common.Instances.Transform);
                return true;
            case CommonBindingKind.PreviousInstances:
                resource = binding.Buffer(context, common.Instances.PrevTransform);
                return true;
            case CommonBindingKind.InstanceHeaders:
                resource = binding.Buffer(context, common.Instances.Header);
                return true;
            case CommonBindingKind.InstanceDataHeap:
                resource = binding.Buffer(context, common.Instances.Data);
                return true;
            case CommonBindingKind.VisBuffer:
                resource = binding.Texture(
                    context,
                    common.Raster.VisBuffer,
                    new TextureViewDesc
                    {
                        Kind = ViewKind.ShaderResource,
                        Dimension = TextureViewDimension.Texture2D,
                        Format = Format.R32UInt,
                        MipCount = 1,
                        SliceCount = 1,
                    });
                return true;
            case CommonBindingKind.VisibleClusters:
                resource = binding.Buffer(context, common.Cull.VisibleClusters);
                return true;
            case CommonBindingKind.PageHeap:
                resource = binding.Buffer(context, common.Buffers.PageHeap);
                return true;
            case CommonBindingKind.OutputColor:
                resource = binding.Texture(context, common.OutputColor);
                return true;
            case CommonBindingKind.OutputMotionVectors:
                resource = binding.Texture(context, common.OutputMotionVectors);
                return true;
            case CommonBindingKind.DeformCache:
                RequireCacheBinding(common.DeformCache, binding.Name);
                resource = binding.Buffer(context, common.DeformCache);
                return true;
            case CommonBindingKind.CacheOffsets:
                RequireCacheBinding(common.CacheOffsets, binding.Name);
                resource = binding.Buffer(context, common.CacheOffsets);
                return true;
            case CommonBindingKind.MaterialScalarRegion:
                resource = binding.Buffer(context, dispatch.MaterialScalarRegion);
                return true;
            case CommonBindingKind.PixelCoordBuffer:
                resource = binding.Buffer(context, common.ShadeBin.PixelCoordBuffer);
                return true;
            case CommonBindingKind.BinOffsets:
                resource = binding.Buffer(context, common.ShadeBin.BinOffsets);
                return true;
            case CommonBindingKind.BinCounts:
                resource = binding.Buffer(context, common.ShadeBin.BinCounts);
                return true;
            case CommonBindingKind.BinIndirectArgs:
                resource = binding.Buffer(context, common.ShadeBin.BinIndirectArgs);
                return true;
            case CommonBindingKind.LightBuffer:
                resource = binding.Buffer(context, common.Lights.Buffer);
                return true;
            case CommonBindingKind.LightCounts:
                resource = binding.Buffer(
                    context,
                    common.LightCounts.Buffer,
                    new BufferViewDesc
                    {
                        Kind = ViewKind.ConstantBuffer,
                        Offset = common.LightCounts.Offset(0),
                        SizeInBytes = common.LightCounts.SlotBytes,
                    });
                return true;
            case CommonBindingKind.LightGridUniforms:
                resource = binding.Buffer(
                    context,
                    common.LightGridUniforms.Buffer,
                    new BufferViewDesc
                    {
                        Kind = ViewKind.ConstantBuffer,
                        Offset = common.LightGridUniforms.Offset(0),
                        SizeInBytes = common.LightGridUniforms.SlotBytes,
                    });
                return true;
            case CommonBindingKind.ClusterLightGrid:
                resource = binding.Buffer(context, common.Lights.ClusterLightGrid);
                return true;
            case CommonBindingKind.LightIndexList:
                resource = binding.Buffer(context, common.Lights.LightIndexList);
                return true;
            case CommonBindingKind.LightCookieAtlas:
                if (!common.LightCookieAtlas.IsValid)
                    return false;
                resource = binding.Texture(common.LightCookieAtlas);
                return true;
            case CommonBindingKind.LightCookieSampler:
                if (!common.LightCookieSampler.IsValid)
                    return false;
                resource = binding.Sampler(common.LightCookieSampler);
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static TextureViewHandle GetLightCookieTexture(
        in SceneLights sceneLights,
        MaterialResourceFallbacks materialFallbacks,
        AssetStore assets)
    {
        if (sceneLights.LightCookieAtlas.IsValid
            && assets.TryGet(sceneLights.LightCookieAtlas, out Texture? texture)
            && texture != null
            && texture.View.IsValid)
        {
            return texture.View;
        }

        return materialFallbacks.LightCookieAtlas;
    }

    private static void RequireCacheBinding(
        RenderGraphHandle handle,
        string resourceName)
    {
        if (!handle.IsValid)
        {
            throw new InvalidOperationException(
                $"material shade state reflects '{resourceName}', but the current pass did not provide that resource.");
        }
    }

    private static int AlignUp(int value, int alignment)
        => ((value + alignment - 1) / alignment) * alignment;

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MaterialShadePass));
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
                "Instances" => CommonBindingKind.Instances,
                "PreviousInstances" => CommonBindingKind.PreviousInstances,
                "InstanceHeaders" => CommonBindingKind.InstanceHeaders,
                "InstanceDataHeap" => CommonBindingKind.InstanceDataHeap,
                "VisBuffer" => CommonBindingKind.VisBuffer,
                "VisibleClusters" => CommonBindingKind.VisibleClusters,
                "PageHeap" => CommonBindingKind.PageHeap,
                "OutputColor" => CommonBindingKind.OutputColor,
                "OutputMotionVectors" => CommonBindingKind.OutputMotionVectors,
                "DeformCache" => CommonBindingKind.DeformCache,
                "CacheOffsets" => CommonBindingKind.CacheOffsets,
                "MaterialScalarRegion" => CommonBindingKind.MaterialScalarRegion,
                "PixelCoordBuffer" => CommonBindingKind.PixelCoordBuffer,
                "BinOffsets" => CommonBindingKind.BinOffsets,
                "BinCounts" => CommonBindingKind.BinCounts,
                "BinIndirectArgs" => CommonBindingKind.BinIndirectArgs,
                "LightBuffer" => CommonBindingKind.LightBuffer,
                "LightCounts" => CommonBindingKind.LightCounts,
                "LightGridUniforms" => CommonBindingKind.LightGridUniforms,
                "ClusterLightGrid" => CommonBindingKind.ClusterLightGrid,
                "LightIndexList" => CommonBindingKind.LightIndexList,
                "LightCookieAtlas" => CommonBindingKind.LightCookieAtlas,
                "LightCookieSampler" => CommonBindingKind.LightCookieSampler,
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
        Instances,
        PreviousInstances,
        InstanceHeaders,
        InstanceDataHeap,
        VisBuffer,
        VisibleClusters,
        PageHeap,
        OutputColor,
        OutputMotionVectors,
        DeformCache,
        CacheOffsets,
        MaterialScalarRegion,
        PixelCoordBuffer,
        BinOffsets,
        BinCounts,
        BinIndirectArgs,
        LightBuffer,
        LightCounts,
        LightGridUniforms,
        ClusterLightGrid,
        LightIndexList,
        LightCookieAtlas,
        LightCookieSampler,
    }

    private readonly record struct MaterialShadeDispatch(
        int StateIndex,
        int ArgsIndex,
        int UniformIndex,
        RenderGraphHandle MaterialScalarRegion);

    private readonly record struct ShadeResources(
        ClusterBuffers Buffers,
        InstanceFrame Instances,
        ClusterRasterOutput Raster,
        ClusterCullOutput Cull,
        ShadeBinFrame ShadeBin,
        UniformFrame Uniforms,
        LightGrid Lights,
        UniformFrame LightCounts,
        UniformFrame LightGridUniforms,
        RenderGraphHandle OutputColor,
        RenderGraphHandle OutputMotionVectors,
        RenderGraphHandle DeformCache,
        RenderGraphHandle CacheOffsets,
        TextureViewHandle LightCookieAtlas,
        SamplerHandle LightCookieSampler);

}

