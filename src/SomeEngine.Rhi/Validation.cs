namespace SomeEngine.Rhi;

public readonly record struct ResourceStateValidationResult(
    bool IsValid,
    BindFlags RequiredBindFlags,
    bool AcceptsAnyBindFlag,
    string? InvalidReason);

public static class ResourceStateValidation
{
    private const BindFlags GenericReadBindFlags =
        BindFlags.VertexBuffer
        | BindFlags.IndexBuffer
        | BindFlags.ConstantBuffer
        | BindFlags.ShaderResource
        | BindFlags.IndirectArgument
        | BindFlags.CopySource;

    public static ResourceStateValidationResult BufferRequirement(ResourceState state)
    {
        if (!Enum.IsDefined(state))
            return Invalid($"value {state} is not defined");

        return state switch
        {
            ResourceState.Undefined => Valid(),
            ResourceState.Common => Valid(),
            ResourceState.GenericRead => RequireAny(GenericReadBindFlags),
            ResourceState.VertexBuffer => RequireAll(BindFlags.VertexBuffer),
            ResourceState.IndexBuffer => RequireAll(BindFlags.IndexBuffer),
            ResourceState.ConstantBuffer => RequireAll(BindFlags.ConstantBuffer),
            ResourceState.ShaderResource => RequireAll(BindFlags.ShaderResource),
            ResourceState.UnorderedAccess => RequireAll(BindFlags.UnorderedAccess),
            ResourceState.CopySource => RequireAll(BindFlags.CopySource),
            ResourceState.CopyDestination => RequireAll(BindFlags.CopyDestination),
            ResourceState.IndirectArgument => RequireAll(BindFlags.IndirectArgument),
            ResourceState.QueryResolve => RequireAll(BindFlags.CopyDestination),
            _ => Invalid($"{state} is not valid for buffers"),
        };
    }

    public static ResourceStateValidationResult TextureRequirement(ResourceState state, bool allowPresent = false)
    {
        if (!Enum.IsDefined(state))
            return Invalid($"value {state} is not defined");

        return state switch
        {
            ResourceState.Undefined => Valid(),
            ResourceState.Common => Valid(),
            ResourceState.Present when allowPresent => Valid(),
            ResourceState.Present => Invalid("Present is only valid for swapchain textures"),
            ResourceState.GenericRead => Invalid("GenericRead is not valid for textures"),
            ResourceState.ShaderResource => RequireAll(BindFlags.ShaderResource),
            ResourceState.UnorderedAccess => RequireAll(BindFlags.UnorderedAccess),
            ResourceState.RenderTarget => RequireAll(BindFlags.RenderTarget),
            ResourceState.DepthRead => RequireAll(BindFlags.DepthStencil),
            ResourceState.DepthWrite => RequireAll(BindFlags.DepthStencil),
            ResourceState.CopySource => RequireAll(BindFlags.CopySource),
            ResourceState.CopyDestination => RequireAll(BindFlags.CopyDestination),
            ResourceState.ResolveSource => RequireAll(BindFlags.RenderTarget),
            ResourceState.ResolveDestination => RequireAll(BindFlags.RenderTarget),
            _ => Invalid($"{state} is not valid for textures"),
        };
    }

    private static ResourceStateValidationResult Valid()
        => new(true, BindFlags.None, false, null);

    private static ResourceStateValidationResult RequireAll(BindFlags flags)
        => new(true, flags, false, null);

    private static ResourceStateValidationResult RequireAny(BindFlags flags)
        => new(true, flags, true, null);

    private static ResourceStateValidationResult Invalid(string reason)
        => new(false, BindFlags.None, false, reason);
}

internal static class Validation
{
    private const BindFlags KnownBindFlags =
        BindFlags.VertexBuffer
        | BindFlags.IndexBuffer
        | BindFlags.ConstantBuffer
        | BindFlags.ShaderResource
        | BindFlags.UnorderedAccess
        | BindFlags.RenderTarget
        | BindFlags.DepthStencil
        | BindFlags.IndirectArgument
        | BindFlags.CopySource
        | BindFlags.CopyDestination;

    private const ShaderStageFlags KnownShaderStages = ShaderStageFlags.All;

    private const BindingFlags KnownBindingFlags =
        BindingFlags.PartiallyBound
        | BindingFlags.Bindless
        | BindingFlags.DynamicOffset;

    private const BindingSetFlags KnownBindingSetFlags = BindingSetFlags.Mutable;

    public static void BufferDesc(BufferDesc desc)
    {
        if (!Enum.IsDefined(desc.Memory))
            Throw(ErrorCode.InvalidDescriptor, $"Buffer memory class value {desc.Memory} is not defined.");
        if ((desc.BindFlags & ~KnownBindFlags) != 0)
            Throw(ErrorCode.InvalidDescriptor, $"Buffer bind flags contain unsupported bits: {desc.BindFlags}.");
        if (!Enum.IsDefined(desc.InitialState))
            Throw(ErrorCode.InvalidDescriptor, $"Buffer initial state value {desc.InitialState} is not defined.");
        if (desc.SizeInBytes == 0)
            Throw(ErrorCode.InvalidDescriptor, "Buffer size must be greater than zero.");
        if (desc.Raw && desc.StrideInBytes != 0)
            Throw(ErrorCode.InvalidDescriptor, "Raw buffers must not specify a structured stride.");
        if (!desc.Raw && desc.StrideInBytes > 0 && desc.SizeInBytes % desc.StrideInBytes != 0)
            Throw(ErrorCode.InvalidDescriptor, $"Buffer size {desc.SizeInBytes} must be a multiple of stride {desc.StrideInBytes}.");
        if (desc.Memory == MemoryClass.CpuUpload && desc.BindFlags.HasFlag(BindFlags.RenderTarget))
            Throw(ErrorCode.InvalidDescriptor, "CPU upload buffers cannot be render targets.");
        BufferResourceState(desc, desc.InitialState, "Buffer initial state");
    }

