using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using D3DClearValue = Vortice.Direct3D12.ClearValue;
using D3DPrimitiveTopology = Vortice.Direct3D.PrimitiveTopology;
using RhiClearValue = SomeEngine.Rhi.ClearValue;
using RhiFormat = SomeEngine.Rhi.Format;
using RhiPrimitiveTopology = SomeEngine.Rhi.PrimitiveTopology;
using DxgiFormat = Vortice.DXGI.Format;
using RhiResourceDimension = SomeEngine.Rhi.ResourceDimension;
using D3DResourceDimension = Vortice.Direct3D12.ResourceDimension;

namespace SomeEngine.Rhi.D3D12;

internal static class D3D12Mappings
{
    public static DxgiFormat ToDxgi(RhiFormat format)
        => format switch
        {
            RhiFormat.Unknown => DxgiFormat.Unknown,
            RhiFormat.R8Unorm => DxgiFormat.R8_UNorm,
            RhiFormat.R8UInt => DxgiFormat.R8_UInt,
            RhiFormat.R16UInt => DxgiFormat.R16_UInt,
            RhiFormat.R16Float => DxgiFormat.R16_Float,
            RhiFormat.Rg8Unorm => DxgiFormat.R8G8_UNorm,
            RhiFormat.Rg16UInt => DxgiFormat.R16G16_UInt,
            RhiFormat.Rgba8Unorm => DxgiFormat.R8G8B8A8_UNorm,
            RhiFormat.Rgba8UnormSrgb => DxgiFormat.R8G8B8A8_UNorm_SRgb,
            RhiFormat.Bgra8Unorm => DxgiFormat.B8G8R8A8_UNorm,
            RhiFormat.Bgra8UnormSrgb => DxgiFormat.B8G8R8A8_UNorm_SRgb,
            RhiFormat.Rgb10A2Unorm => DxgiFormat.R10G10B10A2_UNorm,
            RhiFormat.Rgba16Float => DxgiFormat.R16G16B16A16_Float,
            RhiFormat.R32UInt => DxgiFormat.R32_UInt,
            RhiFormat.R32Float => DxgiFormat.R32_Float,
            RhiFormat.Rg16Float => DxgiFormat.R16G16_Float,
            RhiFormat.Rg32Float => DxgiFormat.R32G32_Float,
            RhiFormat.Rgb32Float => DxgiFormat.R32G32B32_Float,
            RhiFormat.Rgba32Float => DxgiFormat.R32G32B32A32_Float,
            RhiFormat.D32Float => DxgiFormat.D32_Float,
            RhiFormat.D24UnormS8UInt => DxgiFormat.D24_UNorm_S8_UInt,
            RhiFormat.Bc1RgbaUnorm => DxgiFormat.BC1_UNorm,
            RhiFormat.Bc1RgbaUnormSrgb => DxgiFormat.BC1_UNorm_SRgb,
            RhiFormat.Bc2RgbaUnorm => DxgiFormat.BC2_UNorm,
            RhiFormat.Bc2RgbaUnormSrgb => DxgiFormat.BC2_UNorm_SRgb,
            RhiFormat.Bc3RgbaUnorm => DxgiFormat.BC3_UNorm,
            RhiFormat.Bc3RgbaUnormSrgb => DxgiFormat.BC3_UNorm_SRgb,
            RhiFormat.Bc4RUnorm => DxgiFormat.BC4_UNorm,
            RhiFormat.Bc5RgUnorm => DxgiFormat.BC5_UNorm,
            RhiFormat.Bc6HUFloat => DxgiFormat.BC6H_Uf16,
            RhiFormat.Bc7RgbaUnorm => DxgiFormat.BC7_UNorm,
            RhiFormat.Bc7RgbaUnormSrgb => DxgiFormat.BC7_UNorm_SRgb,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Format {format} is not defined."),
        };

