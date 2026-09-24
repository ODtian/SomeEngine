using System.Runtime.InteropServices;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal sealed class PageFaults
{
    public const uint MaxCount = 4096;
    public const uint ByteCount = 4u + (MaxCount * 4u);

    private uint[] _nodes = [];

    public ReadOnlySpan<uint> Read(ReadOnlySpan<byte> bytes, uint maxCount)
    {
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(bytes);
        if (words.Length == 0 || maxCount == 0)
            return [];

        uint count = Math.Min(words[0], maxCount);
        count = Math.Min(count, checked((uint)Math.Max(words.Length - 1, 0)));
        if (count == 0)
            return [];

        Ensure(checked((int)count));
        words.Slice(1, checked((int)count)).CopyTo(_nodes);
        return _nodes.AsSpan(0, checked((int)count));
    }

    public ReadOnlySpan<uint> Read(
        IDevice device,
        BufferHandle readback,
        int byteCount,
        uint maxCount)
    {
        ArgumentNullException.ThrowIfNull(device);
        bool mapped = false;
        try
        {
            Memory<byte> bytes = device.MapBuffer(readback, MapMode.Read, 0, byteCount);
            mapped = true;
            return Read(bytes.Span, maxCount);
        }
        finally
        {
            if (mapped)
                device.UnmapBuffer(readback);
        }
    }

    private void Ensure(int count)
    {
        if (_nodes.Length < count)
            Array.Resize(ref _nodes, count);
    }
}