    public static void TextureDesc(TextureDesc desc)
    {
        if (!Enum.IsDefined(desc.Memory))
            Throw(ErrorCode.InvalidDescriptor, $"Texture memory class value {desc.Memory} is not defined.");
        if ((desc.BindFlags & ~KnownBindFlags) != 0)
            Throw(ErrorCode.InvalidDescriptor, $"Texture bind flags contain unsupported bits: {desc.BindFlags}.");
        if (!Enum.IsDefined(desc.InitialState))
            Throw(ErrorCode.InvalidDescriptor, $"Texture initial state value {desc.InitialState} is not defined.");
        if (!Enum.IsDefined(desc.Format))
            Throw(ErrorCode.InvalidDescriptor, $"Texture format value {desc.Format} is not defined.");
        if (desc.Width == 0 || desc.Height == 0 || desc.Depth == 0)
            Throw(ErrorCode.InvalidDescriptor, "Texture dimensions must be greater than zero.");
        if (desc.MipLevels == 0)
            Throw(ErrorCode.InvalidDescriptor, "Texture mip count must be greater than zero.");
        if (desc.ArraySize == 0)
            Throw(ErrorCode.InvalidDescriptor, "Texture array size must be greater than zero.");
        if (desc.SampleCount == 0)
            Throw(ErrorCode.InvalidDescriptor, "Texture sample count must be greater than zero.");
        if (desc.Format == Format.Unknown)
            Throw(ErrorCode.InvalidDescriptor, "Texture format must not be Unknown.");
        if (desc.BindFlags.HasFlag(BindFlags.RenderTarget) && desc.BindFlags.HasFlag(BindFlags.DepthStencil))
            Throw(ErrorCode.InvalidDescriptor, "A texture cannot be both render target and depth/stencil.");
        if (desc.BindFlags.HasFlag(BindFlags.DepthStencil) && !IsDepthFormat(desc.Format))
            Throw(ErrorCode.InvalidDescriptor, $"Depth/stencil bind flag requires a depth format, got {desc.Format}.");
        switch (desc.Dimension)
        {
            case ResourceDimension.Texture1D:
                if (desc.Height != 1 || desc.Depth != 1)
                    Throw(ErrorCode.InvalidDescriptor, "Texture1D requires Height=1 and Depth=1.");
                break;
            case ResourceDimension.Texture2D:
                if (desc.Depth != 1)
                    Throw(ErrorCode.InvalidDescriptor, "Texture2D requires Depth=1.");
                break;
            case ResourceDimension.Texture3D:
                if (desc.ArraySize != 1)
                    Throw(ErrorCode.InvalidDescriptor, "Texture3D requires ArraySize=1.");
                break;
            case ResourceDimension.TextureCube:
                if (desc.Width != desc.Height || desc.Depth != 1 || desc.ArraySize % 6 != 0)
                    Throw(ErrorCode.InvalidDescriptor, "TextureCube requires square faces, Depth=1, and ArraySize divisible by 6.");
                break;
            default:
                Throw(ErrorCode.InvalidDescriptor, $"Unsupported texture dimension {desc.Dimension}.");
                break;
        }
        if (desc.SampleCount > 1 && (desc.Dimension != ResourceDimension.Texture2D || desc.MipLevels != 1))
            Throw(ErrorCode.InvalidDescriptor, "Multisampled textures must be Texture2D with exactly one mip level.");
        uint maxMipLevels = MaxMipLevels(desc);
        if (desc.MipLevels > maxMipLevels)
            Throw(ErrorCode.InvalidDescriptor, $"Texture mip count {desc.MipLevels} exceeds maximum {maxMipLevels} for extent {desc.Width}x{desc.Height}x{desc.Depth}.");
        if ((ulong)desc.MipLevels * desc.ArraySize > int.MaxValue)
            Throw(ErrorCode.InvalidDescriptor, "Texture subresource count exceeds the supported range.");
        TextureResourceState(desc, desc.InitialState, "Texture initial state");
    }

    public static void TextureViewDesc(TextureDesc texture, TextureViewDesc desc)
    {
        if (!Enum.IsDefined(desc.Kind))
            Throw(ErrorCode.InvalidDescriptor, $"Texture view kind value {desc.Kind} is not defined.");
        if (!Enum.IsDefined(desc.Dimension))
            Throw(ErrorCode.InvalidDescriptor, $"Texture view dimension value {desc.Dimension} is not defined.");
        if (desc.Format != Format.Unknown && !Enum.IsDefined(desc.Format))
            Throw(ErrorCode.InvalidDescriptor, $"Texture view format value {desc.Format} is not defined.");
        Format viewFormat = desc.Format == Format.Unknown ? texture.Format : desc.Format;
        if (viewFormat == Format.Unknown)
            Throw(ErrorCode.InvalidDescriptor, "Texture view format must be known.");
        if (viewFormat != texture.Format)
            Throw(ErrorCode.InvalidDescriptor, $"Texture view format {viewFormat} must match texture format {texture.Format} in the initial RHI API.");
        if (desc.MipCount == 0)
            Throw(ErrorCode.InvalidDescriptor, "Texture view mip count must be greater than zero.");
        if (desc.SliceCount == 0)
            Throw(ErrorCode.InvalidDescriptor, "Texture view slice count must be greater than zero.");
        if (desc.FirstMip >= texture.MipLevels || desc.MipCount > texture.MipLevels - desc.FirstMip)
            Throw(ErrorCode.InvalidDescriptor, "Texture view mip range is outside the texture.");
        if (desc.FirstSlice >= texture.ArraySize || desc.SliceCount > texture.ArraySize - desc.FirstSlice)
            Throw(ErrorCode.InvalidDescriptor, "Texture view slice range is outside the texture.");
        if (desc.PlaneSlice != 0)
            Throw(ErrorCode.UnsupportedFeature, "PlaneSlice other than zero is not supported by the current RHI format model.");
        ValidateViewDim(texture, desc);
        RequireTextureBind(texture, desc.Kind);
    }