    public static DxgiFormat ToTextureDxgi(TextureDesc desc)
    {
        if (!desc.BindFlags.HasFlag(BindFlags.DepthStencil) || !desc.BindFlags.HasFlag(BindFlags.ShaderResource))
            return ToDxgi(desc.Format);
        return desc.Format switch
        {
            RhiFormat.D32Float => DxgiFormat.R32_Typeless,
            RhiFormat.D24UnormS8UInt => DxgiFormat.R24G8_Typeless,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Depth format {desc.Format} is not defined for shader-resource views."),
        };
    }

    public static DxgiFormat ToSrvDxgi(RhiFormat format)
        => format switch
        {
            RhiFormat.D32Float => DxgiFormat.R32_Float,
            RhiFormat.D24UnormS8UInt => DxgiFormat.R24_UNorm_X8_Typeless,
            _ => ToDxgi(format),
        };

    public static ResourceStates ToState(ResourceState state)
        => state switch
        {
            ResourceState.Undefined => ResourceStates.Common,
            ResourceState.Common => ResourceStates.Common,
            ResourceState.GenericRead => ResourceStates.GenericRead,
            ResourceState.Present => ResourceStates.Present,
            ResourceState.VertexBuffer => ResourceStates.VertexAndConstantBuffer,
            ResourceState.IndexBuffer => ResourceStates.IndexBuffer,
            ResourceState.ConstantBuffer => ResourceStates.VertexAndConstantBuffer,
            ResourceState.ShaderResource => ResourceStates.AllShaderResource,
            ResourceState.UnorderedAccess => ResourceStates.UnorderedAccess,
            ResourceState.RenderTarget => ResourceStates.RenderTarget,
            ResourceState.DepthRead => ResourceStates.DepthRead,
            ResourceState.DepthWrite => ResourceStates.DepthWrite,
            ResourceState.CopySource => ResourceStates.CopySource,
            ResourceState.CopyDestination => ResourceStates.CopyDest,
            ResourceState.ResolveSource => ResourceStates.ResolveSource,
            ResourceState.ResolveDestination => ResourceStates.ResolveDest,
            ResourceState.IndirectArgument => ResourceStates.AllShaderResource | ResourceStates.IndirectArgument,
            ResourceState.QueryResolve => ResourceStates.CopyDest,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Resource state {state} is not defined."),
        };

    public static CommandListType ToListType(QueueType type)
        => type switch
        {
            QueueType.Graphics => CommandListType.Direct,
            QueueType.Compute => CommandListType.Compute,
            QueueType.Copy => CommandListType.Copy,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Queue type {type} is not defined."),
        };

    public static CommandQueueKind ToQueueKind(QueueType type)
        => type switch
        {
            QueueType.Graphics => CommandQueueKind.Direct,
            QueueType.Compute => CommandQueueKind.Compute,
            QueueType.Copy => CommandQueueKind.Copy,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Queue type {type} is not defined."),
        };

    public static HeapType ToHeapType(MemoryClass memory)
        => memory switch
        {
            MemoryClass.DeviceLocal => HeapType.Default,
            MemoryClass.CpuUpload => HeapType.Upload,
            MemoryClass.CpuReadback => HeapType.Readback,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Memory class {memory} is not defined."),
        };

    public static HeapFlags ToHeapFlags(MemoryHeapKind kind)
        => kind switch
        {
            MemoryHeapKind.Buffer => HeapFlags.AllowOnlyBuffers,
            MemoryHeapKind.Texture => HeapFlags.AllowOnlyNonRenderTargetDepthStencilTextures,
            MemoryHeapKind.RenderTargetOrDepthStencil => HeapFlags.AllowOnlyRenderTargetDepthStencilTextures,
            MemoryHeapKind.Mixed => HeapFlags.AllowAllBuffersAndTextures,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Memory heap kind {kind} is not defined."),
        };

    public static ResourceDescription BufferResourceDesc(BufferDesc desc)
        => new(
            D3DResourceDimension.Buffer,
            0,
            desc.SizeInBytes,
            1,
            1,
            1,
            DxgiFormat.Unknown,
            1,
            0,
            TextureLayout.RowMajor,
            ToResourceFlags(desc.BindFlags));

