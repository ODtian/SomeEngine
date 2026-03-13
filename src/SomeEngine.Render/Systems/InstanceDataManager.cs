using System;
using SomeEngine.Render.Data;

namespace SomeEngine.Render.Systems;

public class InstanceDataManager
{
    private GpuTransform[] _cpuTransforms;
    private GpuInstanceHeader[] _cpuHeaders;
    private int _capacity = 1024;

    public int Count { get; private set; }

    public Span<GpuTransform> CpuTransforms => _cpuTransforms.AsSpan(0, Count);
    public Span<GpuInstanceHeader> CpuHeaders => _cpuHeaders.AsSpan(0, Count);

    public InstanceDataManager()
    {
        _cpuTransforms = new GpuTransform[_capacity];
        _cpuHeaders = new GpuInstanceHeader[_capacity];
    }

    public void EnsureCapacity(int needed)
    {
        if (needed > _capacity)
        {
            while (_capacity < needed)
                _capacity *= 2;
            Array.Resize(ref _cpuTransforms, _capacity);
            Array.Resize(ref _cpuHeaders, _capacity);
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

    public void SetHeader(int index, GpuInstanceHeader header)
    {
        _cpuHeaders[index] = header;
    }
}
