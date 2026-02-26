using System;
using System.Collections.Generic;
using Diligent;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Graph;

public class RenderGraph : IDisposable
{
    private readonly List<RenderPass> _passes = [];
    private readonly List<RGResource> _resources = [];
    private readonly Dictionary<string, RGResourceHandle> _resourceMap = [];

    // Pass metadata
    private class PassMetadata
    {
        public bool IsActive = true;
        public List<(RGResourceHandle Handle, ResourceState State)> Reads = [];
        public List<(RGResourceHandle Handle, ResourceState State)> Writes = [];
    }

    private readonly Dictionary<RenderPass, PassMetadata> _passMetadata = [];

    // Dependency tracking (simplified for now)
    private readonly HashSet<RGResourceHandle> _compiledResources = [];

    private readonly List<RGMemoryHeap> _memoryHeaps = [];

    public void AddPass(RenderPass pass)
    {
        _passes.Add(pass);
        _passMetadata[pass] = new PassMetadata();
    }

    public void AddPass<TData>(
        string name,
        Action<RenderGraphBuilder, TData> setup,
        Action<RenderGraphContext, TData> execute
    )
        where TData : class, new()
    {
        var pass = new LambdaRenderPass<TData>(name, new TData(), setup, execute);
        AddPass(pass);
    }

    public RGResourceHandle CreateTexture(string name, TextureDesc desc)
    {
        var handle = new RGResourceHandle { Id = _resources.Count, Version = 0 };
        var tex = new RGTexture(name, desc) { Handle = handle };
        _resources.Add(tex);
        _resourceMap[name] = handle;
        return handle;
    }

    public RGResourceHandle CreateBuffer(string name, BufferDesc desc)
    {
        var handle = new RGResourceHandle { Id = _resources.Count, Version = 0 };
        var buf = new RGBuffer(name, desc) { Handle = handle };
        _resources.Add(buf);
        _resourceMap[name] = handle;
        return handle;
    }

    public void MarkAsOutput(RGResourceHandle handle)
    {
        _compiledResources.Add(handle);
    }

    public RGResourceHandle ImportTexture(
        string name,
        ITexture texture,
        ResourceState initialState = ResourceState.Undefined,
        ITextureView? view = null
    )
    {
        var handle = new RGResourceHandle { Id = _resources.Count, Version = 0 };
        var desc = texture.GetDesc();
        var tex = new RGTexture(name, desc)
        {
            Handle = handle,
            InternalTexture = texture,
            InternalView = view,
            IsImported = true,
            InitialState = initialState,
            CurrentState = initialState,
        };
        _resources.Add(tex);
        _resourceMap[name] = handle;
        return handle;
    }

    public RGResourceHandle ImportBuffer(
        string name,
        IBuffer buffer,
        ResourceState initialState = ResourceState.Undefined
    )
    {
        var handle = new RGResourceHandle { Id = _resources.Count, Version = 0 };
        var desc = buffer.GetDesc();
        var buf = new RGBuffer(name, desc)
        {
            Handle = handle,
            InternalBuffer = buffer,
            IsImported = true,
            InitialState = initialState,
            CurrentState = initialState,
        };
        _resources.Add(buf);
        _resourceMap[name] = handle;
        return handle;
    }

    public RGResourceHandle GetResourceHandle(string name)
    {
        if (_resourceMap.TryGetValue(name, out var handle))
            return handle;
        return RGResourceHandle.Invalid;
    }

    public delegate MemoryRequirements GetMemoryRequirementsDelegate(RGResource resource);

    public void Compile(
        IRenderDevice? device = null,
        GetMemoryRequirementsDelegate? getMemoryReqs = null
    )
    {
        // 1. Reset
        _compiledResources.Clear();

        // 2. Setup passes
        foreach (var pass in _passes)
        {
            var builder = new RenderGraphBuilder(this, pass);
            pass.Setup(builder);
        }

        // 3. Calculate Resource Lifetimes
        var firstPass = new Dictionary<int, int>();
        var lastPass = new Dictionary<int, int>();

        for (int i = 0; i < _passes.Count; i++)
        {
            var pass = _passes[i];
            if (!_passMetadata.TryGetValue(pass, out var meta) || !meta.IsActive)
                continue;

            foreach (var (handle, reqState) in meta.Reads)
            {
                if (!firstPass.ContainsKey(handle.Id))
                    firstPass[handle.Id] = i;
                lastPass[handle.Id] = i;
            }
            foreach (var (handle, reqState) in meta.Writes)
            {
                if (!firstPass.ContainsKey(handle.Id))
                    firstPass[handle.Id] = i;
                lastPass[handle.Id] = i;
            }
        }

        // 4. Placed Resource Aliasing Allocation
        if (device != null || getMemoryReqs != null)
        {
            AllocateMemoryHeaps(device, getMemoryReqs, firstPass, lastPass);
        }
    }