    public static ResourceDescription TextureResourceDesc(TextureDesc desc)
        => new(
            ToDimension(desc.Dimension),
            0,
            desc.Width,
            desc.Height,
            checked((ushort)(desc.Dimension == RhiResourceDimension.Texture3D ? desc.Depth : desc.ArraySize)),
            checked((ushort)desc.MipLevels),
            ToTextureDxgi(desc),
            desc.SampleCount,
            0,
            TextureLayout.Unknown,
            ToResourceFlags(desc.BindFlags));

    public static D3DResourceDimension ToDimension(RhiResourceDimension dimension)
        => dimension switch
        {
            RhiResourceDimension.Texture1D => D3DResourceDimension.Texture1D,
            RhiResourceDimension.Texture2D or RhiResourceDimension.TextureCube => D3DResourceDimension.Texture2D,
            RhiResourceDimension.Texture3D => D3DResourceDimension.Texture3D,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture dimension {dimension} is not defined."),
        };

    public static ResourceFlags ToResourceFlags(BindFlags flags)
    {
        ResourceFlags result = ResourceFlags.None;
        if (flags.HasFlag(BindFlags.RenderTarget))
            result |= ResourceFlags.AllowRenderTarget;
        if (flags.HasFlag(BindFlags.DepthStencil))
        {
            result |= ResourceFlags.AllowDepthStencil;
            if (!flags.HasFlag(BindFlags.ShaderResource))
                result |= ResourceFlags.DenyShaderResource;
        }
        if (flags.HasFlag(BindFlags.UnorderedAccess))
            result |= ResourceFlags.AllowUnorderedAccess;
        return result;
    }

    public static D3DClearValue? ToClearValue(RhiClearValue? value)
    {
        if (value == null)
            return null;
        var clear = value.Value;
        if (clear.Format is RhiFormat.D32Float or RhiFormat.D24UnormS8UInt)
            return new D3DClearValue(ToDxgi(clear.Format), clear.DepthStencil.Depth, clear.DepthStencil.Stencil);
        var color = new Color4(clear.Color.R, clear.Color.G, clear.Color.B, clear.Color.A);
        return new D3DClearValue(ToDxgi(clear.Format), in color);
    }

    public static DescriptorRangeType ToRangeType(BindingType type)
        => type switch
        {
            BindingType.ConstantBuffer => DescriptorRangeType.ConstantBufferView,
            BindingType.StorageBufferRead or BindingType.RawBufferRead or BindingType.TextureRead or BindingType.AccelerationStructure => DescriptorRangeType.ShaderResourceView,
            BindingType.StorageBufferReadWrite or BindingType.RawBufferReadWrite or BindingType.TextureReadWrite => DescriptorRangeType.UnorderedAccessView,
            BindingType.Sampler => DescriptorRangeType.Sampler,
            _ => throw new RhiException(ErrorCode.UnsupportedFeature, $"Binding type {type} is not supported by D3D12 backend."),
        };

    public static ShaderVisibility ToShaderVisibility(ShaderStageFlags stages)
        => stages switch
        {
            ShaderStageFlags.Vertex => ShaderVisibility.Vertex,
            ShaderStageFlags.Pixel => ShaderVisibility.Pixel,
            ShaderStageFlags.Hull => ShaderVisibility.Hull,
            ShaderStageFlags.Domain => ShaderVisibility.Domain,
            ShaderStageFlags.Geometry => ShaderVisibility.Geometry,
            _ => ShaderVisibility.All,
        };

    public static Filter ToFilter(SamplerDesc desc)
    {
        if (desc.Compare != null)
            return Filter.ComparisonMinMagMipLinear;
        return desc.MinFilter == FilterMode.Nearest && desc.MagFilter == FilterMode.Nearest && desc.MipmapMode == MipmapMode.Nearest
            ? Filter.MinMagMipPoint
            : Filter.MinMagMipLinear;
    }