    public static void BufferViewDesc(BufferDesc buffer, BufferViewDesc desc)
    {
        if (!Enum.IsDefined(desc.Kind))
            Throw(ErrorCode.InvalidDescriptor, $"Buffer view kind value {desc.Kind} is not defined.");
        if (desc.SizeInBytes == 0)
            Throw(ErrorCode.InvalidDescriptor, "Buffer view size must be greater than zero.");
        if (desc.Format != Format.Unknown && !Enum.IsDefined(desc.Format))
            Throw(ErrorCode.InvalidDescriptor, $"Buffer view format value {desc.Format} is not defined.");
        if (desc.Offset >= buffer.SizeInBytes || desc.SizeInBytes > buffer.SizeInBytes - desc.Offset)
            Throw(ErrorCode.InvalidDescriptor, "Buffer view range is outside the buffer.");
        if (desc.Raw && desc.StrideInBytes != 0)
            Throw(ErrorCode.InvalidDescriptor, "Raw buffer views must not specify a structured stride.");
        if (desc.Raw && desc.Format != Format.Unknown)
            Throw(ErrorCode.InvalidDescriptor, "Raw buffer views must not specify a typed format.");
        if (desc.StrideInBytes > 0 && desc.Format != Format.Unknown)
            Throw(ErrorCode.InvalidDescriptor, "Structured buffer views must not specify a typed format.");
        if (!desc.Raw && desc.StrideInBytes > 0 && desc.SizeInBytes % desc.StrideInBytes != 0)
            Throw(ErrorCode.InvalidDescriptor, "Structured buffer view size must be a multiple of stride.");
        RequireBufferBind(buffer, desc.Kind);
    }

    public static void SamplerDesc(SamplerDesc desc)
    {
        if (!Enum.IsDefined(desc.MinFilter))
            Throw(ErrorCode.InvalidDescriptor, $"Sampler min filter value {desc.MinFilter} is not defined.");
        if (!Enum.IsDefined(desc.MagFilter))
            Throw(ErrorCode.InvalidDescriptor, $"Sampler mag filter value {desc.MagFilter} is not defined.");
        if (!Enum.IsDefined(desc.MipmapMode))
            Throw(ErrorCode.InvalidDescriptor, $"Sampler mipmap mode value {desc.MipmapMode} is not defined.");
        if (!Enum.IsDefined(desc.AddressU) || !Enum.IsDefined(desc.AddressV) || !Enum.IsDefined(desc.AddressW))
            Throw(ErrorCode.InvalidDescriptor, "Sampler address mode value is not defined.");
        if (desc.Compare.HasValue && !Enum.IsDefined(desc.Compare.Value))
            Throw(ErrorCode.InvalidDescriptor, $"Sampler compare op value {desc.Compare.Value} is not defined.");
        if (!Enum.IsDefined(desc.BorderColor))
            Throw(ErrorCode.InvalidDescriptor, $"Sampler border color value {desc.BorderColor} is not defined.");
        if (!float.IsFinite(desc.MipLodBias))
            Throw(ErrorCode.InvalidDescriptor, "Sampler mip LOD bias must be finite.");
        if (!float.IsFinite(desc.MinLod) || !float.IsFinite(desc.MaxLod))
            Throw(ErrorCode.InvalidDescriptor, "Sampler LOD range must be finite.");
        if (desc.MinLod > desc.MaxLod)
            Throw(ErrorCode.InvalidDescriptor, "Sampler MinLod must be less than or equal to MaxLod.");
        if (desc.MaxAnisotropy == 0)
            Throw(ErrorCode.InvalidDescriptor, "Sampler MaxAnisotropy must be greater than zero.");
    }

    public static void MemoryHeapDesc(MemoryHeapDesc desc, DeviceFeatures features)
    {
        if (desc.SizeInBytes == 0)
            Throw(ErrorCode.InvalidDescriptor, "Memory heap size must be greater than zero.");
        if (!Enum.IsDefined(desc.Memory))
            Throw(ErrorCode.InvalidDescriptor, $"Memory class value {desc.Memory} is not defined.");
        if (!Enum.IsDefined(desc.Kind))
            Throw(ErrorCode.InvalidDescriptor, $"Memory heap kind value {desc.Kind} is not defined.");
        if (desc.Kind == MemoryHeapKind.Mixed && !features.MixedResourceHeaps)
            Throw(ErrorCode.UnsupportedFeature, "Mixed resource heaps are not supported by this device.");
    }

    public static void ShaderModuleDesc(ShaderModuleDesc desc)
    {
        if (!Enum.IsDefined(desc.Backend))
            Throw(ErrorCode.InvalidDescriptor, $"Shader backend value {desc.Backend} is not defined.");
        if (!Enum.IsDefined(desc.Stage))
            Throw(ErrorCode.InvalidDescriptor, $"Shader stage value {desc.Stage} is not defined.");
        if (!Enum.IsDefined(desc.BytecodeFormat))
            Throw(ErrorCode.InvalidDescriptor, $"Shader bytecode format value {desc.BytecodeFormat} is not defined.");
        if (string.IsNullOrWhiteSpace(desc.EntryPoint))
            Throw(ErrorCode.InvalidDescriptor, "Shader entry point must be specified.");
        if (desc.Bytecode.IsEmpty)
            Throw(ErrorCode.InvalidDescriptor, "Shader bytecode must not be empty.");
    }