    private void AllocateMemoryHeaps(
        IRenderDevice? device,
        GetMemoryRequirementsDelegate? getMemoryReqs,
        Dictionary<int, int> firstPass,
        Dictionary<int, int> lastPass
    )
    {
        foreach (var heap in _memoryHeaps)
        {
            heap.Reset();
        }

        // Collect transient resources to allocate
        var transientResources = new List<RGResource>();
        foreach (var res in _resources)
        {
            if (res.IsImported)
                continue;
            if (!firstPass.ContainsKey(res.Handle.Id))
                continue; // Dead resource

            // Query memory requirements
            MemoryRequirements reqs = default;
            if (getMemoryReqs != null)
            {
                reqs = getMemoryReqs(res);
            }
            else if (device != null)
            {
                if (res is RGTexture tex)
                {
                    reqs = device.GetTextureMemoryRequirements(tex.Desc);
                }
                else if (res is RGBuffer buf)
                {
                    reqs = device.GetBufferMemoryRequirements(buf.Desc);
                }
                else
                    continue;
            }
            else
                continue;

            res.MemorySize = reqs.Size;
            res.MemoryAlignment = reqs.Alignment;
            res.HeapIndex = -1;
            res.MemoryOffset = ulong.MaxValue;

            transientResources.Add(res);
        }

        // Greedily allocate: largest resources first
        transientResources.Sort((a, b) => b.MemorySize.CompareTo(a.MemorySize));

        foreach (var res in transientResources)
        {
            bool allocated = false;
            int first = firstPass[res.Handle.Id];
            int last = lastPass[res.Handle.Id];

            // Try existing heaps
            for (int i = 0; i < _memoryHeaps.Capacity && i < _memoryHeaps.Count; i++)
            {
                if (
                    _memoryHeaps[i]
                        .TryAllocate(
                            res.MemorySize,
                            res.MemoryAlignment,
                            res.Handle.Id,
                            first,
                            last,
                            out ulong offset
                        )
                )
                {
                    res.HeapIndex = i;
                    res.MemoryOffset = offset;
                    allocated = true;
                    break;
                }
            }

            // Create new heap if needed
            if (!allocated)
            {
                // Simple heuristic: 64MB min, up to the resource size
                ulong minHeapSize = 64ul * 1024 * 1024;
                ulong heapCapacity = Math.Max(minHeapSize, res.MemorySize + res.MemoryAlignment);

                int heapIdx = _memoryHeaps.Count;
                var newHeap =
                    device != null
                        ? new RGMemoryHeap(device, heapCapacity)
                        : new RGMemoryHeap(heapCapacity);
                _memoryHeaps.Add(newHeap);

                if (
                    newHeap.TryAllocate(
                        res.MemorySize,
                        res.MemoryAlignment,
                        res.Handle.Id,
                        first,
                        last,
                        out ulong offset
                    )
                )
                {
                    res.HeapIndex = heapIdx;
                    res.MemoryOffset = offset;
                }
            }
        }
    }

