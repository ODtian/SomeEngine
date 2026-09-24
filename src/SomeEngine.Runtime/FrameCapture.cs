using SomeEngine.Render.Graph;
using SomeEngine.Rhi;

namespace SomeEngine.Runtime;

internal sealed class FrameCapture
{
    private readonly IDevice _device;

    public FrameCapture(IDevice device)
        => _device = device ?? throw new ArgumentNullException(nameof(device));

    public void AddTo(RenderGraph graph, RenderGraphHandle source, TextureDesc desc, string name)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!source.IsValid)
            throw new ArgumentException("Frame capture source must be valid.", nameof(source));

        uint bytesPerPixel = PixelSize(desc.Format);
        uint rowPitch = checked((uint)AlignUp(desc.Width * bytesPerPixel, _device.Limits.TextureRowPitchAlignment));
        ulong byteSize = checked((ulong)rowPitch * desc.Height);
        string resourceName = $"FrameCapture.{name}";
        var readback = graph.CreateBuffer(
            resourceName,
            new BufferDesc
            {
                Name = resourceName,
                SizeInBytes = byteSize,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
                Raw = true,
            });

        graph.AddCopyPass(
            $"{name} Capture",
            builder =>
            {
                builder.Read(source, ResourceState.CopySource);
                builder.Write(readback, ResourceState.CopyDestination);
            },
            context =>
            {
                context.CopyToBuffer(
                    source,
                    new TextureCopyRegion(0, 0, 0, 0, 0, desc.Width, desc.Height, 1),
                    readback,
                    new BufferTextureCopy(0, rowPitch, checked(rowPitch * desc.Height)));
            });

        graph.ExtractBuffer(
            readback,
            ResourceState.CopyDestination,
            (buffer, _) => Read(buffer, desc.Width, desc.Height, desc.Format, rowPitch, bytesPerPixel, name));
    }

    private void Read(
        BufferHandle readback,
        uint width,
        uint height,
        Format format,
        uint rowPitch,
        uint bytesPerPixel,
        string name)
    {
        bool mapped = false;
        try
        {
            int byteCount = checked((int)((ulong)rowPitch * height));
            Memory<byte> memory = _device.MapBuffer(readback, MapMode.Read, 0, byteCount);
            mapped = true;
            ReadOnlySpan<byte> bytes = memory.Span;
            ulong totalPixels = checked((ulong)width * height);
            ulong nonBlackPixels = 0;
            double rgbSum = 0.0;
            double nonBlackR = 0.0;
            double nonBlackG = 0.0;
            double nonBlackB = 0.0;
            double maxRgb = 0.0;
            double maxR = 0.0;
            double maxG = 0.0;
            double maxB = 0.0;

            for (uint y = 0; y < height; y++)
            {
                int row = checked((int)((ulong)y * rowPitch));
                for (uint x = 0; x < width; x++)
                {
                    int pixel = checked(row + (int)(x * bytesPerPixel));
                    ReadRgb(bytes, pixel, format, out double r, out double g, out double b);
                    maxR = Math.Max(maxR, r);
                    maxG = Math.Max(maxG, g);
                    maxB = Math.Max(maxB, b);
                    maxRgb = Math.Max(maxRgb, Math.Max(maxR, Math.Max(maxG, maxB)));
                    rgbSum += r + g + b;

                    if (r != 0.0 || g != 0.0 || b != 0.0)
                    {
                        nonBlackPixels++;
                        nonBlackR += r;
                        nonBlackG += g;
                        nonBlackB += b;
                    }
                }
            }

            double nonBlackRatio = totalPixels == 0 ? 0.0 : nonBlackPixels / (double)totalPixels;
            double meanRgb = totalPixels == 0 ? 0.0 : rgbSum / (totalPixels * 3.0);
            double visibleR = nonBlackPixels == 0 ? 0.0 : nonBlackR / nonBlackPixels;
            double visibleG = nonBlackPixels == 0 ? 0.0 : nonBlackG / nonBlackPixels;
            double visibleB = nonBlackPixels == 0 ? 0.0 : nonBlackB / nonBlackPixels;
            Console.WriteLine(
                $"Frame capture '{name}': {width}x{height} {format}, nonBlack={nonBlackPixels}/{totalPixels} ({nonBlackRatio:P2}), meanRgb={meanRgb:F4}, maxRgb={maxRgb:F4}, visibleRgb=({visibleR:F4},{visibleG:F4},{visibleB:F4}), maxRgbChannels=({maxR:F4},{maxG:F4},{maxB:F4}).");
        }
        finally
        {
            if (mapped)
                _device.UnmapBuffer(readback);
            _device.Destroy(readback);
        }
    }

    private static uint PixelSize(Format format)
        => format switch
        {
            Format.Rgba8Unorm
                or Format.Rgba8UnormSrgb
                or Format.Bgra8Unorm
                or Format.Bgra8UnormSrgb
                or Format.R32Float
                or Format.Rg16Float => 4,
            Format.Rgba16Float => 8,
            _ => throw new InvalidOperationException($"Frame output verification does not support format {format}."),
        };

    private static void ReadRgb(ReadOnlySpan<byte> bytes, int offset, Format format, out double r, out double g, out double b)
    {
        switch (format)
        {
            case Format.Rgba8Unorm:
            case Format.Rgba8UnormSrgb:
                r = bytes[offset] / 255.0;
                g = bytes[offset + 1] / 255.0;
                b = bytes[offset + 2] / 255.0;
                return;
            case Format.Bgra8Unorm:
            case Format.Bgra8UnormSrgb:
                b = bytes[offset] / 255.0;
                g = bytes[offset + 1] / 255.0;
                r = bytes[offset + 2] / 255.0;
                return;
            case Format.Rgba16Float:
                r = HalfToDouble(bytes, offset);
                g = HalfToDouble(bytes, offset + 2);
                b = HalfToDouble(bytes, offset + 4);
                return;
            case Format.Rg16Float:
                r = HalfToDouble(bytes, offset);
                g = HalfToDouble(bytes, offset + 2);
                b = 0.0;
                return;
            case Format.R32Float:
                r = BitConverter.ToSingle(bytes.Slice(offset, sizeof(float)));
                g = 0.0;
                b = 0.0;
                return;
            default:
                throw new InvalidOperationException($"Frame output verification does not support format {format}.");
        }
    }

    private static double HalfToDouble(ReadOnlySpan<byte> bytes, int offset)
    {
        ushort bits = BitConverter.ToUInt16(bytes.Slice(offset, sizeof(ushort)));
        return (double)BitConverter.UInt16BitsToHalf(bits);
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        if (alignment == 0)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be non-zero.");

        ulong remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }
}
