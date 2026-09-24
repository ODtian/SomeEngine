using System.Runtime.InteropServices;
using System.Collections.Generic;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

[StructLayout(LayoutKind.Sequential)]
internal struct ClusterDeformUniforms
{
    public uint MaxVisibleClusters;
    public uint MaxDeformCacheBytes;
    public uint MaxClusterVertices;
    public uint CurrentBin;
    public uint Reserved0;
    public uint ResetCacheAllocationState;
}

internal readonly record struct DeformCacheResources(
    RenderGraphHandle DeformCache,
    RenderGraphHandle CacheOffsets,
    RenderGraphHandle CacheAllocationCounter,
    ulong CacheBytes)
{
    public bool IsEmpty
        => !DeformCache.IsValid
            && !CacheOffsets.IsValid
            && !CacheAllocationCounter.IsValid
            && CacheBytes == 0;

    public bool IsValid
        => DeformCache.IsValid
            && CacheOffsets.IsValid
            && CacheAllocationCounter.IsValid
            && CacheBytes > 0;
}

internal readonly record struct DeformCacheFrame(ClusterDeformUniforms UniformData);

internal sealed class ClusterDeformPass : IDisposable
{
    internal const ulong MaxCacheBytes = 0xFFFFF000UL;

    private readonly Dictionary<PipelineLayoutHandle, BindingPlan> _bindingPlans = [];
    private bool _disposed;

    public ClusterDeformPass(RenderContext renderContext)
    {
        ArgumentNullException.ThrowIfNull(renderContext);
    }

    public DeformCacheFrame CreateFrame(
        DeformCacheResources cache,
        DeformBinFrame deformBin,
        ClusterCullOutput cull,
        ClusterBuffers buffers,
        InstanceFrame instances,
        RenderGraphHandle drawArgs,
        RenderGraphHandle readOffsetArgs,
        bool resetCacheAllocationState)
    {
        ThrowIfDisposed();
        ValidateFrame(buffers, instances);
        ValidateCacheResources(cache, nameof(cache));
        if (!cull.VisibleClusters.IsValid)
            throw new ArgumentException("deform cache preparation requires valid cull output.", nameof(cull));
        if (!drawArgs.IsValid || !readOffsetArgs.IsValid)
            throw new ArgumentException("deform cache preparation requires valid draw/read-offset args.", nameof(drawArgs));
        if (!deformBin.DeformBinnedClusterIndex.IsValid || !deformBin.DeformBinMeta.IsValid)
            throw new ArgumentException("deform cache preparation requires valid deform bin output.", nameof(deformBin));

        var uniformData = new ClusterDeformUniforms
        {
            MaxVisibleClusters = ClusterLimits.MaxDraws,
            MaxDeformCacheBytes = checked((uint)Math.Min(cache.CacheBytes, uint.MaxValue)),
            MaxClusterVertices = ClusterLimits.MaxClusterVertices,
            Reserved0 = 0,
            ResetCacheAllocationState = resetCacheAllocationState ? 1u : 0u,
        };

        return new DeformCacheFrame(uniformData);
    }

    public void AddMaterialPass(
        RenderGraph graph,
        DeformCacheResources cache,
        DeformCacheFrame frame,
        DeformBinFrame deformBin,
        ClusterCullOutput cull,
        ClusterBuffers buffers,
        InstanceFrame instances,
        RenderGraphHandle drawArgs,
        RenderGraphHandle readOffsetArgs,
        IReadOnlyList<MaterialBin> deformStates,
        MaterialResourceFallbacks materialFallbacks,
        string tag)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(deformStates);
        ArgumentNullException.ThrowIfNull(materialFallbacks);
        ValidateFrame(buffers, instances);
        if (!cull.VisibleClusters.IsValid)
            throw new ArgumentException("material deform requires valid cull output.", nameof(cull));
        if (!cache.DeformCache.IsValid || !cache.CacheOffsets.IsValid || !cache.CacheAllocationCounter.IsValid)
            throw new ArgumentException("material deform requires valid deform cache resources.", nameof(cache));
        if (frame.UniformData.MaxDeformCacheBytes == 0)
            throw new ArgumentException("material deform requires a valid deform cache frame.", nameof(frame));
        if (!deformBin.DeformBinnedClusterIndex.IsValid
            || !deformBin.DeformBinMeta.IsValid
            || !deformBin.PreDeformDispatchArgs.IsValid)
        {
            throw new ArgumentException("material deform requires valid deform bin output.", nameof(deformBin));
        }