    public void Execute(RenderContext context)
    {
        var rgContext = new RenderGraphContext(this, context);

        // Create/Ensure physical resources exist
        foreach (var res in _resources)
        {
            if (res.IsImported)
                continue;

            if (res.HeapIndex >= 0)
            {
                // Use Placed resource from memory heap
                var heap = _memoryHeaps[res.HeapIndex];
                if (res is RGTexture tex && tex.InternalTexture == null)
                    tex.InternalTexture = context.Device?.CreatePlacedTexture(
                        tex.Desc,
                        heap.Memory,
                        res.MemoryOffset
                    );
                else if (res is RGBuffer buf && buf.InternalBuffer == null)
                    buf.InternalBuffer = context.Device?.CreatePlacedBuffer(
                        buf.Desc,
                        heap.Memory,
                        res.MemoryOffset
                    );
            }
            else
            {
                // Fallback: standard resource creation (can be replaced with pool later)
                if (res is RGTexture tex && tex.InternalTexture == null)
                    tex.InternalTexture = context.Device?.CreateTexture(tex.Desc, null);
                else if (res is RGBuffer buf && buf.InternalBuffer == null)
                    buf.InternalBuffer = context.Device?.CreateBuffer(buf.Desc, null);
            }

            if (res is RGTexture || res is RGBuffer)
            {
                res.CurrentState = ResourceState.Undefined; // Newly created typically undefined
            }
        }

        foreach (var pass in _passes)
        {
            if (!_passMetadata.TryGetValue(pass, out var meta) || !meta.IsActive)
                continue;

            // State transitioning should be handled in individual passes or through an upcoming Auto-Barrier subsystem.
            // Currently, we just update the tracked state.
            foreach (var (handle, reqState) in meta.Reads)
            {
                var res = _resources[handle.Id];
                res.CurrentState = reqState;
            }
            foreach (var (handle, reqState) in meta.Writes)
            {
                var res = _resources[handle.Id];
                res.CurrentState = reqState;
            }

            // Execute pass
            pass.Execute(context, rgContext);
        }
    }

    internal RGResourceHandle RegisterResourceRead(
        RGResourceHandle handle,
        RenderPass pass,
        ResourceState state
    )
    {
        if (_passMetadata.TryGetValue(pass, out var meta))
        {
            meta.Reads.Add((handle, state));
        }
        return handle;
    }

    internal RGResourceHandle RegisterResourceWrite(
        RGResourceHandle handle,
        RenderPass pass,
        ResourceState state
    )
    {
        if (_passMetadata.TryGetValue(pass, out var meta))
        {
            meta.Writes.Add((handle, state));
        }
        return handle;
    }

    internal ITexture? GetPhysicalTexture(RGResourceHandle handle)
    {
        if (handle.Id >= 0 && handle.Id < _resources.Count)
        {
            var res = _resources[handle.Id] as RGTexture;
            return res?.InternalTexture;
        }
        return null;
    }

    internal IBuffer? GetPhysicalBuffer(RGResourceHandle handle)
    {
        if (handle.Id >= 0 && handle.Id < _resources.Count)
        {
            var res = _resources[handle.Id] as RGBuffer;
            return res?.InternalBuffer;
        }
        return null;
    }

    internal ITextureView? GetPhysicalTextureView(RGResourceHandle handle, TextureViewType type)
    {
        if (handle.Id >= 0 && handle.Id < _resources.Count)
        {
            var res = _resources[handle.Id] as RGTexture;
            if (
                res != null
                && res.InternalView != null
                && res.InternalView.GetDesc().ViewType == type
            )
                return res.InternalView;

            return res?.InternalTexture?.GetDefaultView(type);
        }
        return null;
    }

    internal IBufferView? GetPhysicalBufferView(RGResourceHandle handle, BufferViewType type)
    {
        if (handle.Id >= 0 && handle.Id < _resources.Count)
        {
            var res = _resources[handle.Id] as RGBuffer;
            return res?.InternalBuffer?.GetDefaultView(type);
        }
        return null;
    }

    public void Reset()
    {
        _passes.Clear();
        _resources.Clear();
        _resourceMap.Clear();
        _compiledResources.Clear();
        _passMetadata.Clear();

        foreach (var heap in _memoryHeaps)
        {
            heap.Reset();
        }
    }

    public void Dispose()
    {
        foreach (var res in _resources)
        {
            if (!res.IsImported)
            {
                if (res is RGTexture tex)
                    tex.InternalTexture?.Dispose();
                if (res is RGBuffer buf)
                    buf.InternalBuffer?.Dispose();
            }
        }
        _resources.Clear();
        _passes.Clear();
        _resourceMap.Clear();

        foreach (var heap in _memoryHeaps)
        {
            heap.Dispose();
        }
        _memoryHeaps.Clear();
    }
}
