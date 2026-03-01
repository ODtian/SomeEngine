using System;
using Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render.Data;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Systems;

public class InstanceSyncSystem : QuerySystem<TransformQvvs, MeshInstance>
{
    private GpuTransform[] _cpuTransforms;
    private GpuInstanceHeader[] _cpuHeaders;
    private int _capacity = 1024;

    public int Count { get; private set; }

    public Span<GpuTransform> CpuTransforms => _cpuTransforms.AsSpan(0, Count);
    public Span<GpuInstanceHeader> CpuHeaders => _cpuHeaders.AsSpan(0, Count);

    public InstanceSyncSystem()
    {
        _cpuTransforms = new GpuTransform[_capacity];
        _cpuHeaders = new GpuInstanceHeader[_capacity];
    }

    protected override void OnUpdate()
    {
        int count = Query.Count;
        EnsureCapacity(count);

        int index = 0;
        foreach (var (transforms, meshInstances, _) in Query.Chunks)
        {
            var tSpan = transforms.Span;
            var mSpan = meshInstances.Span;
            for (int i = 0; i < tSpan.Length; i++)
            {
                _cpuTransforms[index] = GpuTransform.FromQvvs(tSpan[i]);
                _cpuHeaders[index] = new GpuInstanceHeader
                {
                    BVHRootIndex = mSpan[i].BVHRootIndex,
                    MaterialID = 0,
                    MetadataOffset = 0,
                    MetadataCount = 0,
                };
                index++;
            }
        }

        Count = count;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed > _capacity)
        {
            while (_capacity < needed)
                _capacity *= 2;
            Array.Resize(ref _cpuTransforms, _capacity);
            Array.Resize(ref _cpuHeaders, _capacity);
        }
    }
}
