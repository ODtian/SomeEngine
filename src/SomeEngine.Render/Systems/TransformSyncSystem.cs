using System;
using System.Collections.Generic;
using Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using SomeEngine.Core.Math;
using SomeEngine.Render.Data;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Systems;

public class TransformSyncSystem : QuerySystem<TransformQvvs>
{
    private readonly RenderContext _renderContext;
    private IBuffer? _gpuBuffer; // The StructuredBuffer on GPU
    private GpuTransform[] _cpuBuffer; // Shadow copy for updates
    private int _capacity = 1024;
    private bool _dirty = false;
    
    // Mapping from Entity ID to Index in buffer
    // For now, we might just compact it every frame or use a component to store index
    // Using a component 'GpuIndex' is better for stability.
    // However, if we want to "upload only changes", we need a persistent mapping.
    
    // Let's use a simple strategy: Growable list.
    // Entities need to know their index.
    // We can add a component 'GpuTransformIndex'.

    public IBuffer? GlobalTransformBuffer => _gpuBuffer;
    public int Count { get; private set; } = 0;

    public TransformSyncSystem(RenderContext renderContext)
    {
        _renderContext = renderContext; // We might need to context to create buffers
        _cpuBuffer = new GpuTransform[_capacity];
    }

    protected override void OnUpdate()
    {
        // Simple full sync for phase 1.
        // In phase 2 we will optimize this.
        
        // 1. Collect all transforms
        // We can access the chunks directly for speed?
        // Query.Chunks...
        
        int count = Query.Count;
        EnsureCapacity(count);
        
        int index = 0;
        foreach (var (components, _) in Query.Chunks)
        {
            var transforms = components.Span;
            for (int i = 0; i < transforms.Length; i++)
            {
                _cpuBuffer[index++] = GpuTransform.FromQvvs(transforms[i]);
            }
        }
        
        Count = count;
        
        // 2. Upload to GPU
        // If count > 0
        if (Count > 0)
        {
            UploadBuffer();
        }
    }

    private void EnsureCapacity(int needed)
    {
        if (needed > _capacity)
        {
            while (_capacity < needed) _capacity *= 2;
            Array.Resize(ref _cpuBuffer, _capacity);
            
            // Re-create GPU buffer
            CreateBuffer(_capacity);
        }
        else if (_gpuBuffer == null)
        {
            CreateBuffer(_capacity);
        }
    }

    private void CreateBuffer(int size)
    {
        _gpuBuffer?.Dispose();
        
        BufferDesc desc = new BufferDesc
        {
            Name = "Global Transform Buffer",
            Size = (ulong)(size * GpuTransform.SizeInBytes),
            Usage = Usage.Default,
            BindFlags = BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = GpuTransform.SizeInBytes
        };
        
        _gpuBuffer = _renderContext.Device?.CreateBuffer(desc);
    }

    private unsafe void UploadBuffer()
    {
        if (_gpuBuffer == null || _renderContext.ImmediateContext == null) return;

        // Using UpdateBuffer for now. 
        // For large buffers, Map/Unmap (Dynamic) or Staging Buffer is better.
        // Start with UpdateBuffer for simplicity.
        
        uint dataSize = (uint)(Count * GpuTransform.SizeInBytes);
        
        fixed (GpuTransform* pData = _cpuBuffer)
        {
            _renderContext.ImmediateContext.UpdateBuffer(_gpuBuffer, 0, dataSize, (IntPtr)pData, ResourceStateTransitionMode.Transition);
        }
    }

    public void Dispose()
    {
        _gpuBuffer?.Dispose();
    }
}