    public static TextureAddressMode ToAddressMode(AddressMode mode)
        => mode switch
        {
            AddressMode.Repeat => TextureAddressMode.Wrap,
            AddressMode.MirrorRepeat => TextureAddressMode.Mirror,
            AddressMode.ClampToEdge => TextureAddressMode.Clamp,
            AddressMode.ClampToBorder => TextureAddressMode.Border,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Address mode {mode} is not defined."),
        };

    public static ComparisonFunction ToComparison(CompareOp? op)
        => op switch
        {
            null => ComparisonFunction.Never,
            CompareOp.Never => ComparisonFunction.Never,
            CompareOp.Less => ComparisonFunction.Less,
            CompareOp.Equal => ComparisonFunction.Equal,
            CompareOp.LessOrEqual => ComparisonFunction.LessEqual,
            CompareOp.Greater => ComparisonFunction.Greater,
            CompareOp.NotEqual => ComparisonFunction.NotEqual,
            CompareOp.GreaterOrEqual => ComparisonFunction.GreaterEqual,
            CompareOp.Always => ComparisonFunction.Always,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Compare op {op} is not defined."),
        };

    public static D3DPrimitiveTopology ToPrimitiveTopology(RhiPrimitiveTopology topology, uint patchControlPoints = 0)
    {
        if (topology == RhiPrimitiveTopology.PatchList)
        {
            if (patchControlPoints is < 1 or > 32)
                throw new RhiException(ErrorCode.InvalidDescriptor, "PatchList topology requires PatchControlPoints in [1, 32].");
            return (D3DPrimitiveTopology)((int)D3DPrimitiveTopology.PatchListWith1ControlPoints + (int)patchControlPoints - 1);
        }

        return topology switch
        {
            RhiPrimitiveTopology.PointList => D3DPrimitiveTopology.PointList,
            RhiPrimitiveTopology.LineList => D3DPrimitiveTopology.LineList,
            RhiPrimitiveTopology.TriangleList => D3DPrimitiveTopology.TriangleList,
            RhiPrimitiveTopology.TriangleStrip => D3DPrimitiveTopology.TriangleStrip,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Primitive topology {topology} is not defined."),
        };
    }

    public static PrimitiveTopologyType ToTopologyType(RhiPrimitiveTopology topology)
        => topology switch
        {
            RhiPrimitiveTopology.PointList => PrimitiveTopologyType.Point,
            RhiPrimitiveTopology.LineList => PrimitiveTopologyType.Line,
            RhiPrimitiveTopology.TriangleList or RhiPrimitiveTopology.TriangleStrip => PrimitiveTopologyType.Triangle,
            RhiPrimitiveTopology.PatchList => PrimitiveTopologyType.Patch,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Primitive topology {topology} is not defined."),
        };

    public static RaytracingAccelerationStructureType ToAccelType(AccelerationStructureKind kind)
        => kind switch
        {
            AccelerationStructureKind.TopLevel => RaytracingAccelerationStructureType.TopLevel,
            AccelerationStructureKind.BottomLevel => RaytracingAccelerationStructureType.BottomLevel,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Acceleration structure kind {kind} is not defined."),
        };

    public static RaytracingAccelerationStructureBuildFlags ToAccelFlags(AccelBuildFlags flags)
    {
        RaytracingAccelerationStructureBuildFlags result = RaytracingAccelerationStructureBuildFlags.None;
        if (flags.HasFlag(AccelBuildFlags.PreferFastTrace))
            result |= RaytracingAccelerationStructureBuildFlags.PreferFastTrace;
        if (flags.HasFlag(AccelBuildFlags.PreferFastBuild))
            result |= RaytracingAccelerationStructureBuildFlags.PreferFastBuild;
        if (flags.HasFlag(AccelBuildFlags.AllowUpdate))
            result |= RaytracingAccelerationStructureBuildFlags.AllowUpdate;
        if (flags.HasFlag(AccelBuildFlags.AllowCompaction))
            result |= RaytracingAccelerationStructureBuildFlags.AllowCompaction;
        return result;
    }

