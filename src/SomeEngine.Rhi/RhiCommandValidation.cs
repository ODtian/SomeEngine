namespace SomeEngine.Rhi;

internal static class RhiCommandValidation
{
    public static IndirectValidation ValidateIndirectDraw(
        DeviceFeatures features,
        DeviceLimits limits,
        bool indexed,
        uint count,
        uint strideInBytes,
        ulong argumentOffset,
        bool hasCountBuffer,
        ulong countBufferOffset)
    {
        uint tightStride = indexed ? IndirectArgumentSize.DrawIndexed : IndirectArgumentSize.Draw;
        if (!features.DrawIndirect)
            throw new RhiException(ErrorCode.UnsupportedFeature, "DrawIndirect is not supported by this device.");
        if (count == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Indirect draw count must be greater than zero.");
        if ((argumentOffset & 3) != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Indirect draw argument offset must be 4-byte aligned.");
        if (strideInBytes != tightStride)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Indirect draw stride must be exactly {tightStride} bytes.");
        if (count > 1 && !features.MultiDrawIndirect)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Multi-draw indirect is not supported by this device.");
        if (count > limits.MaxIndirectDrawCount)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Indirect draw count exceeds the device limit.");
        ValidateCountFeature(features, hasCountBuffer, "draws");
        return new IndirectValidation(count, tightStride, checked((ulong)(count - 1) * strideInBytes + tightStride));
    }

    public static IndirectValidation ValidateIndirectDispatch(
        DeviceFeatures features,
        DeviceLimits limits,
        in IndirectDispatchDesc desc)
    {
        if (!features.DispatchIndirect)
            throw new RhiException(ErrorCode.UnsupportedFeature, "DispatchIndirect is not supported by this device.");
        if (desc.DispatchCount == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Indirect dispatch count must be greater than zero.");
        if ((desc.ArgumentOffset & 3) != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Indirect dispatch argument offset must be 4-byte aligned.");
        if (desc.StrideInBytes != IndirectArgumentSize.Dispatch)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Indirect dispatch stride must be exactly {IndirectArgumentSize.Dispatch} bytes.");
        if (desc.DispatchCount > 1 && !features.MultiDispatchIndirect)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Multi-dispatch indirect is not supported by this device.");
        if (desc.DispatchCount > limits.MaxIndirectDispatchCount)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Indirect dispatch count exceeds the device limit.");
        ValidateCountFeature(features, desc.CountBuffer.IsValid, "dispatches");
        return new IndirectValidation(
            desc.DispatchCount,
            IndirectArgumentSize.Dispatch,
            checked((ulong)(desc.DispatchCount - 1) * desc.StrideInBytes + IndirectArgumentSize.Dispatch));
    }

    public static void ValidateArgsBuffer(BufferDesc buffer, ulong offset, ulong byteCount, string label)
    {
        if ((buffer.BindFlags & BindFlags.IndirectArgument) == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} requires a buffer with IndirectArgument bind flag.");
        if (offset > buffer.SizeInBytes || byteCount > buffer.SizeInBytes - offset)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} range is outside the buffer.");
    }

    public static void ValidateCountBuffer(BufferDesc buffer, ulong offset)
    {
        if ((buffer.BindFlags & BindFlags.IndirectArgument) == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Indirect count buffer requires IndirectArgument bind flag.");
        if ((offset & 3) != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Indirect count buffer offset must be 4-byte aligned.");
        if (offset > buffer.SizeInBytes || sizeof(uint) > buffer.SizeInBytes - offset)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Indirect count buffer range is outside the buffer.");
    }

    public static void ValidateSubresource(TextureDesc texture, SubresourceRange range)
    {
        uint mipCount = range.MipCount == uint.MaxValue ? texture.MipLevels - range.FirstMip : range.MipCount;
        uint sliceCount = range.SliceCount == uint.MaxValue ? texture.ArraySize - range.FirstSlice : range.SliceCount;
        if (range.FirstMip >= texture.MipLevels || mipCount == 0 || mipCount > texture.MipLevels - range.FirstMip)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture barrier mip range is outside the texture.");
        if (range.FirstSlice >= texture.ArraySize || sliceCount == 0 || sliceCount > texture.ArraySize - range.FirstSlice)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture barrier slice range is outside the texture.");
    }

    public static void ValidateTextureRegion(TextureDesc texture, TextureCopyRegion region)
    {
        if (region.Width == 0 || region.Height == 0 || region.Depth == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture copy extent must be non-zero.");
        if (region.MipLevel >= texture.MipLevels || region.ArraySlice >= texture.ArraySize)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture copy mip or array slice is outside the texture.");

        uint mipWidth = MipExtent(texture.Width, region.MipLevel);
        uint mipHeight = MipExtent(texture.Height, region.MipLevel);
        uint mipDepth = MipExtent(texture.Depth, region.MipLevel);
        if (region.X > mipWidth || region.Width > mipWidth - region.X)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture copy X range is outside the texture.");
        if (region.Y > mipHeight || region.Height > mipHeight - region.Y)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture copy Y range is outside the texture.");
        if (region.Z > mipDepth || region.Depth > mipDepth - region.Z)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture copy Z range is outside the texture.");
    }

    public static void ValidateBufferRange(BufferDesc buffer, ulong offset, ulong byteCount, string label)
    {
        if (byteCount == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} byte count must be greater than zero.");
        if (offset > buffer.SizeInBytes || byteCount > buffer.SizeInBytes - offset)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} range is outside the buffer.");
    }

    public static ulong QueryBytes(QueryType type)
        => type switch
        {
            QueryType.Timestamp or QueryType.Occlusion => sizeof(ulong),
            QueryType.PipelineStatistics => 11UL * sizeof(ulong),
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Query type {type} is not defined."),
        };

    public static void ValidateFootprint(BufferDesc buffer, BufferTextureCopy copy, TextureCopyRegion region, Format format, ulong rowPitchAlignment)
    {
        var layout = FormatLayout(format);
        uint rowBlockCount = BlockCount(region.Width, layout.BlockWidth);
        uint rowCount = BlockCount(region.Height, layout.BlockHeight);
        ulong rowBytes = CheckedMul(rowBlockCount, layout.BytesPerBlock, "Texture copy row byte count overflowed.");
        if (copy.RowPitch < rowBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture copy row pitch is smaller than one row.");
        if (rowPitchAlignment == 0 || copy.RowPitch % rowPitchAlignment != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture copy row pitch must be aligned to {rowPitchAlignment} bytes.");
        ulong sliceBytes = CheckedMul(copy.RowPitch, rowCount, "Texture copy slice byte count overflowed.");
        if (copy.SlicePitch < sliceBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture copy slice pitch must cover all rows in the region.");

        ulong depthOffset = CheckedMul(region.Depth - 1, copy.SlicePitch, "Texture copy depth offset overflowed.");
        ulong lastSliceOffset = CheckedAdd(copy.Offset, depthOffset, "Texture copy offset overflowed.");
        ulong lastRowOffset = CheckedAdd(lastSliceOffset, CheckedMul(rowCount - 1, copy.RowPitch, "Texture copy row offset overflowed."), "Texture copy row offset overflowed.");
        ulong requiredEnd = CheckedAdd(lastRowOffset, rowBytes, "Texture copy footprint overflowed.");
        if (requiredEnd > buffer.SizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture copy footprint exceeds the buffer.");
    }

    public static void ValidateCopyCompat(
        TextureDesc source,
        TextureCopyRegion sourceRegion,
        TextureDesc destination,
        TextureCopyRegion destinationRegion)
    {
        if (source.SampleCount != 1 || destination.SampleCount != 1)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture copy commands require single-sampled textures.");
        if (source.Dimension != destination.Dimension)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture copy requires matching source and destination dimensions.");
        if (source.Format != destination.Format)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture copy requires matching source and destination formats.");
        if (sourceRegion.Width != destinationRegion.Width
            || sourceRegion.Height != destinationRegion.Height
            || sourceRegion.Depth != destinationRegion.Depth)
        {
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture copy source and destination extents must match.");
        }
    }

    public static void ValidateResolveCompat(
        TextureDesc source,
        TextureCopyRegion sourceRegion,
        TextureDesc destination,
        TextureCopyRegion destinationRegion)
    {
        if (source.Dimension != destination.Dimension)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture resolve requires matching source and destination dimensions.");
        if (source.Format != destination.Format)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture resolve requires matching source and destination formats.");
        if (sourceRegion.Width != destinationRegion.Width
            || sourceRegion.Height != destinationRegion.Height
            || sourceRegion.Depth != destinationRegion.Depth)
        {
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture resolve source and destination extents must match.");
        }

        ValidateFullResolve(source, sourceRegion, "Texture resolve source");
        ValidateFullResolve(destination, destinationRegion, "Texture resolve destination");
    }

    public static void ValidateFullResolve(TextureDesc texture, TextureCopyRegion region, string label)
    {
        uint width = MipExtent(texture.Width, region.MipLevel);
        uint height = texture.Dimension == ResourceDimension.Texture1D ? 1u : MipExtent(texture.Height, region.MipLevel);
        uint depth = texture.Dimension == ResourceDimension.Texture3D ? MipExtent(texture.Depth, region.MipLevel) : 1u;
        if (region.X != 0
            || region.Y != 0
            || region.Z != 0
            || region.Width != width
            || region.Height != height
            || region.Depth != depth)
        {
            throw new RhiException(ErrorCode.UnsupportedFeature, $"{label} must cover the full subresource.");
        }
    }

    public static void ValidateBufferCopy(TextureDesc texture)
    {
        if (texture.SampleCount != 1)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Buffer-texture copy commands require single-sampled textures.");
    }

    public static void ValidateCopyOverlap(
        TextureHandle source,
        TextureCopyRegion sourceRegion,
        TextureHandle destination,
        TextureCopyRegion destinationRegion)
    {
        if (source != destination
            || sourceRegion.MipLevel != destinationRegion.MipLevel
            || sourceRegion.ArraySlice != destinationRegion.ArraySlice)
        {
            return;
        }

        bool overlaps = RangesOverlap(sourceRegion.X, sourceRegion.Width, destinationRegion.X, destinationRegion.Width)
            && RangesOverlap(sourceRegion.Y, sourceRegion.Height, destinationRegion.Y, destinationRegion.Height)
            && RangesOverlap(sourceRegion.Z, sourceRegion.Depth, destinationRegion.Z, destinationRegion.Depth);
        if (overlaps)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Overlapping same-texture copy regions are not supported.");
    }

    public static uint FormatByteSize(Format format)
        => FormatLayout(format).BytesPerBlock;

    private static FormatFootprint FormatLayout(Format format)
        => format switch
        {
            Format.R8Unorm or Format.R8UInt => new(1, 1, 1),
            Format.R16UInt or Format.R16Float or Format.Rg8Unorm => new(2, 1, 1),
            Format.Rgba8Unorm
            or Format.Rgba8UnormSrgb
            or Format.Bgra8Unorm
            or Format.Bgra8UnormSrgb
            or Format.Rgb10A2Unorm
            or Format.R32UInt
            or Format.R32Float
            or Format.Rg16Float
            or Format.D32Float
            or Format.D24UnormS8UInt => new(4, 1, 1),
            Format.Rgba16Float => new(8, 1, 1),
            Format.Rg16UInt or Format.Rg32Float => new(8, 1, 1),
            Format.Rgb32Float => new(12, 1, 1),
            Format.Rgba32Float => new(16, 1, 1),
            Format.Bc1RgbaUnorm
            or Format.Bc1RgbaUnormSrgb
            or Format.Bc4RUnorm => new(8, 4, 4),
            Format.Bc2RgbaUnorm
            or Format.Bc2RgbaUnormSrgb
            or Format.Bc3RgbaUnorm
            or Format.Bc3RgbaUnormSrgb
            or Format.Bc5RgUnorm
            or Format.Bc6HUFloat
            or Format.Bc7RgbaUnorm
            or Format.Bc7RgbaUnormSrgb => new(16, 4, 4),
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Unsupported format {format}."),
        };

    private static uint BlockCount(uint texelCount, uint blockSize)
        => checked((texelCount + blockSize - 1) / blockSize);

    private static uint MipExtent(uint size, uint mipLevel)
        => Math.Max(1u, size >> checked((int)mipLevel));

    private static bool RangesOverlap(uint firstStart, uint firstLength, uint secondStart, uint secondLength)
    {
        uint firstEnd = checked(firstStart + firstLength);
        uint secondEnd = checked(secondStart + secondLength);
        return firstStart < secondEnd && secondStart < firstEnd;
    }

    private static void ValidateCountFeature(DeviceFeatures features, bool hasCountBuffer, string commandName)
    {
        if (!hasCountBuffer)
            return;
        if (!features.IndirectCount)
            throw new RhiException(ErrorCode.UnsupportedFeature, $"Count-buffer indirect {commandName} are not supported by this device.");
    }

    private static ulong CheckedMul(ulong left, ulong right, string message)
    {
        if (left != 0 && right > ulong.MaxValue / left)
            throw new RhiException(ErrorCode.InvalidDescriptor, message);
        return left * right;
    }

    private static ulong CheckedAdd(ulong left, ulong right, string message)
    {
        if (right > ulong.MaxValue - left)
            throw new RhiException(ErrorCode.InvalidDescriptor, message);
        return left + right;
    }
}

internal readonly record struct IndirectValidation(uint Count, uint StrideInBytes, ulong ArgumentByteCount);

internal readonly record struct FormatFootprint(uint BytesPerBlock, uint BlockWidth, uint BlockHeight);