    public static void BindingLayoutDesc(BindingLayoutDesc desc, DeviceLimits limits, DeviceFeatures features)
    {
        if ((uint)desc.Slots.Count > limits.MaxBindingsPerSet)
            Throw(ErrorCode.InvalidDescriptor, $"Binding layout has {desc.Slots.Count} slots, exceeding limit {limits.MaxBindingsPerSet}.");

        for (int slotIndex = 0; slotIndex < desc.Slots.Count; slotIndex++)
        {
            var slot = desc.Slots[slotIndex];
            if (!Enum.IsDefined(slot.Type))
                Throw(ErrorCode.InvalidDescriptor, $"Binding slot type value {slot.Type} is not defined.");
            if (slot.Type == BindingType.None)
                Throw(ErrorCode.InvalidDescriptor, "Binding slot type None is only valid for update clear operations.");
            if ((slot.Stages & ~KnownShaderStages) != 0)
                Throw(ErrorCode.InvalidDescriptor, $"Binding slot stages contain unsupported bits: {slot.Stages}.");
            if ((slot.Flags & ~KnownBindingFlags) != 0)
                Throw(ErrorCode.InvalidDescriptor, $"Binding slot flags contain unsupported bits: {slot.Flags}.");
            if (slot.Count == 0)
                Throw(ErrorCode.InvalidDescriptor, "Binding slot descriptor count must be greater than zero.");
            if (slot.Stages == ShaderStageFlags.None)
                Throw(ErrorCode.InvalidDescriptor, "Binding slot must be visible to at least one shader stage.");
            for (int previous = 0; previous < slotIndex; previous++)
            {
                if (desc.Slots[previous].Binding == slot.Binding
                    && RhiBindingValidation.ShareRegisterClass(desc.Slots[previous].Type, slot.Type))
                {
                    Throw(ErrorCode.InvalidDescriptor, $"Duplicate binding slot binding={slot.Binding}.");
                }
            }

            if ((slot.Flags & BindingFlags.Bindless) != 0 && !features.Bindless)
                Throw(ErrorCode.UnsupportedFeature, "Bindless descriptors are not supported by this device.");
            if ((slot.Flags & BindingFlags.PartiallyBound) != 0 && !features.PartiallyBoundDescriptors)
                Throw(ErrorCode.UnsupportedFeature, "Partially bound descriptors are not supported by this device.");
            if ((slot.Flags & BindingFlags.DynamicOffset) != 0 && !features.DynamicOffsets)
                Throw(ErrorCode.UnsupportedFeature, "Dynamic descriptor offsets are not supported by this device.");
            if ((slot.Flags & BindingFlags.DynamicOffset) != 0 && (slot.Flags & BindingFlags.Bindless) != 0)
                Throw(ErrorCode.InvalidDescriptor, "Dynamic offsets cannot be combined with bindless descriptors.");
            if ((slot.Flags & BindingFlags.DynamicOffset) != 0 && (slot.Flags & BindingFlags.PartiallyBound) != 0)
                Throw(ErrorCode.InvalidDescriptor, "Dynamic offsets cannot be combined with partially bound descriptors.");
            if ((slot.Flags & BindingFlags.DynamicOffset) != 0 && !SupportsDynamicOffset(slot.Type))
                Throw(ErrorCode.InvalidDescriptor, $"Dynamic offsets are only valid for buffer bindings, got {slot.Type}.");
            if (slot.Type == BindingType.AccelerationStructure && !features.RayTracing)
                Throw(ErrorCode.UnsupportedFeature, "Acceleration-structure bindings require ray tracing support.");
            ValidateSlotShape(slot);
            uint descriptorLimit = (slot.Flags & BindingFlags.Bindless) != 0
                ? slot.Type == BindingType.Sampler ? limits.MaxBindlessSamplerDescriptors : limits.MaxBindlessResourceDescriptors
                : limits.MaxDescriptorArrayLength;
            if (slot.Count > 1 && slot.Count > descriptorLimit)
                Throw(ErrorCode.InvalidDescriptor, $"Descriptor count {slot.Count} exceeds limit {descriptorLimit}.");
        }
    }

    public static void BindingSetDesc(BindingSetDesc desc)
    {
        if ((desc.Flags & ~KnownBindingSetFlags) != 0)
            Throw(ErrorCode.InvalidDescriptor, $"Binding set flags contain unsupported bits: {desc.Flags}.");
    }

    public static void PipelineLayoutDesc(PipelineLayoutDesc desc, DeviceLimits limits)
    {
        if ((uint)desc.BindingLayouts.Count > limits.MaxBindingSets)
            Throw(ErrorCode.InvalidDescriptor, $"Pipeline layout has {desc.BindingLayouts.Count} sets, exceeding limit {limits.MaxBindingSets}.");
        if ((uint)desc.StaticSamplers.Count > limits.MaxStaticSamplers)
            Throw(ErrorCode.InvalidDescriptor, $"Pipeline layout has {desc.StaticSamplers.Count} static samplers, exceeding limit {limits.MaxStaticSamplers}.");
        foreach (var push in desc.PushConstants)
        {
            if ((push.Stages & ~KnownShaderStages) != 0)
                Throw(ErrorCode.InvalidDescriptor, $"Push constant range stages contain unsupported bits: {push.Stages}.");
            if (push.Stages == ShaderStageFlags.None)
                Throw(ErrorCode.InvalidDescriptor, "Push constant range must be visible to at least one stage.");
            if (push.SizeInBytes == 0)
                Throw(ErrorCode.InvalidDescriptor, "Push constant range size must be greater than zero.");
            if (push.SizeInBytes > limits.MaxPushConstantBytes || push.Offset > limits.MaxPushConstantBytes - push.SizeInBytes)
                Throw(ErrorCode.InvalidDescriptor, $"Push constant range exceeds limit {limits.MaxPushConstantBytes} bytes.");
        }

        for (int samplerIndex = 0; samplerIndex < desc.StaticSamplers.Count; samplerIndex++)
        {
            var sampler = desc.StaticSamplers[samplerIndex];
            if ((sampler.Stages & ~KnownShaderStages) != 0)
                Throw(ErrorCode.InvalidDescriptor, $"Static sampler stages contain unsupported bits: {sampler.Stages}.");
            if (sampler.Stages == ShaderStageFlags.None)
                Throw(ErrorCode.InvalidDescriptor, "Static sampler must be visible to at least one stage.");
            if (sampler.Set >= limits.MaxBindingSets)
                Throw(ErrorCode.InvalidDescriptor, $"Static sampler set {sampler.Set} exceeds binding set limit {limits.MaxBindingSets}.");
            for (int previous = 0; previous < samplerIndex; previous++)
            {
                var previousSampler = desc.StaticSamplers[previous];
                if (previousSampler.Set == sampler.Set && previousSampler.Binding == sampler.Binding)
                    Throw(ErrorCode.InvalidDescriptor, $"Duplicate static sampler set={sampler.Set} binding={sampler.Binding}.");
            }

            SamplerDesc(sampler.Sampler);
        }
    }