        string prefix = string.IsNullOrWhiteSpace(tag) ? string.Empty : $"{tag}_";
        DeformDispatch[] dispatches = CreateMaterialDispatches(
            deformStates,
            frame.UniformData,
            "Deform");
        if (dispatches.Length == 0)
            return;

        graph.AddComputePass(
            $"{prefix}Cluster Deform",
            builder =>
            {
                foreach (DeformDispatch dispatch in dispatches)
                {
                    builder.Read(dispatch.MaterialScalarRegion, ResourceState.ShaderResource);
                }

                builder.Read(deformBin.DeformBinnedClusterIndex, ResourceState.ShaderResource);
                builder.Read(cull.VisibleClusters, ResourceState.ShaderResource);
                builder.Read(deformBin.DeformBinMeta, ResourceState.ShaderResource);
                builder.Read(buffers.PageHeap, ResourceState.ShaderResource);
                builder.Read(instances.Transform, ResourceState.ShaderResource);
                builder.Read(instances.PrevTransform, ResourceState.ShaderResource);
                builder.Read(instances.Header, ResourceState.ShaderResource);
                builder.Read(instances.Data, ResourceState.ShaderResource);
                builder.Read(deformBin.PreDeformDispatchArgs, ResourceState.IndirectArgument);
                builder.Write(cache.DeformCache, ResourceState.UnorderedAccess);
                builder.Write(cache.CacheOffsets, ResourceState.UnorderedAccess);
                builder.ReadWrite(cache.CacheAllocationCounter, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                foreach (DeformDispatch dispatch in dispatches)
                {
                    MaterialBin state = deformStates[dispatch.StateIndex];
                    DispatchMaterialBin(
                        state,
                        pass,
                        context,
                        buffers,
                        instances,
                        deformBin,
                        cull,
                        cache,
                        dispatch.UniformData,
                        dispatch.MaterialScalarRegion,
                        state.MaterialBindings,
                        materialFallbacks,
                        deformBin.PreDeformDispatchArgs,
                        checked((ulong)dispatch.ArgsIndex * IndirectArgumentSize.Dispatch));
                }
            });
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }

    private DeformDispatch[] CreateMaterialDispatches(
        IReadOnlyList<MaterialBin> states,
        ClusterDeformUniforms uniformTemplate,
        string owner)
    {
        if (states.Count == 0)
        {
            return [];
        }

        var dispatches = new DeformDispatch[states.Count];
        for (int index = 0; index < states.Count; index++)
        {
            MaterialBin state = states[index];
            state.Check(owner);

            var data = uniformTemplate;
            data.CurrentBin = checked((uint)state.ArgsIndex);

            dispatches[index] = new DeformDispatch(
                index,
                state.ArgsIndex,
                state.MaterialScalarRegion,
                data);
        }

        return dispatches;
    }

