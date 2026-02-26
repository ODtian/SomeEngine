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
    private readonly RenderContext _renderContext;

    private IBuffer? _transformBuffer;
    private IBuffer? _headerBuffer;
    private GpuTransform[] _cpuTransforms;
    private GpuInstanceHeader[] _cpuHeaders;
    private int _capacity = 1024;

    public IBuffer? GlobalTransformBuffer => _transformBuffer;
    public IBuffer? GlobalInstanceHeaderBuffer => _headerBuffer;
    public int Count { get; private set; }

    public InstanceSyncSystem(RenderContext renderContext)
    {
        _renderContext = renderContext;
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

        if (Count > 0)
            UploadBuffers();
    }

    private void EnsureCapacity(int needed)
    {
        if (needed > _capacity)
        {
            while (_capacity < needed)
                _capacity *= 2;
            Array.Resize(ref _cpuTransforms, _capacity);
            Array.Resize(ref _cpuHeaders, _capacity);
            CreateBuffers(_capacity);
        }
        else if (_transformBuffer == null)
        {
            CreateBuffers(_capacity);
        }
    }

    private void CreateBuffers(int size)
    {
        _transformBuffer?.Dispose();
        _headerBuffer?.Dispose();

        _transformBuffer = _renderContext.Device?.CreateBuffer(
            new BufferDesc
            {
                Name = "Global Transform Buffer",
                Size = (ulong)(size * GpuTransform.SizeInBytes),
                Usage = Usage.Default,
                BindFlags = BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = GpuTransform.SizeInBytes,
            }
        );

        _headerBuffer = _renderContext.Device?.CreateBuffer(
            new BufferDesc
            {
                Name = "Global Instance Header Buffer",
                Size = (ulong)(size * GpuInstanceHeader.SizeInBytes),
                Usage = Usage.Default,
                BindFlags = BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = GpuInstanceHeader.SizeInBytes,
            }
        );
    }

    private unsafe void UploadBuffers()
    {
        if (
            _transformBuffer == null
            || _headerBuffer == null
            || _renderContext.ImmediateContext == null
        )
            return;

        uint transformSize = (uint)(Count * GpuTransform.SizeInBytes);
        fixed (GpuTransform* pT = _cpuTransforms)
        {
            _renderContext.ImmediateContext.UpdateBuffer(
                _transformBuffer,
                0,
                transformSize,
                (IntPtr)pT,
                ResourceStateTransitionMode.Transition
            );
        }

        uint headerSize = (uint)(Count * GpuInstanceHeader.SizeInBytes);
        fixed (GpuInstanceHeader* pH = _cpuHeaders)
        {
            _renderContext.ImmediateContext.UpdateBuffer(
                _headerBuffer,
                0,
                headerSize,
                (IntPtr)pH,
                ResourceStateTransitionMode.Transition
            );
        }
    }

    public void Dispose()
    {
        _transformBuffer?.Dispose();
        _headerBuffer?.Dispose();
    }
}