    public static void RenderPassDesc(in RenderPassDesc desc, DeviceLimits limits)
    {
        if (desc.ColorAttachments.Length == 0 && desc.DepthStencilAttachment == null)
            Throw(ErrorCode.InvalidDescriptor, "Render pass must have at least one attachment.");
        if ((uint)desc.ColorAttachments.Length > limits.MaxColorAttachments)
            Throw(ErrorCode.InvalidDescriptor, $"Render pass has {desc.ColorAttachments.Length} color attachments, exceeding limit {limits.MaxColorAttachments}.");
        if (desc.RenderArea.Width <= 0 || desc.RenderArea.Height <= 0)
            Throw(ErrorCode.InvalidDescriptor, "Render pass area must be positive.");
        foreach (var attachment in desc.ColorAttachments)
        {
            if (!Enum.IsDefined(attachment.LoadOp) || !Enum.IsDefined(attachment.StoreOp))
                Throw(ErrorCode.InvalidDescriptor, "Color attachment load/store op is not defined.");
        }
        if (desc.DepthStencilAttachment is { } depth
            && (!Enum.IsDefined(depth.DepthLoadOp) || !Enum.IsDefined(depth.DepthStoreOp)))
        {
            Throw(ErrorCode.InvalidDescriptor, "Depth attachment load/store op is not defined.");
        }
    }

    public static void CommandListDesc(CommandListDesc desc)
    {
        if (!Enum.IsDefined(desc.QueueType))
            Throw(ErrorCode.InvalidDescriptor, $"Command list queue type value {desc.QueueType} is not defined.");
    }

    public static void SwapchainDesc(SwapchainDesc desc)
    {
        if (desc.Width == 0 || desc.Height == 0)
            Throw(ErrorCode.InvalidDescriptor, "Swapchain dimensions must be greater than zero.");
        if (!Enum.IsDefined(desc.Format))
            Throw(ErrorCode.InvalidDescriptor, $"Swapchain format value {desc.Format} is not defined.");
        if (desc.Format == Format.Unknown || IsDepthFormat(desc.Format))
            Throw(ErrorCode.InvalidDescriptor, $"Swapchain format must be a color format, got {desc.Format}.");
        if (desc.BufferCount < 2)
            Throw(ErrorCode.InvalidDescriptor, "Swapchain buffer count must be at least 2.");
        if (!Enum.IsDefined(desc.ColorSpace))
            Throw(ErrorCode.InvalidDescriptor, $"Color space value {desc.ColorSpace} is not defined.");
        if (!Enum.IsDefined(desc.Mode))
            Throw(ErrorCode.InvalidDescriptor, $"Swapchain mode value {desc.Mode} is not defined.");
        if (desc.RefreshRate.Denominator == 0 && desc.RefreshRate.Numerator != 0)
            Throw(ErrorCode.InvalidDescriptor, "Swapchain refresh rate denominator must be non-zero when numerator is non-zero.");
        if (desc.ColorSpace == ColorSpace.Hdr10 && desc.Hdr10Metadata == null)
            Throw(ErrorCode.InvalidDescriptor, "HDR10 color space requires HDR10 metadata.");
    }

    public static void QueryPoolDesc(QueryPoolDesc desc)
    {
        if (!Enum.IsDefined(desc.Type))
            Throw(ErrorCode.InvalidDescriptor, $"Query type value {desc.Type} is not defined.");
        if (desc.Count == 0)
            Throw(ErrorCode.InvalidDescriptor, "Query pool count must be greater than zero.");
    }

    public static void PipelineCacheDesc(PipelineCacheDesc desc)
    {
        _ = desc;
    }

    public static void PresentDesc(PresentDesc desc, SwapchainDesc swapchain)
    {
        if (desc.SyncInterval > 4)
            Throw(ErrorCode.InvalidDescriptor, "Present sync interval must be in the range [0, 4].");
        if (desc.AllowTearing && desc.SyncInterval != 0)
            Throw(ErrorCode.InvalidDescriptor, "Tearing can only be requested with SyncInterval=0.");
        if (desc.AllowTearing && !swapchain.AllowTearing)
            Throw(ErrorCode.InvalidDescriptor, "Present requested tearing but the swapchain was not created with AllowTearing.");
    }

    public static void AccelerationStructureDesc(AccelerationStructureDesc desc)
    {
        if (!Enum.IsDefined(desc.Kind))
            Throw(ErrorCode.InvalidDescriptor, $"Acceleration structure kind value {desc.Kind} is not defined.");
        if (desc.SizeInBytes == 0)
            Throw(ErrorCode.InvalidDescriptor, "Acceleration structure size must be greater than zero.");
    }