    public static RaytracingGeometryFlags ToRtGeom(AccelGeomFlags flags)
    {
        RaytracingGeometryFlags result = RaytracingGeometryFlags.None;
        if (flags.HasFlag(AccelGeomFlags.Opaque))
            result |= RaytracingGeometryFlags.Opaque;
        if (flags.HasFlag(AccelGeomFlags.NoDuplicateAnyHitInvocation))
            result |= RaytracingGeometryFlags.NoDuplicateAnyHitInvocation;
        return result;
    }

    public static RaytracingAccelerationStructureCopyMode ToRtCopy(AccelCopyMode mode)
        => mode switch
        {
            AccelCopyMode.Clone => RaytracingAccelerationStructureCopyMode.Clone,
            AccelCopyMode.Compact => RaytracingAccelerationStructureCopyMode.Compact,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Acceleration structure copy mode {mode} is not defined."),
        };

    public static DxgiFormat ToRtVertex(RhiFormat format)
        => format switch
        {
            RhiFormat.Rg16Float => DxgiFormat.R16G16_Float,
            RhiFormat.Rg32Float => DxgiFormat.R32G32_Float,
            RhiFormat.Rgb32Float => DxgiFormat.R32G32B32_Float,
            RhiFormat.Rgba16Float => DxgiFormat.R16G16B16A16_Float,
            RhiFormat.Rgba32Float => DxgiFormat.R32G32B32A32_Float,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Format {format} is not valid for D3D12 ray tracing vertex data."),
        };

    public static Vortice.Direct3D12.Blend ToBlend(BlendFactor factor)
        => factor switch
        {
            BlendFactor.Zero => Vortice.Direct3D12.Blend.Zero,
            BlendFactor.One => Vortice.Direct3D12.Blend.One,
            BlendFactor.SourceColor => Vortice.Direct3D12.Blend.SourceColor,
            BlendFactor.OneMinusSourceColor => Vortice.Direct3D12.Blend.InverseSourceColor,
            BlendFactor.DestinationColor => Vortice.Direct3D12.Blend.DestinationColor,
            BlendFactor.OneMinusDestinationColor => Vortice.Direct3D12.Blend.InverseDestinationColor,
            BlendFactor.SourceAlpha => Vortice.Direct3D12.Blend.SourceAlpha,
            BlendFactor.OneMinusSourceAlpha => Vortice.Direct3D12.Blend.InverseSourceAlpha,
            BlendFactor.DestinationAlpha => Vortice.Direct3D12.Blend.DestinationAlpha,
            BlendFactor.OneMinusDestinationAlpha => Vortice.Direct3D12.Blend.InverseDestinationAlpha,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Blend factor {factor} is not defined."),
        };

    public static BlendOperation ToBlendOperation(BlendOp op)
        => op switch
        {
            BlendOp.Add => BlendOperation.Add,
            BlendOp.Subtract => BlendOperation.Subtract,
            BlendOp.ReverseSubtract => BlendOperation.RevSubtract,
            BlendOp.Min => BlendOperation.Min,
            BlendOp.Max => BlendOperation.Max,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Blend op {op} is not defined."),
        };

    public static ColorWriteEnable ToColorWrite(ColorWriteMask mask)
    {
        ColorWriteEnable result = ColorWriteEnable.None;
        if (mask.HasFlag(ColorWriteMask.Red))
            result |= ColorWriteEnable.Red;
        if (mask.HasFlag(ColorWriteMask.Green))
            result |= ColorWriteEnable.Green;
        if (mask.HasFlag(ColorWriteMask.Blue))
            result |= ColorWriteEnable.Blue;
        if (mask.HasFlag(ColorWriteMask.Alpha))
            result |= ColorWriteEnable.Alpha;
        return result;
    }
}
