using System;
using SomeEngine.Render.Data;

namespace SomeEngine.Render.Systems;

public class InstanceDataManager
{
    private GpuTransform[] _cpuTransforms;
    private byte[] _cpuHeaders;
    private byte[] _cpuMetadata;
    private int _capacity = 1024;

    public int Count { get; private set; }
    public int MetadataByteCount { get; private set; }

    public Span<GpuTransform> CpuTransforms => _cpuTransforms.AsSpan(0, Count);
    public Span<byte> CpuHeaders => _cpuHeaders.AsSpan(0, Count * InstanceHeaderLayout.StrideBytes);
    public Span<byte> CpuMetadata => _cpuMetadata.AsSpan(0, MetadataByteCount);

    public InstanceDataManager()
    {
        _cpuTransforms = new GpuTransform[_capacity];
        _cpuHeaders = new byte[_capacity * InstanceHeaderLayout.StrideBytes];
        _cpuMetadata = new byte[_capacity * 32]; // Initial guess: 32 bytes per instance max on average
    }

    public void EnsureCapacity(int needed)
    {
        if (needed > _capacity)
        {
            while (_capacity < needed)
                _capacity *= 2;
            Array.Resize(ref _cpuTransforms, _capacity);
            Array.Resize(ref _cpuHeaders, _capacity * InstanceHeaderLayout.StrideBytes);
        }
    }

    public void UpdateCount(int count)
    {
        Count = count;
    }

    public void SetTransform(int index, GpuTransform transform)
    {
        _cpuTransforms[index] = transform;
    }

    public InstanceHeaderWriter GetHeaderWriter(int index)
        => new(_cpuHeaders.AsSpan(
            index * InstanceHeaderLayout.StrideBytes,
            InstanceHeaderLayout.StrideBytes));

    public ReadOnlySpan<byte> GetHeader(int index)
        => _cpuHeaders.AsSpan(
            index * InstanceHeaderLayout.StrideBytes,
            InstanceHeaderLayout.StrideBytes);

    public void ClearMetadata()
    {
        MetadataByteCount = 0;
    }

    public uint AppendMetadata<T>(ref T data) where T : unmanaged
    {
        int size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        if (MetadataByteCount + size > _cpuMetadata.Length)
        {
            Array.Resize(ref _cpuMetadata, Math.Max(_cpuMetadata.Length * 2, MetadataByteCount + size));
        }

        System.Runtime.InteropServices.MemoryMarshal.Write(_cpuMetadata.AsSpan(MetadataByteCount), in data);
        
        uint offset = (uint)MetadataByteCount;
        MetadataByteCount += size;
        return offset;
    }
}