    public static void AccelBuildDesc(AccelBuildDesc desc, bool requireResourceHandles = true)
    {
        if (!Enum.IsDefined(desc.Kind))
            Throw(ErrorCode.InvalidDescriptor, $"Acceleration structure build kind value {desc.Kind} is not defined.");
        if (requireResourceHandles && !desc.Destination.IsValid)
            Throw(ErrorCode.InvalidHandle, "Acceleration structure build destination handle is invalid.");
        if ((desc.Flags & ~KnownAccelBuildFlags) != 0)
            Throw(ErrorCode.InvalidDescriptor, $"Acceleration structure build flags contain unsupported bits: {desc.Flags}.");
        if (desc.Geometries.Count == 0)
            Throw(ErrorCode.InvalidDescriptor, "Acceleration structure build requires at least one geometry.");
        if (requireResourceHandles && !desc.ScratchBuffer.IsValid)
            Throw(ErrorCode.InvalidHandle, "Acceleration structure build requires a scratch buffer.");
        if (desc.Kind == AccelerationStructureKind.TopLevel && desc.Geometries.Count != 1)
            Throw(ErrorCode.InvalidDescriptor, "Top-level acceleration structure builds require exactly one instance geometry.");
        for (int index = 0; index < desc.Geometries.Count; index++)
            ValidateGeom(desc.Kind, desc.Geometries[index], index, requireResourceHandles);
    }

    public static void RtPipelineDesc(RtPipelineDesc desc, DeviceLimits limits)
    {
        if (!desc.Layout.IsValid)
            Throw(ErrorCode.InvalidHandle, "Ray tracing pipeline layout handle is invalid.");
        if (desc.Shaders.Count == 0)
            Throw(ErrorCode.InvalidDescriptor, "Ray tracing pipeline requires at least one shader.");
        if (desc.ShaderGroups.Count == 0)
            Throw(ErrorCode.InvalidDescriptor, "Ray tracing pipeline requires at least one shader group.");
        if (desc.MaxRayRecursionDepth == 0)
            Throw(ErrorCode.InvalidDescriptor, "Ray tracing max recursion depth must be greater than zero.");
        if (desc.MaxRayRecursionDepth > limits.MaxRayRecursionDepth)
            Throw(ErrorCode.InvalidDescriptor, $"Ray tracing max recursion depth exceeds limit {limits.MaxRayRecursionDepth}.");
        if (desc.MaxPayloadSizeInBytes > limits.MaxPushConstantBytes * 16)
            Throw(ErrorCode.InvalidDescriptor, "Ray tracing max payload size is outside the current RHI limit.");
        if (desc.MaxAttributeSizeInBytes > limits.MaxRayTracingAttributeSizeInBytes)
            Throw(ErrorCode.InvalidDescriptor, $"Ray tracing max attribute size exceeds limit {limits.MaxRayTracingAttributeSizeInBytes}.");
    }

    public static void MeshPipelineDesc(MeshPipelineDesc desc)
    {
        if (!desc.MeshShader.IsValid)
            Throw(ErrorCode.InvalidHandle, "Mesh pipeline requires a mesh shader.");
        if (!Enum.IsDefined(desc.Topology))
            Throw(ErrorCode.InvalidDescriptor, $"Mesh pipeline topology value {desc.Topology} is not defined.");
        if (desc.Topology == PrimitiveTopology.PatchList)
            Throw(ErrorCode.InvalidDescriptor, "Mesh pipeline topology cannot be PatchList.");
        if (desc.SampleCount == 0 || desc.Multisample.SampleCount == 0)
            Throw(ErrorCode.InvalidDescriptor, "Mesh pipeline sample counts must be greater than zero.");
        if (desc.SampleCount != desc.Multisample.SampleCount)
            Throw(ErrorCode.InvalidDescriptor, "Mesh pipeline SampleCount must match Multisample.SampleCount.");
        if (desc.ColorFormats.Count == 0 && desc.DepthStencilFormat == Format.Unknown)
            Throw(ErrorCode.InvalidDescriptor, "Mesh pipeline requires at least one color or depth format.");
    }

    private const AccelBuildFlags KnownAccelBuildFlags =
        AccelBuildFlags.PreferFastTrace
        | AccelBuildFlags.PreferFastBuild
        | AccelBuildFlags.AllowUpdate
        | AccelBuildFlags.AllowCompaction;

    private static void ValidateGeom(AccelerationStructureKind buildKind, AccelGeomDesc desc, int index, bool requireResourceHandles)
    {
        if (!Enum.IsDefined(desc.Kind))
            Throw(ErrorCode.InvalidDescriptor, $"Acceleration structure geometry {index} kind value {desc.Kind} is not defined.");
        if ((desc.Flags & ~(AccelGeomFlags.Opaque | AccelGeomFlags.NoDuplicateAnyHitInvocation)) != 0)
            Throw(ErrorCode.InvalidDescriptor, $"Acceleration structure geometry {index} flags contain unsupported bits: {desc.Flags}.");
        if (buildKind == AccelerationStructureKind.TopLevel && desc.Kind != AccelGeomKind.Instances)
            Throw(ErrorCode.InvalidDescriptor, "Top-level acceleration structure builds require instance geometry.");
        if (buildKind == AccelerationStructureKind.BottomLevel && desc.Kind == AccelGeomKind.Instances)
            Throw(ErrorCode.InvalidDescriptor, "Bottom-level acceleration structure builds cannot use instance geometry.");
        switch (desc.Kind)
        {
            case AccelGeomKind.Triangles:
                if (requireResourceHandles && !desc.VertexBuffer.IsValid)
                    Throw(ErrorCode.InvalidHandle, $"Acceleration structure triangle geometry {index} requires a vertex buffer.");
                if (desc.VertexCount == 0 || desc.VertexStrideInBytes == 0)
                    Throw(ErrorCode.InvalidDescriptor, $"Acceleration structure triangle geometry {index} requires vertex count and stride.");
                if (desc.IndexBuffer.IsValid && desc.IndexCount == 0)
                    Throw(ErrorCode.InvalidDescriptor, $"Acceleration structure triangle geometry {index} index buffer requires a non-zero index count.");
                if (requireResourceHandles && desc.IndexCount > 0 && !desc.IndexBuffer.IsValid)
                    Throw(ErrorCode.InvalidHandle, $"Acceleration structure triangle geometry {index} indexed build requires an index buffer.");
                if (desc.TransformBuffer.IsValid && desc.TransformOffset % 16 != 0)
                    Throw(ErrorCode.InvalidDescriptor, $"Acceleration structure triangle geometry {index} transform offset must be 16-byte aligned.");
                break;
            case AccelGeomKind.Aabbs:
                if (requireResourceHandles && !desc.AabbBuffer.IsValid)
                    Throw(ErrorCode.InvalidHandle, $"Acceleration structure AABB geometry {index} requires an AABB buffer.");
                if (desc.AabbCount == 0 || desc.AabbStrideInBytes == 0)
                    Throw(ErrorCode.InvalidDescriptor, $"Acceleration structure AABB geometry {index} requires AABB count and stride.");
                if (desc.AabbOffset % 8 != 0 || desc.AabbStrideInBytes % 8 != 0)
                    Throw(ErrorCode.InvalidDescriptor, $"Acceleration structure AABB geometry {index} offset and stride must be 8-byte aligned.");
                break;
            case AccelGeomKind.Instances:
                if (requireResourceHandles && !desc.InstanceBuffer.IsValid)
                    Throw(ErrorCode.InvalidHandle, $"Acceleration structure instance geometry {index} requires an instance buffer.");
                if (desc.InstanceCount == 0)
                    Throw(ErrorCode.InvalidDescriptor, $"Acceleration structure instance geometry {index} requires instance count.");
                if (desc.InstanceOffset % 16 != 0)
                    Throw(ErrorCode.InvalidDescriptor, $"Acceleration structure instance geometry {index} offset must be 16-byte aligned.");
                break;
        }
    }