    private void DispatchMaterialBin(
        MaterialBin state,
        IComputeCommands pass,
        RenderGraphContext context,
        ClusterBuffers buffers,
        InstanceFrame instances,
        DeformBinFrame deformBin,
        ClusterCullOutput cull,
        DeformCacheResources cache,
        ClusterDeformUniforms uniformData,
        RenderGraphHandle materialScalarRegion,
        MaterialBindings material,
        MaterialResourceFallbacks materialFallbacks,
        RenderGraphHandle dispatchArgs,
        ulong dispatchArgsOffset)
    {
        PipelineHandle pipeline = context.GetPipeline(state.PipelineState, PipelineNeed.Optional);
        if (!pipeline.IsValid)
            return;
        ShaderBindingTable bindings = state.Bindings
            ?? throw new InvalidOperationException("cluster deform material state has no reflected layout.");
        pass.SetPipeline(pipeline);
        pass.SetPushConstants(
            ShaderStageFlags.Compute,
            0,
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref uniformData, 1)));
        ApplyBindingPlan(
            pass,
            context,
            GetBindingPlan(bindings),
            buffers,
            instances,
            deformBin,
            cull,
            cache,
            materialScalarRegion,
            material,
            materialFallbacks);

        pass.DispatchIndirect(dispatchArgs, dispatchArgsOffset);
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
        DeformBinFrame deformBin,
        ClusterCullOutput cull,
        DeformCacheResources cache,
        RenderGraphHandle materialScalarRegion,
        MaterialBindings material,
        MaterialResourceFallbacks? fallbacks)
    {
        foreach (BindingSetExecutionPlan setPlan in plan.Sets)
        {
            PassBindings bindings = context.Bindings(setPlan.Layout).Reserve(setPlan.BindingCount);
            foreach (BindingSlotExecutionPlan slotPlan in setPlan.Slots)
            {
                if (TryResolveCommon(slotPlan, context, buffers, instances, deformBin, cull, cache, materialScalarRegion, out BindingResourceDesc resource, out RenderGraphAccess access))
                {
                    bindings = bindings.Resource(resource, access);
                    continue;
                }

                if (TryResolveMaterial(slotPlan, material, out resource)
                    || TryResolveFallback(slotPlan, fallbacks, out resource))
                {
                    bindings = bindings.Resource(resource);
                    continue;
                }

                throw new InvalidOperationException(
                    $"cluster deform could not bind reflected resource slot set {slotPlan.First.Set} binding {slotPlan.First.Binding}: {slotPlan.Names}.");
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
        DeformBinFrame deformBin,
        ClusterCullOutput cull,
        DeformCacheResources cache,
        RenderGraphHandle materialScalarRegion,
        out BindingResourceDesc resource,
        out RenderGraphAccess access)
    {
        for (int i = 0; i < slotPlan.Steps.Length; i++)
        {
            if (TryResolveCommon(
                slotPlan.Steps[i],
                context,
                buffers,
                instances,
                deformBin,
                cull,
                cache,
                materialScalarRegion,
                out resource))
            {
                access = slotPlan.Steps[i].Access;
                return true;
            }
        }

        resource = default;
        access = RenderGraphAccess.None;
        return false;
    }

    private static bool TryResolveCommon(
        BindingStep step,
        RenderGraphContext context,
        ClusterBuffers buffers,
        InstanceFrame instances,
        DeformBinFrame deformBin,
        ClusterCullOutput cull,
        DeformCacheResources cache,
        RenderGraphHandle materialScalarRegion,
        out BindingResourceDesc resource)
    {
        resource = default;
        switch (step.Kind)
        {
            case DeformBindingKind.None:
                return false;
            case DeformBindingKind.PageHeap:
                resource = step.Binding.Buffer(context, buffers.PageHeap, step.Access);
                return true;
            case DeformBindingKind.BinnedClusterIndexBuffer:
                resource = step.Binding.Buffer(context, deformBin.DeformBinnedClusterIndex, step.Access);
                return true;
            case DeformBindingKind.VisibleClusters:
                resource = step.Binding.Buffer(context, cull.VisibleClusters, step.Access);
                return true;
            case DeformBindingKind.DeformBinMeta:
                resource = step.Binding.Buffer(context, deformBin.DeformBinMeta, step.Access);
                return true;
            case DeformBindingKind.Instances:
                resource = step.Binding.Buffer(context, instances.Transform, step.Access);
                return true;
            case DeformBindingKind.PreviousInstances:
                resource = step.Binding.Buffer(context, instances.PrevTransform, step.Access);
                return true;
            case DeformBindingKind.InstanceHeaders:
                resource = step.Binding.Buffer(context, instances.Header, step.Access);
                return true;
            case DeformBindingKind.InstanceDataHeap:
                resource = step.Binding.Buffer(context, instances.Data, step.Access);
                return true;
            case DeformBindingKind.MaterialScalarRegion:
                resource = step.Binding.Buffer(context, materialScalarRegion, step.Access);
                return true;
            case DeformBindingKind.DeformDispatchArgs:
                resource = step.Binding.Buffer(context, deformBin.PreDeformDispatchArgs, step.Access);
                return true;
            case DeformBindingKind.DeformCache:
                resource = step.Binding.Buffer(context, cache.DeformCache, step.Access);
                return true;
            case DeformBindingKind.CacheOffsetsWrite:
                resource = step.Binding.Buffer(context, cache.CacheOffsets, step.Access);
                return true;
            case DeformBindingKind.CacheAllocationCounter:
                resource = step.Binding.Buffer(context, cache.CacheAllocationCounter, step.Access);
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(step), step.Kind, null);
        }
    }

    private static void ValidateFrame(
        ClusterBuffers buffers,
        InstanceFrame instances)
    {
        if (!buffers.PageHeap.IsValid
            || !instances.Transform.IsValid
            || !instances.PrevTransform.IsValid
            || !instances.Header.IsValid
            || !instances.Data.IsValid)
        {
            throw new ArgumentException("cluster deform requires valid cluster buffers and instance frame.", nameof(buffers));
        }
    }

    private static void ValidateCacheResources(DeformCacheResources cache, string paramName)
    {
        if (!cache.IsValid)
        {
            throw new ArgumentException(
                "deform cache preparation requires complete deform cache resources from the pipeline cache resource owner.",
                paramName);
        }

        if (cache.CacheBytes > MaxCacheBytes)
        {
            throw new ArgumentException(
                "deform cache byte capacity exceeds the shader-addressable deform cache range.",
                paramName);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ClusterDeformPass));
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
                steps[i] = new BindingStep(plan.Bindings[i], Classify(plan.Bindings[i].Name), Access(plan.Bindings[i]));

            return new BindingSlotExecutionPlan(
                plan.Bindings,
                steps,
                string.Join(", ", Array.ConvertAll(plan.Bindings, static binding => binding.Name)));
        }

        private static DeformBindingKind Classify(string name)
            => name switch
            {
                "PageHeap" => DeformBindingKind.PageHeap,
                "BinnedClusterIndexBuffer" => DeformBindingKind.BinnedClusterIndexBuffer,
                "VisibleClusters" => DeformBindingKind.VisibleClusters,
                "DeformBinMeta" => DeformBindingKind.DeformBinMeta,
                "Instances" => DeformBindingKind.Instances,
                "PreviousInstances" => DeformBindingKind.PreviousInstances,
                "InstanceHeaders" => DeformBindingKind.InstanceHeaders,
                "InstanceDataHeap" => DeformBindingKind.InstanceDataHeap,
                "MaterialScalarRegion" => DeformBindingKind.MaterialScalarRegion,
                "DeformDispatchArgs" => DeformBindingKind.DeformDispatchArgs,
                "DeformCache" => DeformBindingKind.DeformCache,
                "CacheOffsetsWrite" => DeformBindingKind.CacheOffsetsWrite,
                "CacheAllocationCounter" => DeformBindingKind.CacheAllocationCounter,
                _ => DeformBindingKind.None,
            };

        private static RenderGraphAccess Access(ReflectedBinding binding)
            => Classify(binding.Name) switch
            {
                DeformBindingKind.DeformCache or DeformBindingKind.CacheOffsetsWrite => RenderGraphAccess.WriteOnly,
                _ => PassBindings.BindingAccess(binding.Type),
            };
    }

    private readonly record struct BindingStep(
        ReflectedBinding Binding,
        DeformBindingKind Kind,
        RenderGraphAccess Access);

    private enum DeformBindingKind
    {
        None,
        PageHeap,
        BinnedClusterIndexBuffer,
        VisibleClusters,
        DeformBinMeta,
        Instances,
        PreviousInstances,
        InstanceHeaders,
        InstanceDataHeap,
        MaterialScalarRegion,
        DeformDispatchArgs,
        DeformCache,
        CacheOffsetsWrite,
        CacheAllocationCounter,
    }

    private readonly record struct DeformDispatch(
        int StateIndex,
        int ArgsIndex,
        RenderGraphHandle MaterialScalarRegion,
        ClusterDeformUniforms UniformData);
}

