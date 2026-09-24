using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using SomeEngine.Core.Collections;
using SomeEngine.Core.Diagnostics;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace SomeEngine.Rhi.D3D12;

internal sealed class DescriptorTables(
    uint setIndex,
    BindingLayoutHandle layout,
    LayoutSignature signature,
    BindingResourceDesc[] resources,
    DynamicOffset[] dynamicOffsets,
    ShaderDescriptorAllocation? resourceDescriptors,
    ShaderDescriptorAllocation? dynamicResourceDescriptors,
    ShaderDescriptorAllocation? samplerDescriptors)
{
    public uint SetIndex { get; } = setIndex;
    public BindingLayoutHandle Layout { get; } = layout;
    public LayoutSignature Signature { get; } = signature;
    public BindingResourceDesc[] Resources { get; } = resources;
    public DynamicOffset[] DynamicOffsets { get; } = dynamicOffsets;
    public ShaderDescriptorAllocation? ResourceDescriptors { get; } = resourceDescriptors;
    public ShaderDescriptorAllocation? DynamicResourceDescriptors { get; } = dynamicResourceDescriptors;
    public ShaderDescriptorAllocation? SamplerDescriptors { get; } = samplerDescriptors;
}

internal sealed class DescriptorCache
{
    private const int SlotCount = 64;
    private readonly Dictionary<int, List<DescriptorTables>> _buckets = [];
    private readonly DescriptorTables?[] _slots = new DescriptorTables?[SlotCount];
    private DescriptorTables? _recent;

    public bool TryGet(
        uint setIndex,
        BindingLayoutHandle layout,
        ReadOnlySpan<BindingResourceDesc> resources,
        ReadOnlySpan<DynamicOffset> dynamicOffsets,
        [NotNullWhen(true)] out DescriptorTables? tables)
    {
        DescriptorTables? recent = _recent;
        if (recent != null && Matches(recent, setIndex, layout, resources, dynamicOffsets))
        {
            tables = recent;
            return true;
        }

        int hash = Hash(setIndex, layout, resources, dynamicOffsets);
        int slot = Slot(hash);
        DescriptorTables? slotTables = _slots[slot];
        if (slotTables != null && Matches(slotTables, setIndex, layout, resources, dynamicOffsets))
        {
            tables = slotTables;
            _recent = slotTables;
            return true;
        }

        if (!_buckets.TryGetValue(hash, out List<DescriptorTables>? bucket))
        {
            tables = null;
            return false;
        }

        for (int index = 0; index < bucket.Count; index++)
        {
            tables = bucket[index];
            if (Matches(tables, setIndex, layout, resources, dynamicOffsets))
            {
                _slots[slot] = tables;
                _recent = tables;
                return true;
            }
        }

        tables = null;
        return false;
    }

    public void Add(
        uint setIndex,
        BindingLayoutHandle layout,
        LayoutSignature signature,
        BindingResourceDesc[] resources,
        ReadOnlySpan<DynamicOffset> dynamicOffsets,
        ShaderDescriptorAllocation? resourceDescriptors,
        ShaderDescriptorAllocation? dynamicResourceDescriptors,
        ShaderDescriptorAllocation? samplerDescriptors)
    {
        AddCore(
            setIndex,
            layout,
            signature,
            resources,
            dynamicOffsets.ToArray(),
            resourceDescriptors,
            dynamicResourceDescriptors,
            samplerDescriptors);
    }

    public void Add(
        uint setIndex,
        BindingLayoutHandle layout,
        LayoutSignature signature,
        ReadOnlySpan<BindingResourceDesc> resources,
        ReadOnlySpan<DynamicOffset> dynamicOffsets,
        ShaderDescriptorAllocation? resourceDescriptors,
        ShaderDescriptorAllocation? dynamicResourceDescriptors,
        ShaderDescriptorAllocation? samplerDescriptors)
    {
        AddCore(
            setIndex,
            layout,
            signature,
            resources.ToArray(),
            dynamicOffsets.ToArray(),
            resourceDescriptors,
            dynamicResourceDescriptors,
            samplerDescriptors);
    }

    private void AddCore(
        uint setIndex,
        BindingLayoutHandle layout,
        LayoutSignature signature,
        BindingResourceDesc[] resources,
        DynamicOffset[] dynamicOffsets,
        ShaderDescriptorAllocation? resourceDescriptors,
        ShaderDescriptorAllocation? dynamicResourceDescriptors,
        ShaderDescriptorAllocation? samplerDescriptors)
    {
        var tables = new DescriptorTables(
            setIndex,
            layout,
            signature,
            resources,
            dynamicOffsets,
            resourceDescriptors,
            dynamicResourceDescriptors,
            samplerDescriptors);
        int hash = Hash(setIndex, layout, resources, dynamicOffsets);
        if (!_buckets.TryGetValue(hash, out List<DescriptorTables>? bucket))
        {
            bucket = [];
            _buckets.Add(hash, bucket);
        }

        bucket.Add(tables);
        _slots[Slot(hash)] = tables;
        _recent = tables;
    }

    private static int Hash(
        uint setIndex,
        BindingLayoutHandle layout,
        ReadOnlySpan<BindingResourceDesc> resources,
        ReadOnlySpan<DynamicOffset> dynamicOffsets)
    {
        HashCode hash = new();
        hash.Add(setIndex);
        hash.Add(layout);
        for (int index = 0; index < resources.Length; index++)
            hash.Add(resources[index]);
        for (int index = 0; index < dynamicOffsets.Length; index++)
            hash.Add(dynamicOffsets[index]);
        return hash.ToHashCode();
    }

    private static int Slot(int hash)
        => hash & (SlotCount - 1);

    private static bool Matches(
        DescriptorTables? tables,
        uint setIndex,
        BindingLayoutHandle layout,
        ReadOnlySpan<BindingResourceDesc> resources,
        ReadOnlySpan<DynamicOffset> dynamicOffsets)
    {
        return tables != null
            && tables.SetIndex == setIndex
            && tables.Layout == layout
            && tables.Resources.AsSpan().SequenceEqual(resources)
            && tables.DynamicOffsets.AsSpan().SequenceEqual(dynamicOffsets);
    }
}

internal sealed class DescriptorBinder
{
    private readonly D3D12CommandList _owner;
    private readonly D3D12Device _device;
    private readonly bool _graphics;
    private DescriptorCache? _cache;

    public DescriptorBinder(D3D12CommandList owner, D3D12Device device, bool graphics)
    {
        _owner = owner;
        _device = device;
        _graphics = graphics;
    }