    public static bool IsDepthFormat(Format format) => format is Format.D32Float or Format.D24UnormS8UInt;

    public static void BufferResourceState(BufferDesc desc, ResourceState state, string label)
    {
        RequireResourceState(ResourceStateValidation.BufferRequirement(state), desc.BindFlags, label);
    }

    public static void TextureResourceState(TextureDesc desc, ResourceState state, string label, bool allowPresent = false)
    {
        RequireResourceState(ResourceStateValidation.TextureRequirement(state, allowPresent), desc.BindFlags, label);
    }

    private static void RequireResourceState(
        ResourceStateValidationResult requirement,
        BindFlags bindFlags,
        string label)
    {
        if (!requirement.IsValid)
        {
            Throw(ErrorCode.InvalidDescriptor, $"{label} {requirement.InvalidReason}.");
            return;
        }

        if (requirement.RequiredBindFlags == BindFlags.None)
            return;

        if (requirement.AcceptsAnyBindFlag)
        {
            if ((bindFlags & requirement.RequiredBindFlags) == 0)
                Throw(ErrorCode.InvalidDescriptor, $"{label} requires at least one read bind flag.");
            return;
        }

        if ((bindFlags & requirement.RequiredBindFlags) != requirement.RequiredBindFlags)
            Throw(ErrorCode.InvalidDescriptor, $"{label} requires bind flag {requirement.RequiredBindFlags}.");
    }

    private static bool SupportsDynamicOffset(BindingType type)
        => type is BindingType.ConstantBuffer
            or BindingType.StorageBufferRead
            or BindingType.StorageBufferReadWrite
            or BindingType.RawBufferRead
            or BindingType.RawBufferReadWrite;

    private static void ValidateSlotShape(BindingSlotDesc slot)
    {
        var shape = slot.Shape ?? throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding slot {slot.Binding} shape must not be null.");
        var samplerShape = shape.Sampler;
        if (samplerShape == null)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding slot {slot.Binding} sampler shape must not be null.");
        if (!Enum.IsDefined(shape.TextureDimension))
            Throw(ErrorCode.InvalidDescriptor, $"Binding slot {slot.Binding} texture dimension value {shape.TextureDimension} is not defined.");
        if (!Enum.IsDefined(shape.Format))
            Throw(ErrorCode.InvalidDescriptor, $"Binding slot {slot.Binding} format value {shape.Format} is not defined.");

        if ((slot.Flags & BindingFlags.PartiallyBound) == 0)
            return;

        switch (slot.Type)
        {
            case BindingType.TextureRead:
                if (shape.Format == Format.Unknown)
                    Throw(ErrorCode.InvalidDescriptor, $"Partially bound texture binding {slot.Binding} must declare a concrete null-descriptor format.");
                if (IsDepthFormat(shape.Format))
                    Throw(ErrorCode.InvalidDescriptor, $"Partially bound texture binding {slot.Binding} cannot use depth/stencil format {shape.Format}.");
                break;
            case BindingType.TextureReadWrite:
                if (shape.Format == Format.Unknown)
                    Throw(ErrorCode.InvalidDescriptor, $"Partially bound texture UAV binding {slot.Binding} must declare a concrete null-descriptor format.");
                if (IsDepthFormat(shape.Format))
                    Throw(ErrorCode.InvalidDescriptor, $"Partially bound texture UAV binding {slot.Binding} cannot use depth/stencil format {shape.Format}.");
                if (shape.TextureDimension is TextureViewDimension.TextureCube
                    or TextureViewDimension.TextureCubeArray
                    or TextureViewDimension.Texture2DMultisampled
                    or TextureViewDimension.Texture2DMultisampledArray)
                {
                    Throw(ErrorCode.InvalidDescriptor, $"Partially bound texture UAV binding {slot.Binding} uses unsupported UAV dimension {shape.TextureDimension}.");
                }
                break;
            case BindingType.StorageBufferRead:
            case BindingType.StorageBufferReadWrite:
                if ((shape.Format == Format.Unknown) == (shape.StrideInBytes == 0))
                    Throw(ErrorCode.InvalidDescriptor, $"Partially bound storage buffer binding {slot.Binding} must declare exactly one of typed format or structured stride.");
                if (shape.Format != Format.Unknown && IsDepthFormat(shape.Format))
                    Throw(ErrorCode.InvalidDescriptor, $"Partially bound storage buffer binding {slot.Binding} cannot use depth/stencil format {shape.Format}.");
                break;
            case BindingType.Sampler:
                SamplerDesc(samplerShape);
                break;
        }
    }