    public void SetBindingSet(
        PipeLayoutRecord pipelineLayout,
        uint setIndex,
        BindingSetHandle bindingSet,
        ReadOnlySpan<DynamicOffset> dynamicOffsets)
    {
        bool alreadyBoundSet = bindingSet.IsValid
            && dynamicOffsets.IsEmpty
            && _owner.IsBoundSet(_graphics, setIndex, bindingSet);
        if (alreadyBoundSet && !_device.ValidationEnabled)
        {
            return;
        }

        using var scope = Profiler.BeginScope("D3D12.SetBindingSet.Persistent");
        var set = _device.BindingSets.Get(bindingSet, "BindingSet");

        if (_device.ValidationEnabled)
        {
            RequireLayout(pipelineLayout, setIndex, set.LayoutSignature);
            if (alreadyBoundSet)
            {
                _device.RequireBindStates(_owner, set.BindStates);
                return;
            }
        }

        bool canTrackAsBoundSet = CanTrackAsBoundSet(set, dynamicOffsets);
        if (!dynamicOffsets.IsEmpty && TryRebind(pipelineLayout, setIndex, set.Layout, set.Resources, dynamicOffsets))
        {
            TrackSet(bindingSet, set);
            _owner.ClearBoundSet(_graphics, setIndex);
            return;
        }

        LayoutRecord? layout = null;
        if (!dynamicOffsets.IsEmpty || set.HasDynamicResourceSlots)
        {
            layout = _device.BindingLayouts.Get(set.Layout, "BindingLayout");
            _device.ValidateDynamicOffsets(layout, set.Resources, dynamicOffsets);
        }

        ShaderDescriptorAllocation? transientDynamicResourceDescriptors = null;
        bool descriptorsTracked = false;
        try
        {
            var resourceDescriptors = set.ResourceDescriptors;
            var dynamicResourceDescriptors = set.DynamicResourceDescriptors;
            if (!dynamicOffsets.IsEmpty)
            {
                transientDynamicResourceDescriptors = _device.CreateTransientDynamic(layout!, set.Resources, dynamicOffsets);
                dynamicResourceDescriptors = transientDynamicResourceDescriptors;
            }

            BindTables(pipelineLayout, setIndex, resourceDescriptors, dynamicResourceDescriptors, set.SamplerDescriptors);
            bool firstSetUse = TrackSet(bindingSet, set);
            if (_device.ValidationEnabled)
                _device.RequireBindStates(_owner, set.BindStates);
            else if (firstSetUse)
                _owner.TrackSetUses(set);
            if (canTrackAsBoundSet)
                _owner.TrackBoundSet(_graphics, setIndex, bindingSet);
            else
                _owner.ClearBoundSet(_graphics, setIndex);
            _owner.TrackTransientDescriptor(transientDynamicResourceDescriptors, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            descriptorsTracked = true;
            if (!dynamicOffsets.IsEmpty)
            {
                (_cache ??= new DescriptorCache()).Add(
                    setIndex,
                    set.Layout,
                    set.LayoutSignature,
                    set.Resources,
                    dynamicOffsets,
                    resourceDescriptors,
                    dynamicResourceDescriptors,
                    set.SamplerDescriptors);
            }
        }
        catch
        {
            if (!descriptorsTracked)
                _device.FreeTransientDescriptor(transientDynamicResourceDescriptors, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            throw;
        }
    }

    public void SetBindings(
        PipeLayoutRecord pipelineLayout,
        uint setIndex,
        BindingLayoutHandle layout,
        ReadOnlySpan<BindingResourceDesc> resources,
        ReadOnlySpan<DynamicOffset> dynamicOffsets)
    {
        using var scope = Profiler.BeginScope("D3D12.SetBindings.Transient");
        LayoutRecord layoutRecord;
        using (Profiler.BeginScope("D3D12.SetBindings.Validate"))
        {
            if (TryRebind(pipelineLayout, setIndex, layout, resources, dynamicOffsets))
            {
                _owner.ClearBoundSet(_graphics, setIndex);
                return;
            }

            layoutRecord = _device.BindingLayouts.Get(layout, "BindingLayout");
            RequireLayout(pipelineLayout, setIndex, layoutRecord.Signature);
            _device.ValidateTransientBindings(layoutRecord, resources);
            _device.ValidateDynamicOffsets(layoutRecord, resources, dynamicOffsets);
            _device.RequireBindStates(_owner, resources);
        }

        ShaderDescriptorAllocation? resourceDescriptors = null;
        ShaderDescriptorAllocation? dynamicResourceDescriptors = null;
        ShaderDescriptorAllocation? samplerDescriptors = null;
        bool descriptorsTracked = false;
        try
        {
            using (Profiler.BeginScope("D3D12.SetBindings.CreateDescriptors"))
            {
                resourceDescriptors = _device.CreateTransientResources(layoutRecord, resources);
                dynamicResourceDescriptors = _device.CreateTransientDynamic(layoutRecord, resources, dynamicOffsets);
                samplerDescriptors = _device.CreateTransientSamplers(layoutRecord, resources);
                _device.ReportDescriptors(
                    (resourceDescriptors?.Count ?? 0) + (dynamicResourceDescriptors?.Count ?? 0),
                    samplerDescriptors?.Count ?? 0);
            }

            using (Profiler.BeginScope("D3D12.SetBindings.BindRootTables"))
            {
                BindTables(pipelineLayout, setIndex, resourceDescriptors, dynamicResourceDescriptors, samplerDescriptors);
                _owner.ClearBoundSet(_graphics, setIndex);
            }

            using (Profiler.BeginScope("D3D12.SetBindings.Track"))
            {
                _owner.TrackBindingLayout(layout);
                _owner.TrackBindingResources(resources);
                _owner.TrackTransientDescriptor(resourceDescriptors, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
                _owner.TrackTransientDescriptor(dynamicResourceDescriptors, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
                _owner.TrackTransientDescriptor(samplerDescriptors, DescriptorHeapType.Sampler);
            }

            descriptorsTracked = true;
            (_cache ??= new DescriptorCache()).Add(
                setIndex,
                layout,
                layoutRecord.Signature,
                resources,
                dynamicOffsets,
                resourceDescriptors,
                dynamicResourceDescriptors,
                samplerDescriptors);
        }
        catch
        {
            if (!descriptorsTracked)
            {
                _device.FreeTransientDescriptor(resourceDescriptors, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
                _device.FreeTransientDescriptor(dynamicResourceDescriptors, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
                _device.FreeTransientDescriptor(samplerDescriptors, DescriptorHeapType.Sampler);
            }

            throw;
        }
    }

    private bool TrackSet(BindingSetHandle bindingSet, BindingSetRecord set)
    {
        bool firstSetUse = _owner.TrackBindingSet(bindingSet);
        if (firstSetUse)
        {
            _owner.TrackBindingLayout(set.Layout);
            if (_device.ValidationEnabled)
                _owner.TrackSetAccel(set);
        }

        return firstSetUse;
    }

    private static bool CanTrackAsBoundSet(BindingSetRecord set, ReadOnlySpan<DynamicOffset> dynamicOffsets)
        => dynamicOffsets.IsEmpty && !set.HasDynamicResourceSlots;

    private bool TryRebind(
        PipeLayoutRecord pipelineLayout,
        uint setIndex,
        BindingLayoutHandle layout,
        ReadOnlySpan<BindingResourceDesc> resources,
        ReadOnlySpan<DynamicOffset> dynamicOffsets)
    {
        if (_cache == null
            || !_cache.TryGet(setIndex, layout, resources, dynamicOffsets, out DescriptorTables? tables))
        {
            return false;
        }

        if (_device.ValidationEnabled)
        {
            RequireLayout(pipelineLayout, setIndex, tables.Signature);
            _device.RequireBindStates(_owner, tables.Resources);
        }
        else
        {
            _owner.TrackBindingLayout(layout);
            _owner.TrackBindingResources(tables.Resources);
        }
        using (Profiler.BeginScope("D3D12.SetBindings.BindRootTables"))
        {
            BindTables(
                pipelineLayout,
                setIndex,
                tables.ResourceDescriptors,
                tables.DynamicResourceDescriptors,
                tables.SamplerDescriptors);
        }

        return true;
    }

    private void BindTables(
        PipeLayoutRecord pipelineLayout,
        uint setIndex,
        ShaderDescriptorAllocation? resourceDescriptors,
        ShaderDescriptorAllocation? dynamicResourceDescriptors,
        ShaderDescriptorAllocation? samplerDescriptors)
    {
        var binding = SetBinding(pipelineLayout, setIndex);
        if (resourceDescriptors != null || dynamicResourceDescriptors != null || samplerDescriptors != null)
            _owner.EnsureDescHeaps();

        if (binding.ResourceTable >= 0 && resourceDescriptors is { } resources)
        {
            SetTable((uint)binding.ResourceTable, resources);
        }
        if (binding.DynamicResourceTable >= 0 && dynamicResourceDescriptors is { } dynamicResources)
        {
            SetTable((uint)binding.DynamicResourceTable, dynamicResources);
        }
        if (binding.SamplerTable >= 0 && samplerDescriptors is { } samplers)
        {
            SetTable((uint)binding.SamplerTable, samplers);
        }
    }

    private void SetTable(uint root, ShaderDescriptorAllocation descriptors)
    {
        _ = _graphics
            ? _owner.SetGraphicsTable(root, descriptors.Gpu)
            : _owner.SetComputeTable(root, descriptors.Gpu);
    }

    private static RootBinding SetBinding(PipeLayoutRecord pipelineLayout, uint setIndex)
    {
        if (setIndex >= pipelineLayout.Sets.Length)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Pipeline layout has no set {setIndex}.");
        return pipelineLayout.Sets[(int)setIndex];
    }

    private static void RequireLayout(
        PipeLayoutRecord pipelineLayout,
        uint setIndex,
        LayoutSignature signature)
    {
        if (setIndex >= pipelineLayout.SetSignatures.Length)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Pipeline layout has no set {setIndex}.");
        var expectedSignature = pipelineLayout.SetSignatures[(int)setIndex];
        if (signature != expectedSignature)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Binding layout is incompatible with the current pipeline set.");
    }
}

internal sealed class RenderPass : IRenderPass
{
    private readonly D3D12CommandList _owner;
    private readonly D3D12Device _device;
    private readonly PassCompatibility _compatibility;
    private readonly ColorResolve[] _resolves;
    private readonly int _resolveCount;
    private InlineFlatCore<uint, VertexBufferBinding> _vertexBufferBindings;
    private readonly DescriptorBinder _bindings;
    private PipelineRecord? _pipeline;
    private PipeLayoutRecord? _layout;
    private PipelineHandle _pipelineHandle;
    private bool _ended;
    private bool _hasIndexBuffer;

    internal static void ValidateForBegin(D3D12CommandList owner, D3D12Device device, in RenderPassDesc desc)
    {
        Validation.RenderPassDesc(desc, device.Limits);
        uint? sampleCount = null;
        for (int index = 0; index < desc.ColorAttachments.Length; index++)
        {
            var attachment = desc.ColorAttachments[index];
            var view = device.TextureViews.Get(attachment.View, "ColorAttachment");
            if (view.Kind != ViewKind.RenderTarget)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Color attachment requires a render-target texture view.");
            var texture = device.Textures.Get(view.Texture, "ColorAttachmentTexture");
            owner.ValidateTextureState(
                view.Texture,
                texture.Desc,
                view.Desc,
                ResourceState.RenderTarget,
                "Color attachment texture",
                trackWhenValidationDisabled: false);
            ValidateRenderArea(texture.Desc, view.Desc, desc.RenderArea);
            sampleCount = ValidatePassSamples(sampleCount, texture.Desc.SampleCount);

            if (!attachment.ResolveTarget.IsValid)
                continue;

            var resolve = device.TextureViews.Get(attachment.ResolveTarget, "ResolveTarget");
            if (resolve.Kind != ViewKind.RenderTarget)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve target requires a render-target texture view.");
            var resolveTexture = device.Textures.Get(resolve.Texture, "ResolveTargetTexture");
            if (texture.Desc.SampleCount == 1)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve requires a multisampled color attachment.");
            if (resolveTexture.Desc.SampleCount != 1)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve target must be single-sampled.");
            if (resolve.Desc.Format != view.Desc.Format)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve target format must match the color attachment format.");
            if (view.Desc.MipCount != 1 || view.Desc.SliceCount != 1 || resolve.Desc.MipCount != 1 || resolve.Desc.SliceCount != 1)
                throw new RhiException(ErrorCode.UnsupportedFeature, "D3D12 render-pass resolve supports single-subresource views in v0.");
            if (view.Texture == resolve.Texture && view.Desc.FirstMip == resolve.Desc.FirstMip && view.Desc.FirstSlice == resolve.Desc.FirstSlice)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve source and target must be different subresources.");
            owner.ValidateTextureState(
                resolve.Texture,
                resolveTexture.Desc,
                resolve.Desc,
                ResourceState.RenderTarget,
                "Resolve target texture",
                trackWhenValidationDisabled: false);
            ValidateRenderArea(resolveTexture.Desc, resolve.Desc, desc.RenderArea);
            ValidateResolveArea(texture.Desc, view.Desc, desc.RenderArea, "Render pass resolve source");
            ValidateResolveArea(resolveTexture.Desc, resolve.Desc, desc.RenderArea, "Render pass resolve target");
            ValidateResolveCompat(texture.Desc, view.Desc, resolveTexture.Desc, resolve.Desc);
        }

        if (desc.DepthStencilAttachment is not { } depth)
            return;

        var depthView = device.TextureViews.Get(depth.View, "DepthStencilAttachment");
        if (depthView.Kind != ViewKind.DepthStencil)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Depth/stencil attachment requires a depth-stencil texture view.");
        if (depth.DepthReadOnly && depth.DepthLoadOp == LoadOp.Clear)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Read-only depth attachments cannot use Clear load op.");
        var depthTexture = device.Textures.Get(depthView.Texture, "DepthStencilAttachmentTexture");
        var required = depth.DepthReadOnly ? ResourceState.DepthRead : ResourceState.DepthWrite;
        owner.ValidateTextureState(
            depthView.Texture,
            depthTexture.Desc,
            depthView.Desc,
            required,
            "Depth attachment texture",
            trackWhenValidationDisabled: false);
        ValidateRenderArea(depthTexture.Desc, depthView.Desc, desc.RenderArea);
        _ = ValidatePassSamples(sampleCount, depthTexture.Desc.SampleCount);
    }

    public RenderPass(D3D12CommandList owner, D3D12Device device, in RenderPassDesc desc, bool validated = false)
    {
        _owner = owner;
        _device = device;
        _bindings = new DescriptorBinder(owner, device, graphics: true);
        if (!validated)
            ValidateForBegin(owner, device, desc);
        Span<CpuDescriptorHandle> rtvs = stackalloc CpuDescriptorHandle[desc.ColorAttachments.Length];
        ColorResolve[] resolves = [];
        int resolveCount = 0;
        Format color0 = Format.Unknown;
        Format color1 = Format.Unknown;
        Format color2 = Format.Unknown;
        Format color3 = Format.Unknown;
        Format color4 = Format.Unknown;
        Format color5 = Format.Unknown;
        Format color6 = Format.Unknown;
        Format color7 = Format.Unknown;
        int colorCount = 0;
        uint? sampleCount = null;
        for (int index = 0; index < desc.ColorAttachments.Length; index++)
        {
            var attachment = desc.ColorAttachments[index];
            var view = device.TextureViews.Get(attachment.View, "ColorAttachment");
            if (!validated && view.Kind != ViewKind.RenderTarget)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Color attachment requires a render-target texture view.");
            var texture = device.Textures.Get(view.Texture, "ColorAttachmentTexture");
            if (!validated)
            {
                owner.ValidateTextureState(view.Texture, texture.Desc, view.Desc, ResourceState.RenderTarget, "Color attachment texture");
                ValidateRenderArea(texture.Desc, view.Desc, desc.RenderArea);
            }
            owner.TrackTextureView(attachment.View);
            owner.RequireTextureState(view.Texture, texture.Desc, view.Desc, ResourceState.RenderTarget, "Color attachment texture");
            SetColorFormat(ref color0, ref color1, ref color2, ref color3, ref color4, ref color5, ref color6, ref color7, colorCount, view.Desc.Format);
            colorCount++;
            sampleCount = ValidatePassSamples(sampleCount, texture.Desc.SampleCount);
            rtvs[index] = view.Descriptor;
            if (attachment.LoadOp == LoadOp.Clear)
            {
                owner.List.ClearRenderTargetView(view.Descriptor, new Color4(attachment.ClearColor.R, attachment.ClearColor.G, attachment.ClearColor.B, attachment.ClearColor.A));
            }

            if (attachment.ResolveTarget.IsValid)
            {
                var resolve = device.TextureViews.Get(attachment.ResolveTarget, "ResolveTarget");
                var resolveTexture = device.Textures.Get(resolve.Texture, "ResolveTargetTexture");
                if (!validated)
                {
                    if (resolve.Kind != ViewKind.RenderTarget)
                        throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve target requires a render-target texture view.");
                    if (texture.Desc.SampleCount == 1)
                        throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve requires a multisampled color attachment.");
                    if (resolveTexture.Desc.SampleCount != 1)
                        throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve target must be single-sampled.");
                    if (resolve.Desc.Format != view.Desc.Format)
                        throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve target format must match the color attachment format.");
                    if (view.Desc.MipCount != 1 || view.Desc.SliceCount != 1 || resolve.Desc.MipCount != 1 || resolve.Desc.SliceCount != 1)
                        throw new RhiException(ErrorCode.UnsupportedFeature, "D3D12 render-pass resolve supports single-subresource views in v0.");
                    if (view.Texture == resolve.Texture && view.Desc.FirstMip == resolve.Desc.FirstMip && view.Desc.FirstSlice == resolve.Desc.FirstSlice)
                        throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve source and target must be different subresources.");
                    owner.ValidateTextureState(resolve.Texture, resolveTexture.Desc, resolve.Desc, ResourceState.RenderTarget, "Resolve target texture");
                    ValidateRenderArea(resolveTexture.Desc, resolve.Desc, desc.RenderArea);
                    ValidateResolveArea(texture.Desc, view.Desc, desc.RenderArea, "Render pass resolve source");
                    ValidateResolveArea(resolveTexture.Desc, resolve.Desc, desc.RenderArea, "Render pass resolve target");
                    ValidateResolveCompat(texture.Desc, view.Desc, resolveTexture.Desc, resolve.Desc);
                }
                owner.TrackTextureView(attachment.ResolveTarget);
                owner.RequireTextureState(resolve.Texture, resolveTexture.Desc, resolve.Desc, ResourceState.RenderTarget, "Resolve target texture");
                if (resolves.Length == 0)
                    resolves = new ColorResolve[desc.ColorAttachments.Length];
                resolves[resolveCount++] = new ColorResolve(view.Texture, view.Desc, resolve.Texture, resolve.Desc, D3D12Mappings.ToDxgi(view.Desc.Format));
            }
        }

        CpuDescriptorHandle? dsv = null;
        Format depthStencilFormat = Format.Unknown;
        bool depthReadOnly = false;
        bool clearDepth = false;
        ClearDepthStencil depthClearValue = default;
        CpuDescriptorHandle depthClearDescriptor = default;
        if (desc.DepthStencilAttachment is { } depth)
        {
            var view = device.TextureViews.Get(depth.View, "DepthStencilAttachment");
            if (!validated)
            {
                if (view.Kind != ViewKind.DepthStencil)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Depth/stencil attachment requires a depth-stencil texture view.");
                if (depth.DepthReadOnly && depth.DepthLoadOp == LoadOp.Clear)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Read-only depth attachments cannot use Clear load op.");
            }
            var texture = device.Textures.Get(view.Texture, "DepthStencilAttachmentTexture");
            var required = depth.DepthReadOnly ? ResourceState.DepthRead : ResourceState.DepthWrite;
            if (!validated)
            {
                owner.ValidateTextureState(view.Texture, texture.Desc, view.Desc, required, "Depth attachment texture");
                ValidateRenderArea(texture.Desc, view.Desc, desc.RenderArea);
            }
            depthStencilFormat = view.Desc.Format;
            depthReadOnly = depth.DepthReadOnly;
            sampleCount = ValidatePassSamples(sampleCount, texture.Desc.SampleCount);
            dsv = depth.DepthReadOnly ? view.ReadOnlyDepthDescriptor : view.Descriptor;
            owner.TrackTextureView(depth.View);
            owner.RequireTextureState(view.Texture, texture.Desc, view.Desc, required, "Depth attachment texture");
            if (depth.DepthLoadOp == LoadOp.Clear)
            {
                clearDepth = true;
                depthClearValue = depth.ClearValue;
                depthClearDescriptor = view.Descriptor;
            }
        }

        _compatibility = new PassCompatibility(
            color0,
            color1,
            color2,
            color3,
            color4,
            color5,
            color6,
            color7,
            colorCount,
            depthStencilFormat,
            sampleCount ?? 1,
            depthReadOnly);
        _resolves = resolves;
        _resolveCount = resolveCount;

        owner.List.OMSetRenderTargets(rtvs, dsv);

        if (clearDepth)
        {
            owner.List.ClearDepthStencilView(depthClearDescriptor, ClearFlags.Depth, depthClearValue.Depth, depthClearValue.Stencil);
        }

        if (desc.RenderArea.Width > 0 && desc.RenderArea.Height > 0)
        {
            owner.SetViewport(desc.RenderArea.X, desc.RenderArea.Y, desc.RenderArea.Width, desc.RenderArea.Height, 0, 1);
            owner.SetScissorRect(desc.RenderArea.X, desc.RenderArea.Y, desc.RenderArea.X + desc.RenderArea.Width, desc.RenderArea.Y + desc.RenderArea.Height);
        }
    }

    public void SetViewport(Viewport viewport)
    {
        ThrowIfEnded();
        _owner.SetViewport(viewport.X, viewport.Y, viewport.Width, viewport.Height, viewport.MinDepth, viewport.MaxDepth);
    }

    public void SetScissor(Rect rect)
    {
        ThrowIfEnded();
        _owner.SetScissorRect(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);
    }

    public void SetPipeline(PipelineHandle pipeline)
    {
        ThrowIfEnded();
        if (_pipeline != null && pipeline == _pipelineHandle)
        {
            return;
        }

        using var scope = Profiler.BeginScope("D3D12.SetPipeline");
        var record = _device.Pipelines.Get(pipeline, "Pipeline");
        if (record.Kind != PipelineKind.Graphics && record.Kind != PipelineKind.Mesh)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass requires a graphics or mesh pipeline.");
        ValidatePassCompatibility(record, _compatibility);
        _pipeline = record;
        _layout = record.LayoutRecord;
        _pipelineHandle = pipeline;
        _owner.TrackPipeline(pipeline);
        _owner.TrackPipelineLayout(record.Layout);
        _owner.SetGraphicsState(record.State!);
        _owner.SetGraphicsRoot(_layout.RootSignature);
        RebindVertexBuffers();
    }

    public void SetBindingSet(uint setIndex, BindingSetHandle bindingSet, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)
    {
        ThrowIfEnded();
        RequirePipeline();
        _bindings.SetBindingSet(_layout!, setIndex, bindingSet, dynamicOffsets);
    }

    public void SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)
    {
        ThrowIfEnded();
        RequirePipeline();
        _bindings.SetBindings(_layout!, setIndex, layout, resources, dynamicOffsets);
    }

    public void SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data)
    {
        ThrowIfEnded();
        RequirePipeline();
        var binding = FindPushRoot(stages, offset, data.Length);
        _owner.List.SetGraphicsRoot32BitConstants(checked((uint)binding.RootParameter), MemoryMarshal.Cast<byte, uint>(data), checked((uint)((offset - binding.Offset) / 4)));
    }

    public void SetVertexBuffer(uint slot, BufferHandle buffer, ulong offset = 0)
    {
        ThrowIfEnded();
        var record = _device.Buffers.Get(buffer, "VertexBuffer");
        if (!record.Desc.BindFlags.HasFlag(BindFlags.VertexBuffer))
            throw new RhiException(ErrorCode.InvalidDescriptor, "SetVertexBuffer requires a buffer with VertexBuffer bind flag.");
        if (offset >= record.Desc.SizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Vertex buffer offset is outside the buffer.");
        _owner.RequireBufferState(buffer, ResourceState.VertexBuffer, "Vertex buffer");
        _vertexBufferBindings.Set(slot, new VertexBufferBinding(buffer, offset));
        if (_pipeline?.Kind == PipelineKind.Graphics)
            BindVertexBuffer(slot, record, offset);
    }

    public void SetIndexBuffer(BufferHandle buffer, IndexFormat format, ulong offset = 0)
    {
        ThrowIfEnded();
        var record = _device.Buffers.Get(buffer, "IndexBuffer");
        if (!Enum.IsDefined(format))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Index format value {format} is not defined.");
        if (!record.Desc.BindFlags.HasFlag(BindFlags.IndexBuffer))
            throw new RhiException(ErrorCode.InvalidDescriptor, "SetIndexBuffer requires a buffer with IndexBuffer bind flag.");
        if (offset >= record.Desc.SizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Index buffer offset is outside the buffer.");
        _owner.RequireBufferState(buffer, ResourceState.IndexBuffer, "Index buffer");
        _owner.List.IASetIndexBuffer(new IndexBufferView(record.Resource.GPUVirtualAddress + offset, checked((uint)(record.Desc.SizeInBytes - offset)), format == IndexFormat.UInt16 ? Vortice.DXGI.Format.R16_UInt : Vortice.DXGI.Format.R32_UInt));
        _hasIndexBuffer = true;
    }

    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        ThrowIfEnded();
        RequireGraphicsPipeline("Draw");
        using var scope = Profiler.BeginScope("D3D12.Draw");
        if (vertexCount == 0 || instanceCount == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Draw vertex count and instance count must be greater than zero.");
        _owner.SetPrimitiveTopology(D3D12Mappings.ToPrimitiveTopology(_pipeline!.Topology, _pipeline.GraphicsDesc?.PatchControlPoints ?? 0));
        _owner.List.DrawInstanced(vertexCount, instanceCount, firstVertex, firstInstance);
    }

    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
    {
        ThrowIfEnded();
        RequireGraphicsPipeline("DrawIndexed");
        using var scope = Profiler.BeginScope("D3D12.Draw");
        if (!_hasIndexBuffer)
            throw new RhiException(ErrorCode.ValidationFailure, "Indexed draw requires an index buffer.");
        if (indexCount == 0 || instanceCount == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "DrawIndexed index count and instance count must be greater than zero.");
        _owner.SetPrimitiveTopology(D3D12Mappings.ToPrimitiveTopology(_pipeline!.Topology, _pipeline.GraphicsDesc?.PatchControlPoints ?? 0));
        _owner.List.DrawIndexedInstanced(indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
    }

    public void DrawIndirect(in IndirectDrawDesc desc)
    {
        ThrowIfEnded();
        RequireGraphicsPipeline("DrawIndirect");
        using var scope = Profiler.BeginScope("D3D12.DrawIndirect");
        var arguments = ValidateIndirectDraw(desc.Arguments, desc.ArgumentOffset, desc.DrawCount, desc.StrideInBytes, desc.CountBuffer, desc.CountBufferOffset, indexed: false, out var countBuffer);
        _owner.SetPrimitiveTopology(D3D12Mappings.ToPrimitiveTopology(_pipeline!.Topology, _pipeline.GraphicsDesc?.PatchControlPoints ?? 0));
        _owner.List.ExecuteIndirect(
            _device.DrawIndirectSignature,
            desc.DrawCount,
            arguments.Resource,
            desc.ArgumentOffset,
            countBuffer?.Resource,
            desc.CountBufferOffset);
    }

    public void DrawIndexedIndirect(in DrawIdxDesc desc)
    {
        ThrowIfEnded();
        RequireGraphicsPipeline("DrawIndexedIndirect");
        using var scope = Profiler.BeginScope("D3D12.DrawIndirect");
        if (!_hasIndexBuffer)
            throw new RhiException(ErrorCode.ValidationFailure, "Indexed indirect draw requires an index buffer.");
        var arguments = ValidateIndirectDraw(desc.Arguments, desc.ArgumentOffset, desc.DrawCount, desc.StrideInBytes, desc.CountBuffer, desc.CountBufferOffset, indexed: true, out var countBuffer);
        _owner.SetPrimitiveTopology(D3D12Mappings.ToPrimitiveTopology(_pipeline!.Topology, _pipeline.GraphicsDesc?.PatchControlPoints ?? 0));
        _owner.List.ExecuteIndirect(
            _device.DrawIndexedIndirectSignature,
            desc.DrawCount,
            arguments.Resource,
            desc.ArgumentOffset,
            countBuffer?.Resource,
            desc.CountBufferOffset);
    }

    public void DispatchMesh(uint groupCountX, uint groupCountY, uint groupCountZ)
    {
        ThrowIfEnded();
        RequireMeshPipeline("DispatchMesh");
        using var scope = Profiler.BeginScope("D3D12.DispatchMesh");
        if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "DispatchMesh group counts must all be greater than zero.");
        var list6 = _owner.RequireList6("Mesh shader dispatch");
        list6.DispatchMesh(groupCountX, groupCountY, groupCountZ);
    }

    public void DispatchMeshIndirect(in IndirectDispatchDesc desc)
    {
        ThrowIfEnded();
        RequireMeshPipeline("DispatchMeshIndirect");
        using var scope = Profiler.BeginScope("D3D12.DispatchMeshIndirect");
        var arguments = ValidateIndirectDispatch(desc, out var countBuffer);
        _owner.List.ExecuteIndirect(
            _device.MeshDispatchIndirectSignature,
            desc.DispatchCount,
            arguments.Resource,
            desc.ArgumentOffset,
            countBuffer?.Resource,
            desc.CountBufferOffset);
    }

    public void End()
    {
        ThrowIfEnded();
        ResolveAttachments();
        _vertexBufferBindings.Dispose();
        _ended = true;
        _owner.EndPass();
    }

    private void ResolveAttachments()
    {
        for (int index = 0; index < _resolveCount; index++)
        {
            var resolve = _resolves[index];
            var source = _device.Textures.Get(resolve.Source, "ResolveSource");
            var destination = _device.Textures.Get(resolve.Destination, "ResolveDestination");
            uint sourceSubresource = source.SubresourceIndex(resolve.SourceView.FirstMip, resolve.SourceView.FirstSlice);
            uint destinationSubresource = destination.SubresourceIndex(resolve.DestinationView.FirstMip, resolve.DestinationView.FirstSlice);
            _owner.List.ResourceBarrierTransition(source.Resource, ResourceStates.RenderTarget, ResourceStates.ResolveSource, sourceSubresource, ResourceBarrierFlags.None);
            _owner.List.ResourceBarrierTransition(destination.Resource, ResourceStates.RenderTarget, ResourceStates.ResolveDest, destinationSubresource, ResourceBarrierFlags.None);
            _owner.List.ResolveSubresource(destination.Resource, destinationSubresource, source.Resource, sourceSubresource, resolve.Format);
            _owner.List.ResourceBarrierTransition(source.Resource, ResourceStates.ResolveSource, ResourceStates.RenderTarget, sourceSubresource, ResourceBarrierFlags.None);
            _owner.List.ResourceBarrierTransition(destination.Resource, ResourceStates.ResolveDest, ResourceStates.RenderTarget, destinationSubresource, ResourceBarrierFlags.None);
        }
    }

    private PushRoot FindPushRoot(ShaderStageFlags stages, uint offset, int length)
    {
        if (length == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Push constant data must not be empty.");
        if ((offset & 3) != 0 || (length & 3) != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 push constant offset and size must be 4-byte aligned.");
        foreach (var binding in _layout!.PushConstants)
        {
            if ((binding.Stages & stages) == stages && offset >= binding.Offset && (uint)length <= binding.SizeInBytes - (offset - binding.Offset))
                return binding;
        }

        throw new RhiException(ErrorCode.InvalidDescriptor, "Push constant write is outside the pipeline layout ranges.");
    }

    private void RebindVertexBuffers()
    {
        if (_pipeline?.Kind != PipelineKind.Graphics || _vertexBufferBindings.Count == 0)
            return;

        for (int slot = 0; slot < _vertexBufferBindings.SlotCount; slot++)
        {
            if (!_vertexBufferBindings.TryPairSlot(slot, out var binding))
                continue;

            var record = _device.Buffers.Get(binding.Value.Buffer, "VertexBuffer");
            _owner.RequireBufferState(binding.Value.Buffer, ResourceState.VertexBuffer, "Vertex buffer");
            BindVertexBuffer(binding.Key, record, binding.Value.Offset);
        }
    }

    private void BindVertexBuffer(uint slot, BufferRecord record, ulong offset)
    {
        uint strideInBytes = VertexStride(slot);
        _owner.List.IASetVertexBuffers(
            slot,
            new VertexBufferView(
                record.Resource.GPUVirtualAddress + offset,
                checked((uint)(record.Desc.SizeInBytes - offset)),
                strideInBytes));
    }

    private uint VertexStride(uint slot)
    {
        var graphicsDesc = _pipeline?.GraphicsDesc
            ?? throw new RhiException(ErrorCode.ValidationFailure, "Vertex buffer binding requires a graphics pipeline.");
        for (int index = 0; index < graphicsDesc.VertexBuffers.Count; index++)
        {
            var layout = graphicsDesc.VertexBuffers[index];
            if (layout.Slot == slot)
                return layout.StrideInBytes;
        }

        throw new RhiException(ErrorCode.InvalidDescriptor, $"Vertex buffer slot {slot} is not declared by the current graphics pipeline.");
    }

    private readonly record struct VertexBufferBinding(BufferHandle Buffer, ulong Offset);

    private void RequirePipeline()
    {
        if (_pipeline == null || _layout == null)
            throw new RhiException(ErrorCode.ValidationFailure, "Pipeline must be set before this render pass command.");
    }

    private void RequireGraphicsPipeline(string command)
    {
        RequirePipeline();
        if (_pipeline!.Kind != PipelineKind.Graphics)
            throw new RhiException(ErrorCode.ValidationFailure, $"{command} requires a graphics pipeline.");
    }

    private void RequireMeshPipeline(string command)
    {
        RequirePipeline();
        if (_pipeline!.Kind != PipelineKind.Mesh)
            throw new RhiException(ErrorCode.ValidationFailure, $"{command} requires a mesh pipeline.");
    }

    private BufferRecord ValidateIndirectDraw(
        BufferHandle arguments,
        ulong argumentOffset,
        uint count,
        uint strideInBytes,
        BufferHandle countBuffer,
        ulong countBufferOffset,
        bool indexed,
        out BufferRecord? countBufferRecord)
    {
        var validation = RhiCommandValidation.ValidateIndirectDraw(
            _device.Features,
            _device.Limits,
            indexed,
            count,
            strideInBytes,
            argumentOffset,
            countBuffer.IsValid,
            countBufferOffset);
        var argumentRecord = _device.Buffers.Get(arguments, "IndirectArguments");
        RhiCommandValidation.ValidateArgsBuffer(argumentRecord.Desc, argumentOffset, validation.ArgumentByteCount, "Indirect draw argument");
        _owner.RequireBufferState(arguments, ResourceState.IndirectArgument, "Indirect draw arguments");

        countBufferRecord = null;
        if (countBuffer.IsValid)
        {
            countBufferRecord = _device.Buffers.Get(countBuffer, "IndirectCountBuffer");
            RhiCommandValidation.ValidateCountBuffer(countBufferRecord.Desc, countBufferOffset);
            _owner.RequireBufferState(countBuffer, ResourceState.IndirectArgument, "Indirect draw count");
        }

        return argumentRecord;
    }

    private BufferRecord ValidateIndirectDispatch(in IndirectDispatchDesc desc, out BufferRecord? countBufferRecord)
    {
        var validation = RhiCommandValidation.ValidateIndirectDispatch(_device.Features, _device.Limits, desc);
        var argumentRecord = _device.Buffers.Get(desc.Arguments, "IndirectArguments");
        RhiCommandValidation.ValidateArgsBuffer(argumentRecord.Desc, desc.ArgumentOffset, validation.ArgumentByteCount, "Indirect mesh dispatch argument");
        _owner.RequireBufferState(desc.Arguments, ResourceState.IndirectArgument, "Indirect mesh dispatch arguments");

        countBufferRecord = null;
        if (desc.CountBuffer.IsValid)
        {
            countBufferRecord = _device.Buffers.Get(desc.CountBuffer, "IndirectCountBuffer");
            RhiCommandValidation.ValidateCountBuffer(countBufferRecord.Desc, desc.CountBufferOffset);
            _owner.RequireBufferState(desc.CountBuffer, ResourceState.IndirectArgument, "Indirect mesh dispatch count");
        }

        return argumentRecord;
    }

    private static void SetColorFormat(
        ref Format color0,
        ref Format color1,
        ref Format color2,
        ref Format color3,
        ref Format color4,
        ref Format color5,
        ref Format color6,
        ref Format color7,
        int index,
        Format format)
    {
        switch (index)
        {
            case 0:
                color0 = format;
                break;
            case 1:
                color1 = format;
                break;
            case 2:
                color2 = format;
                break;
            case 3:
                color3 = format;
                break;
            case 4:
                color4 = format;
                break;
            case 5:
                color5 = format;
                break;
            case 6:
                color6 = format;
                break;
            case 7:
                color7 = format;
                break;
            default:
                throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 backend supports at most 8 color attachments.");
        }
    }

    private static uint ValidatePassSamples(uint? current, uint next)
    {
        if (current.HasValue && current.Value != next)
            throw new RhiException(ErrorCode.InvalidDescriptor, "All render pass attachments must have the same sample count.");
        return next;
    }

    private static void ValidateRenderArea(TextureDesc texture, TextureViewDesc view, Rect area)
    {
        uint width = Math.Max(1u, texture.Width >> checked((int)view.FirstMip));
        uint height = Math.Max(1u, texture.Height >> checked((int)view.FirstMip));
        if (area.X < 0 || area.Y < 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass area origin must be non-negative.");
        if ((uint)area.X > width || (uint)area.Width > width - (uint)area.X)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass area exceeds attachment width.");
        if ((uint)area.Y > height || (uint)area.Height > height - (uint)area.Y)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass area exceeds attachment height.");
    }

    private static void ValidateResolveArea(TextureDesc texture, TextureViewDesc view, Rect area, string label)
    {
        uint width = Math.Max(1u, texture.Width >> checked((int)view.FirstMip));
        uint height = Math.Max(1u, texture.Height >> checked((int)view.FirstMip));
        if (area.X != 0 || area.Y != 0 || area.Width < 0 || area.Height < 0 || (uint)area.Width != width || (uint)area.Height != height)
            throw new RhiException(ErrorCode.UnsupportedFeature, $"{label} requires a full-subresource render area on D3D12.");
    }

    private static void ValidateResolveCompat(TextureDesc source, TextureViewDesc sourceView, TextureDesc destination, TextureViewDesc destinationView)
    {
        if (source.Dimension != destination.Dimension)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve requires matching source and target dimensions.");
        uint sourceWidth = Math.Max(1u, source.Width >> checked((int)sourceView.FirstMip));
        uint sourceHeight = Math.Max(1u, source.Height >> checked((int)sourceView.FirstMip));
        uint sourceDepth = source.Dimension == ResourceDimension.Texture3D ? Math.Max(1u, source.Depth >> checked((int)sourceView.FirstMip)) : 1u;
        uint destinationWidth = Math.Max(1u, destination.Width >> checked((int)destinationView.FirstMip));
        uint destinationHeight = Math.Max(1u, destination.Height >> checked((int)destinationView.FirstMip));
        uint destinationDepth = destination.Dimension == ResourceDimension.Texture3D ? Math.Max(1u, destination.Depth >> checked((int)destinationView.FirstMip)) : 1u;
        if (sourceWidth != destinationWidth || sourceHeight != destinationHeight || sourceDepth != destinationDepth)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve source and target extents must match.");
    }

    private static void ValidatePassCompatibility(PipelineRecord pipeline, PassCompatibility compatibility)
    {
        var pipelineCompatibility = pipeline.RenderPassCompatibility;
        if (pipelineCompatibility.SampleCount != compatibility.SampleCount)
            throw new RhiException(ErrorCode.ValidationFailure, $"Pipeline sample count {pipelineCompatibility.SampleCount} does not match render pass sample count {compatibility.SampleCount}.");
        if (pipelineCompatibility.ColorCount != compatibility.ColorCount)
            throw new RhiException(ErrorCode.ValidationFailure, $"Pipeline color target count {pipelineCompatibility.ColorCount} does not match render pass color target count {compatibility.ColorCount}.");
        for (int index = 0; index < compatibility.ColorCount; index++)
        {
            var requiredFormat = compatibility.ColorFormatAt(index);
            var pipelineFormat = pipelineCompatibility.ColorFormatAt(index);
            if (pipelineFormat != requiredFormat)
                throw new RhiException(ErrorCode.ValidationFailure, $"Pipeline color format at index {index} is {pipelineFormat}, render pass requires {requiredFormat}.");
        }
        if (pipelineCompatibility.DepthStencilFormat != compatibility.DepthStencilFormat)
            throw new RhiException(ErrorCode.ValidationFailure, $"Pipeline depth/stencil format {pipelineCompatibility.DepthStencilFormat} does not match render pass format {compatibility.DepthStencilFormat}.");
        if (compatibility.DepthReadOnly && pipelineCompatibility.DepthWriteEnable)
            throw new RhiException(ErrorCode.ValidationFailure, "A depth-writing pipeline cannot be bound to a depth-read-only render pass.");
    }

    private void ThrowIfEnded()
    {
        if (_ended)
            throw new RhiException(ErrorCode.ValidationFailure, "Render pass is already ended.");
        _owner.CheckPassRecord();
    }
}

internal sealed class ComputePass : IComputePass
{
    private readonly D3D12CommandList _owner;
    private readonly D3D12Device _device;
    private readonly DescriptorBinder _bindings;
    private PipelineRecord? _pipeline;
    private PipeLayoutRecord? _layout;
    private PipelineHandle _pipelineHandle;
    private bool _ended;

    public ComputePass(D3D12CommandList owner, D3D12Device device, ComputePassDesc desc)
    {
        _owner = owner;
        _device = device;
        _bindings = new DescriptorBinder(owner, device, graphics: false);
        _ = desc;
    }

    public void SetPipeline(PipelineHandle pipeline)
    {
        ThrowIfEnded();
        if (_pipeline != null && pipeline == _pipelineHandle)
        {
            return;
        }

        using var scope = Profiler.BeginScope("D3D12.SetPipeline");
        var record = _device.Pipelines.Get(pipeline, "Pipeline");
        if (record.Kind != PipelineKind.Compute)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Compute pass requires a compute pipeline.");
        _pipeline = record;
        _layout = record.LayoutRecord;
        _pipelineHandle = pipeline;
        _owner.TrackPipeline(pipeline);
        _owner.TrackPipelineLayout(record.Layout);
        _owner.SetComputeState(record.State!);
        _owner.SetComputeRoot(_layout.RootSignature);
    }

    public void SetBindingSet(uint setIndex, BindingSetHandle bindingSet, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)
    {
        ThrowIfEnded();
        RequirePipeline();
        _bindings.SetBindingSet(_layout!, setIndex, bindingSet, dynamicOffsets);
    }

    public void SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)
    {
        ThrowIfEnded();
        RequirePipeline();
        _bindings.SetBindings(_layout!, setIndex, layout, resources, dynamicOffsets);
    }

    public void Barrier(
        ReadOnlySpan<TextureBarrier> textureBarriers,
        ReadOnlySpan<BufferBarrier> bufferBarriers,
        ReadOnlySpan<AliasingBarrier> aliasingBarriers = default)
    {
        ThrowIfEnded();
        _owner.CheckOpenPass(textureBarriers, bufferBarriers, aliasingBarriers);
    }

    public void SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data)
    {
        ThrowIfEnded();
        RequirePipeline();
        var binding = FindPushRoot(stages, offset, data.Length);
        _owner.List.SetComputeRoot32BitConstants(checked((uint)binding.RootParameter), MemoryMarshal.Cast<byte, uint>(data), checked((uint)((offset - binding.Offset) / 4)));
    }

    public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
    {
        ThrowIfEnded();
        RequirePipeline();
        using var scope = Profiler.BeginScope("D3D12.Dispatch");
        if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Dispatch group counts must all be greater than zero.");
        _owner.List.Dispatch(groupCountX, groupCountY, groupCountZ);
    }

    public void DispatchIndirect(in IndirectDispatchDesc desc)
    {
        ThrowIfEnded();
        RequirePipeline();
        using var scope = Profiler.BeginScope("D3D12.DispatchIndirect");
        var arguments = ValidateIndirectDispatch(desc, out var countBuffer);
        _owner.List.ExecuteIndirect(
            _device.DispatchIndirectSignature,
            desc.DispatchCount,
            arguments.Resource,
            desc.ArgumentOffset,
            countBuffer?.Resource,
            desc.CountBufferOffset);
    }

    public void End()
    {
        ThrowIfEnded();
        _ended = true;
        _owner.EndPass();
    }

    private PushRoot FindPushRoot(ShaderStageFlags stages, uint offset, int length)
    {
        if (length == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Push constant data must not be empty.");
        if ((offset & 3) != 0 || (length & 3) != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 push constant offset and size must be 4-byte aligned.");
        foreach (var binding in _layout!.PushConstants)
        {
            if ((binding.Stages & stages) == stages && offset >= binding.Offset && (uint)length <= binding.SizeInBytes - (offset - binding.Offset))
                return binding;
        }

        throw new RhiException(ErrorCode.InvalidDescriptor, "Push constant write is outside the pipeline layout ranges.");
    }

    private void RequirePipeline()
    {
        if (_pipeline == null || _layout == null)
            throw new RhiException(ErrorCode.ValidationFailure, "Pipeline must be set before this compute pass command.");
    }

    private BufferRecord ValidateIndirectDispatch(in IndirectDispatchDesc desc, out BufferRecord? countBufferRecord)
    {
        var validation = RhiCommandValidation.ValidateIndirectDispatch(_device.Features, _device.Limits, desc);
        var argumentRecord = _device.Buffers.Get(desc.Arguments, "IndirectArguments");
        RhiCommandValidation.ValidateArgsBuffer(argumentRecord.Desc, desc.ArgumentOffset, validation.ArgumentByteCount, "Indirect dispatch argument");
        _owner.RequireBufferState(desc.Arguments, ResourceState.IndirectArgument, "Indirect dispatch arguments");

        countBufferRecord = null;
        if (desc.CountBuffer.IsValid)
        {
            countBufferRecord = _device.Buffers.Get(desc.CountBuffer, "IndirectCountBuffer");
            RhiCommandValidation.ValidateCountBuffer(countBufferRecord.Desc, desc.CountBufferOffset);
            _owner.RequireBufferState(desc.CountBuffer, ResourceState.IndirectArgument, "Indirect dispatch count");
        }

        return argumentRecord;
    }

    private void ThrowIfEnded()
    {
        if (_ended)
            throw new RhiException(ErrorCode.ValidationFailure, "Compute pass is already ended.");
        _owner.CheckPassRecord();
    }

}

internal sealed class RtPass : IRtPass
{
    private readonly D3D12CommandList _owner;
    private readonly D3D12Device _device;
    private readonly DescriptorBinder _bindings;
    private PipelineRecord? _pipeline;
    private PipeLayoutRecord? _layout;
    private PipelineHandle _pipelineHandle;
    private bool _ended;

    public RtPass(D3D12CommandList owner, D3D12Device device)
    {
        _owner = owner;
        _device = device;
        _bindings = new DescriptorBinder(owner, device, graphics: false);
    }

    public void SetPipeline(PipelineHandle pipeline)
    {
        ThrowIfEnded();
        if (_pipeline != null && pipeline == _pipelineHandle)
        {
            return;
        }

        using var scope = Profiler.BeginScope("D3D12.SetPipeline");
        var record = _device.Pipelines.Get(pipeline, "Pipeline");
        if (record.Kind != PipelineKind.RayTracing || record.StateObject == null)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Ray tracing pass requires a ray tracing pipeline.");
        _pipeline = record;
        _layout = record.LayoutRecord;
        _pipelineHandle = pipeline;
        _owner.TrackPipeline(pipeline);
        _owner.TrackPipelineLayout(record.Layout);
        var list4 = _owner.RequireList4("Ray tracing pass");
        _owner.SetRayState(list4, record.StateObject);
        _owner.SetComputeRoot(_layout.RootSignature);
    }

    public void SetBindingSet(uint setIndex, BindingSetHandle bindingSet, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)
    {
        ThrowIfEnded();
        RequirePipeline();
        _bindings.SetBindingSet(_layout!, setIndex, bindingSet, dynamicOffsets);
    }

    public void SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)
    {
        ThrowIfEnded();
        RequirePipeline();
        _bindings.SetBindings(_layout!, setIndex, layout, resources, dynamicOffsets);
    }

    public void SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data)
    {
        ThrowIfEnded();
        RequirePipeline();
        if (stages == ShaderStageFlags.None || (stages & ~ShaderStageFlags.AllRayTracing) != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Ray tracing pass push constants must target ray tracing shader stages.");
        var binding = FindPushRoot(stages, offset, data.Length);
        _owner.List.SetComputeRoot32BitConstants(checked((uint)binding.RootParameter), MemoryMarshal.Cast<byte, uint>(data), checked((uint)((offset - binding.Offset) / 4)));
    }

    public void TraceRays(in ShaderTableDesc shaderBindingTable, uint width, uint height, uint depth)
    {
        ThrowIfEnded();
        RequirePipeline();
        using var scope = Profiler.BeginScope("D3D12.TraceRays");
        if (width == 0 || height == 0 || depth == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "TraceRays dimensions must all be greater than zero.");
        var rayGeneration = ValidateSbtRegion(shaderBindingTable.RayGeneration, singleRecord: true, required: true, "ray-generation shader record");
        var miss = ValidateSbtRegion(shaderBindingTable.Miss, singleRecord: false, required: false, "miss shader table");
        var hitGroup = ValidateSbtRegion(shaderBindingTable.HitGroup, singleRecord: false, required: false, "hit-group shader table");
        var callable = ValidateSbtRegion(shaderBindingTable.Callable, singleRecord: false, required: false, "callable shader table");

        var list4 = _owner.RequireList4("TraceRays");
        list4.DispatchRays(new DispatchRaysDescription
        {
            RayGenerationShaderRecord = new GpuVirtualAddressRange(rayGeneration.StartAddress, rayGeneration.SizeInBytes),
            MissShaderTable = miss,
            HitGroupTable = hitGroup,
            CallableShaderTable = callable,
            Width = width,
            Height = height,
            Depth = depth,
        });
    }

    public void End()
    {
        ThrowIfEnded();
        _ended = true;
        _owner.EndPass();
    }

    private PushRoot FindPushRoot(ShaderStageFlags stages, uint offset, int length)
    {
        if (length == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Push constant data must not be empty.");
        if ((offset & 3) != 0 || (length & 3) != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 push constant offset and size must be 4-byte aligned.");
        foreach (var binding in _layout!.PushConstants)
        {
            if ((binding.Stages & stages) == stages && offset >= binding.Offset && (uint)length <= binding.SizeInBytes - (offset - binding.Offset))
                return binding;
        }

        throw new RhiException(ErrorCode.InvalidDescriptor, "Push constant write is outside the pipeline layout ranges.");
    }

    private GpuVirtualAddressRangeAndStride ValidateSbtRegion(ShaderTableRegion region, bool singleRecord, bool required, string label)
    {
        if (!region.Buffer.IsValid)
        {
            if (required)
                throw new RhiException(ErrorCode.InvalidHandle, $"{label} requires a valid buffer.");
            if (region.Offset != 0 || region.SizeInBytes != 0 || region.StrideInBytes != 0)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Empty {label} must have zero offset, size, and stride.");
            return default;
        }

        var buffer = _device.Buffers.Get(region.Buffer, label);
        if (!buffer.Desc.BindFlags.HasFlag(BindFlags.ShaderResource))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} buffer requires ShaderResource bind flag.");
        if (region.Offset % _device.Limits.RayTracingShaderTableAlignment != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} offset must be aligned to {_device.Limits.RayTracingShaderTableAlignment} bytes.");
        if (region.SizeInBytes == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} size must be greater than zero.");
        if (singleRecord)
        {
            if (region.StrideInBytes != 0 && region.StrideInBytes != region.SizeInBytes)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} stride must be zero or equal to size.");
        }
        else if (region.StrideInBytes == 0
            || region.StrideInBytes % _device.Limits.RayTracingShaderRecordAlignment != 0
            || region.StrideInBytes > _device.Limits.MaxRayTracingShaderRecordStride)
        {
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} stride must be non-zero, aligned, and within device limits.");
        }

        RhiCommandValidation.ValidateBufferRange(buffer.Desc, region.Offset, region.SizeInBytes, label);
        _owner.RequireBufferState(region.Buffer, ResourceState.ShaderResource, label);
        _owner.TrackBuffer(region.Buffer);
        return new GpuVirtualAddressRangeAndStride(buffer.Resource.GPUVirtualAddress + region.Offset, region.SizeInBytes, singleRecord ? 0 : region.StrideInBytes);
    }

    private void RequirePipeline()
    {
        if (_pipeline == null || _layout == null)
            throw new RhiException(ErrorCode.ValidationFailure, "Pipeline must be set before this ray tracing pass command.");
    }

    private void ThrowIfEnded()
    {
        if (_ended)
            throw new RhiException(ErrorCode.ValidationFailure, "Ray tracing pass is already ended.");
        _owner.CheckPassRecord();
    }
}