    private static uint MaxMipLevels(TextureDesc desc)
    {
        uint maxDimension = desc.Dimension switch
        {
            ResourceDimension.Texture1D => desc.Width,
            ResourceDimension.Texture2D or ResourceDimension.TextureCube => Math.Max(desc.Width, desc.Height),
            ResourceDimension.Texture3D => Math.Max(desc.Width, Math.Max(desc.Height, desc.Depth)),
            _ => Math.Max(desc.Width, Math.Max(desc.Height, desc.Depth)),
        };
        uint levels = 1;
        while (maxDimension > 1)
        {
            maxDimension >>= 1;
            levels++;
        }

        return levels;
    }

    private static void RequireTextureBind(TextureDesc texture, ViewKind kind)
    {
        BindFlags required = kind switch
        {
            ViewKind.ShaderResource => BindFlags.ShaderResource,
            ViewKind.UnorderedAccess => BindFlags.UnorderedAccess,
            ViewKind.RenderTarget => BindFlags.RenderTarget,
            ViewKind.DepthStencil => BindFlags.DepthStencil,
            ViewKind.ConstantBuffer => throw new RhiException(ErrorCode.InvalidDescriptor, "Textures cannot have constant-buffer views."),
            _ => BindFlags.None,
        };

        if (required != BindFlags.None && !texture.BindFlags.HasFlag(required))
            Throw(ErrorCode.InvalidDescriptor, $"Texture view kind {kind} requires bind flag {required}.");
    }

    private static void ValidateViewDim(TextureDesc texture, TextureViewDesc desc)
    {
        switch (texture.Dimension)
        {
            case ResourceDimension.Texture1D:
                if (desc.Dimension == TextureViewDimension.Texture1D)
                {
                    if (desc.SliceCount != 1)
                        Throw(ErrorCode.InvalidDescriptor, "Texture1D views require exactly one slice.");
                }
                else if (desc.Dimension != TextureViewDimension.Texture1DArray)
                {
                    Throw(ErrorCode.InvalidDescriptor, "Texture1D views must be Texture1D or Texture1DArray.");
                }
                break;
            case ResourceDimension.Texture2D:
                if (texture.SampleCount > 1)
                {
                    if (desc.Dimension == TextureViewDimension.Texture2DMultisampled)
                    {
                        if (desc.SliceCount != 1)
                            Throw(ErrorCode.InvalidDescriptor, "Texture2DMultisampled views require exactly one slice.");
                    }
                    else if (desc.Dimension != TextureViewDimension.Texture2DMultisampledArray)
                    {
                        Throw(ErrorCode.InvalidDescriptor, "Multisampled Texture2D views must be Texture2DMultisampled or Texture2DMultisampledArray.");
                    }
                    if (desc.MipCount != 1)
                        Throw(ErrorCode.InvalidDescriptor, "Multisampled texture views require exactly one mip level.");
                }
                else if (desc.Dimension == TextureViewDimension.Texture2D)
                {
                    if (desc.SliceCount != 1)
                        Throw(ErrorCode.InvalidDescriptor, "Texture2D views require exactly one slice.");
                }
                else if (desc.Dimension != TextureViewDimension.Texture2DArray)
                {
                    Throw(ErrorCode.InvalidDescriptor, "Texture2D views must be Texture2D or Texture2DArray.");
                }
                break;
            case ResourceDimension.Texture3D:
                if (desc.Dimension != TextureViewDimension.Texture3D || desc.FirstSlice != 0 || desc.SliceCount != 1)
                    Throw(ErrorCode.InvalidDescriptor, "Texture3D views must be Texture3D with FirstSlice=0 and SliceCount=1.");
                break;
            case ResourceDimension.TextureCube:
                if (desc.Dimension == TextureViewDimension.TextureCube)
                {
                    if (desc.FirstSlice % 6 != 0 || desc.SliceCount != 6)
                        Throw(ErrorCode.InvalidDescriptor, "TextureCube views require cube-aligned FirstSlice and exactly 6 slices.");
                }
                else if (desc.Dimension == TextureViewDimension.TextureCubeArray)
                {
                    if (desc.FirstSlice % 6 != 0 || desc.SliceCount % 6 != 0)
                        Throw(ErrorCode.InvalidDescriptor, "TextureCubeArray views require cube-aligned FirstSlice and a slice count divisible by 6.");
                }
                else
                {
                    Throw(ErrorCode.InvalidDescriptor, "TextureCube views must be TextureCube or TextureCubeArray.");
                }
                break;
            default:
                Throw(ErrorCode.InvalidDescriptor, $"Unsupported texture dimension {texture.Dimension}.");
                break;
        }
    }

    private static void RequireBufferBind(BufferDesc buffer, ViewKind kind)
    {
        BindFlags required = kind switch
        {
            ViewKind.ConstantBuffer => BindFlags.ConstantBuffer,
            ViewKind.ShaderResource => BindFlags.ShaderResource,
            ViewKind.UnorderedAccess => BindFlags.UnorderedAccess,
            ViewKind.RenderTarget => throw new RhiException(ErrorCode.InvalidDescriptor, "Buffers cannot have render-target views."),
            ViewKind.DepthStencil => throw new RhiException(ErrorCode.InvalidDescriptor, "Buffers cannot have depth-stencil views."),
            _ => BindFlags.None,
        };

        if (required != BindFlags.None && !buffer.BindFlags.HasFlag(required))
            Throw(ErrorCode.InvalidDescriptor, $"Buffer view kind {kind} requires bind flag {required}.");
    }

    public static void Throw(ErrorCode code, string message) => throw new RhiException(code, message);
}
