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
    private readonly List<TextureExtractionRequest> _textureExtractionQueue = [];
    private readonly List<BufferExtractionRequest> _bufferExtractionQueue = [];

    // Pass metadata
    private class PassMetadata
    {
        public bool IsActive = true;
        public List<(RGResourceHandle Handle, ResourceState State)> Reads = [];
        public List<(RGResourceHandle Handle, ResourceState State)> Writes = [];
    }

    private readonly struct CompiledBarrier
    {
        public CompiledBarrier(RGResourceHandle handle, ResourceState oldState, ResourceState newState)
        {
            Handle = handle;
            OldState = oldState;
            NewState = newState;
        }

        public RGResourceHandle Handle { get; }
        public ResourceState OldState { get; }
        public ResourceState NewState { get; }
    }

    private class CompiledPass
    {
        public CompiledPass(RenderPass pass, int originalIndex)
        {
            Pass = pass;
            OriginalIndex = originalIndex;
        }

        public RenderPass Pass { get; }
        public int OriginalIndex { get; }
        public bool Active { get; set; }
        public List<CompiledBarrier> PreBarriers { get; } = [];
        public List<(RGResourceHandle Handle, ResourceState State)> RequiredStates { get; } = [];
    }

    private readonly Dictionary<RenderPass, PassMetadata> _passMetadata = [];
    private readonly HashSet<int> _markedOutputResources = [];
    private readonly List<CompiledPass> _compiledPasses = [];
    private readonly List<int> _executionOrder = [];
    private readonly HashSet<int> _activeResourceIds = [];

    private readonly List<RGMemoryHeap> _memoryHeaps = [];

    private readonly struct TextureExtractionRequest
    {
        public TextureExtractionRequest(RGResourceHandle handle, Action<ITexture?> assign)
        {
            Handle = handle;
            Assign = assign;
        }

        public RGResourceHandle Handle { get; }
        public Action<ITexture?> Assign { get; }
    }

    private readonly struct BufferExtractionRequest
    {
        public BufferExtractionRequest(RGResourceHandle handle, Action<IBuffer?> assign)
        {
            Handle = handle;
            Assign = assign;
        }

        public RGResourceHandle Handle { get; }
        public Action<IBuffer?> Assign { get; }
    }

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
        if (!handle.IsValid)
            return;

        _markedOutputResources.Add(handle.Id);
    }

    public RGResourceHandle ImportTexture(
        string name,
        ITexture texture,
        ResourceState initialState = ResourceState.Unknown,
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

    public RGResourceHandle RegisterExternalTexture(
        string name,
        ITexture texture,
        ResourceState initialState = ResourceState.Unknown,
        ITextureView? view = null
    )
    {
        var handle = ImportTexture(name, texture, initialState, view);
        if (handle.IsValid)
        {
            _resources[handle.Id].IsExternal = true;
        }

        return handle;
    }

    public RGResourceHandle ImportBuffer(
        string name,
        IBuffer buffer,
        ResourceState initialState = ResourceState.Unknown
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

    public RGResourceHandle RegisterExternalBuffer(
        string name,
        IBuffer buffer,
        ResourceState initialState = ResourceState.Unknown
    )
    {
        var handle = ImportBuffer(name, buffer, initialState);
        if (handle.IsValid)
        {
            _resources[handle.Id].IsExternal = true;
        }

        return handle;
    }

    public void QueueTextureExtraction(RGResourceHandle handle, Action<ITexture?> assign)
    {
        ArgumentNullException.ThrowIfNull(assign);

        if (!handle.IsValid)
        {
            assign(null);
            return;
        }

        _textureExtractionQueue.Add(new TextureExtractionRequest(handle, assign));
    }

    public void QueueBufferExtraction(RGResourceHandle handle, Action<IBuffer?> assign)
    {
        ArgumentNullException.ThrowIfNull(assign);

        if (!handle.IsValid)
        {
            assign(null);
            return;
        }

        _bufferExtractionQueue.Add(new BufferExtractionRequest(handle, assign));
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
        PrepareForCompile();

        foreach (var pass in _passes)
        {
            var builder = new RenderGraphBuilder(this, pass);
            pass.Setup(builder);
        }

        var sinkResources = CollectSinkResources();
        var producerPassByResource = BuildProducerPassLookup();
        var activePasses = BuildReachablePassSet(sinkResources, producerPassByResource);

        BuildExecutionOrder(activePasses);
        BuildAutomaticBarriersAndTrackedStates();

        var firstPass = new Dictionary<int, int>();
        var lastPass = new Dictionary<int, int>();

        _activeResourceIds.Clear();

        for (int executionIndex = 0; executionIndex < _executionOrder.Count; executionIndex++)
        {
            int passIndex = _executionOrder[executionIndex];
            var compiledPass = _compiledPasses[passIndex];
            if (!compiledPass.Active)
                continue;

            if (!_passMetadata.TryGetValue(compiledPass.Pass, out var meta))
                continue;

            foreach (var (handle, _) in meta.Reads)
            {
                if (!handle.IsValid)
                    continue;

                _activeResourceIds.Add(handle.Id);
                if (!firstPass.ContainsKey(handle.Id))
                    firstPass[handle.Id] = executionIndex;
                lastPass[handle.Id] = executionIndex;
            }

            foreach (var (handle, _) in meta.Writes)
            {
                if (!handle.IsValid)
                    continue;

                _activeResourceIds.Add(handle.Id);
                if (!firstPass.ContainsKey(handle.Id))
                    firstPass[handle.Id] = executionIndex;
                lastPass[handle.Id] = executionIndex;
            }
        }

        if (device != null || getMemoryReqs != null)
        {
            AllocateMemoryHeaps(device, getMemoryReqs, firstPass, lastPass);
        }
    }

    private void PrepareForCompile()
    {
        _compiledPasses.Clear();
        _executionOrder.Clear();

        for (int i = 0; i < _passes.Count; i++)
        {
            _compiledPasses.Add(new CompiledPass(_passes[i], i));
        }

        foreach (var meta in _passMetadata.Values)
        {
            meta.IsActive = true;
            meta.Reads.Clear();
            meta.Writes.Clear();
        }
    }

    private HashSet<int> CollectSinkResources()
    {
        var sinkResources = new HashSet<int>(_markedOutputResources);

        foreach (var request in _textureExtractionQueue)
        {
            if (request.Handle.IsValid)
                sinkResources.Add(request.Handle.Id);
        }

        foreach (var request in _bufferExtractionQueue)
        {
            if (request.Handle.IsValid)
                sinkResources.Add(request.Handle.Id);
        }

        return sinkResources;
    }

    private Dictionary<int, List<int>> BuildProducerPassLookup()
    {
        var producerPassByResource = new Dictionary<int, List<int>>();

        for (int passIndex = 0; passIndex < _passes.Count; passIndex++)
        {
            var pass = _passes[passIndex];
            if (!_passMetadata.TryGetValue(pass, out var meta) || !meta.IsActive)
                continue;

            foreach (var (handle, _) in meta.Writes)
            {
                if (!handle.IsValid)
                    continue;

                if (!producerPassByResource.TryGetValue(handle.Id, out var producers))
                {
                    producers = new List<int>();
                    producerPassByResource[handle.Id] = producers;
                }
                producers.Add(passIndex);
            }
        }

        return producerPassByResource;
    }

    private HashSet<int> BuildReachablePassSet(
        HashSet<int> sinkResources,
        Dictionary<int, List<int>> producerPassByResource
    )
    {
        var reachablePasses = new HashSet<int>();

        if (sinkResources.Count == 0)
        {
            for (int passIndex = 0; passIndex < _passes.Count; passIndex++)
            {
                var pass = _passes[passIndex];
                if (_passMetadata.TryGetValue(pass, out var meta) && meta.IsActive)
                {
                    reachablePasses.Add(passIndex);
                }
            }

            return reachablePasses;
        }

        var pendingResources = new Queue<int>();
        foreach (int resourceId in sinkResources)
        {
            pendingResources.Enqueue(resourceId);
        }

        while (pendingResources.Count > 0)
        {
            int resourceId = pendingResources.Dequeue();
            if (!producerPassByResource.TryGetValue(resourceId, out var producers))
                continue;

            foreach (int producerPass in producers)
            {
                if (!reachablePasses.Add(producerPass))
                    continue;

                var pass = _passes[producerPass];
                if (!_passMetadata.TryGetValue(pass, out var meta))
                    continue;

                foreach (var (handle, _) in meta.Reads)
                {
                    if (handle.IsValid)
                        pendingResources.Enqueue(handle.Id);
                }
            }
        }

        return reachablePasses;
    }

    private void BuildExecutionOrder(HashSet<int> activePasses)
    {
        _executionOrder.Clear();

        for (int i = 0; i < _compiledPasses.Count; i++)
        {
            bool isActive = activePasses.Contains(i);
            _compiledPasses[i].Active = isActive;

            if (_passMetadata.TryGetValue(_compiledPasses[i].Pass, out var meta))
            {
                meta.IsActive = isActive;
            }
        }

        if (activePasses.Count == 0)
            return;

        var indegree = new Dictionary<int, int>();
        var outgoingEdges = new Dictionary<int, HashSet<int>>();

        foreach (int passIndex in activePasses)
        {
            indegree[passIndex] = 0;
            outgoingEdges[passIndex] = [];
        }

        var lastWriterPassByResource = new Dictionary<int, int>();

        for (int passIndex = 0; passIndex < _passes.Count; passIndex++)
        {
            if (!activePasses.Contains(passIndex))
                continue;

            var pass = _passes[passIndex];
            if (!_passMetadata.TryGetValue(pass, out var meta) || !meta.IsActive)
                continue;

            foreach (var (handle, _) in meta.Reads)
            {
                if (!handle.IsValid)
                    continue;

                if (lastWriterPassByResource.TryGetValue(handle.Id, out int producerPass))
                {
                    AddDependencyEdge(producerPass, passIndex, indegree, outgoingEdges);
                }
            }

            foreach (var (handle, _) in meta.Writes)
            {
                if (!handle.IsValid)
                    continue;

                if (lastWriterPassByResource.TryGetValue(handle.Id, out int producerPass))
                {
                    AddDependencyEdge(producerPass, passIndex, indegree, outgoingEdges);
                }

                lastWriterPassByResource[handle.Id] = passIndex;
            }
        }

        var ready = new PriorityQueue<int, int>();
        foreach (var (passIndex, degree) in indegree)
        {
            if (degree == 0)
            {
                ready.Enqueue(passIndex, _compiledPasses[passIndex].OriginalIndex);
            }
        }

        while (ready.TryDequeue(out int passIndex, out _))
        {
            _executionOrder.Add(passIndex);

            if (!outgoingEdges.TryGetValue(passIndex, out var nextPasses))
                continue;

            foreach (int nextPass in nextPasses)
            {
                int newDegree = indegree[nextPass] - 1;
                indegree[nextPass] = newDegree;
                if (newDegree == 0)
                {
                    ready.Enqueue(nextPass, _compiledPasses[nextPass].OriginalIndex);
                }
            }
        }

        if (_executionOrder.Count == activePasses.Count)
            return;

        _executionOrder.Clear();
        for (int passIndex = 0; passIndex < _passes.Count; passIndex++)
        {
            if (activePasses.Contains(passIndex))
            {
                _executionOrder.Add(passIndex);
            }
        }
    }

    private static void AddDependencyEdge(
        int fromPass,
        int toPass,
        Dictionary<int, int> indegree,
        Dictionary<int, HashSet<int>> outgoingEdges
    )
    {
        if (fromPass == toPass)
            return;

        if (!outgoingEdges.TryGetValue(fromPass, out var edges))
            return;

        if (edges.Add(toPass))
        {
            indegree[toPass] = indegree[toPass] + 1;
        }
    }

    private void BuildAutomaticBarriersAndTrackedStates()
    {
        var trackedStateByResource = new Dictionary<int, ResourceState>(_resources.Count);
        foreach (var resource in _resources)
        {
            ResourceState initialState = resource.IsImported
                ? resource.InitialState
                : ResourceState.Unknown;
            trackedStateByResource[resource.Handle.Id] = initialState;
            resource.CurrentState = initialState;
        }

        foreach (int passIndex in _executionOrder)
        {
            var compiledPass = _compiledPasses[passIndex];
            compiledPass.PreBarriers.Clear();
            compiledPass.RequiredStates.Clear();

            if (!_passMetadata.TryGetValue(compiledPass.Pass, out var meta) || !meta.IsActive)
                continue;

            var requiredStateByResource = new Dictionary<int, (RGResourceHandle Handle, ResourceState State)>();

            foreach (var (handle, state) in meta.Writes)
            {
                if (!handle.IsValid)
                    continue;

                if (requiredStateByResource.TryGetValue(handle.Id, out var existing))
                {
                    requiredStateByResource[handle.Id] = (handle, existing.State | state);
                }
                else
                {
                    requiredStateByResource[handle.Id] = (handle, state);
                }
            }

            foreach (var (handle, state) in meta.Reads)
            {
                if (!handle.IsValid)
                    continue;

                if (requiredStateByResource.TryGetValue(handle.Id, out var existing))
                {
                    // Generally shouldn't mix read and write states, but if they do, OR them (e.g. DepthRead | DepthWrite)
                    requiredStateByResource[handle.Id] = (handle, existing.State | state);
                }
                else
                {
                    requiredStateByResource[handle.Id] = (handle, state);
                }
            }

            if (requiredStateByResource.Count == 0)
                continue;

            var sortedResourceIds = new List<int>(requiredStateByResource.Keys);
            sortedResourceIds.Sort();

            foreach (int resourceId in sortedResourceIds)
            {
                var required = requiredStateByResource[resourceId];
                ResourceState oldState = trackedStateByResource.TryGetValue(resourceId, out var tracked)
                    ? tracked
                    : ResourceState.Unknown;

                if (oldState != required.State || (oldState == ResourceState.UnorderedAccess && required.State == ResourceState.UnorderedAccess))
                {
                    compiledPass.PreBarriers.Add(
                        new CompiledBarrier(required.Handle, oldState, required.State)
                    );
                }

                compiledPass.RequiredStates.Add((required.Handle, required.State));
                trackedStateByResource[resourceId] = required.State;
            }
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

            bool requiresCpuAccessibleMemory = false;
            if (res is RGBuffer cpuBuf)
            {
                requiresCpuAccessibleMemory =
                    cpuBuf.Desc.Usage == Usage.Dynamic
                    || cpuBuf.Desc.Usage == Usage.Staging
                    || cpuBuf.Desc.CPUAccessFlags != CpuAccessFlags.None;
            }
            else if (res is RGTexture cpuTex)
            {
                requiresCpuAccessibleMemory =
                    cpuTex.Desc.Usage == Usage.Dynamic
                    || cpuTex.Desc.Usage == Usage.Staging
                    || cpuTex.Desc.CPUAccessFlags != CpuAccessFlags.None;
            }

            if (requiresCpuAccessibleMemory)
            {
                res.HeapIndex = -1;
                res.MemoryOffset = ulong.MaxValue;
                continue;
            }

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
        var deviceContext = context.ImmediateContext;

        if (_compiledPasses.Count == 0 || _executionOrder.Count == 0)
        {
            ResolveExtractions();
            return;
        }

        foreach (var res in _resources)
        {
            if (!_activeResourceIds.Contains(res.Handle.Id))
                continue;

            if (res.IsImported)
            {
                res.CurrentState = res.InitialState;
                continue;
            }

            if (res.HeapIndex >= 0)
            {
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
                if (res is RGTexture tex && tex.InternalTexture == null)
                    tex.InternalTexture = context.Device?.CreateTexture(tex.Desc, null);
                else if (res is RGBuffer buf && buf.InternalBuffer == null)
                    buf.InternalBuffer = context.Device?.CreateBuffer(buf.Desc, null);
            }

            res.CurrentState = ResourceState.Unknown;
        }

        foreach (int passIndex in _executionOrder)
        {
            var compiledPass = _compiledPasses[passIndex];
            if (!compiledPass.Active)
                continue;

            if (deviceContext != null && compiledPass.PreBarriers.Count > 0)
            {
                var transitions = new List<StateTransitionDesc>();
                var seenResources = new HashSet<IDeviceObject>();

                foreach (var barrier in compiledPass.PreBarriers)
                {
                    if (barrier.Handle.Id < 0 || barrier.Handle.Id >= _resources.Count)
                        continue;

                    var resource = _resources[barrier.Handle.Id];
                    IDeviceObject? deviceObj = null;

                    if (resource is RGTexture texture && texture.InternalTexture != null)
                    {
                        deviceObj = texture.InternalTexture;
                    }
                    else if (resource is RGBuffer buffer && buffer.InternalBuffer != null)
                    {
                        deviceObj = buffer.InternalBuffer;
                    }

                    if (deviceObj != null)
                    {
                        if (!seenResources.Add(deviceObj))
                        {
                            Console.WriteLine($"[RenderGraph] WARNING: Duplicate transition for deviceObj in pass {compiledPass.Pass.Name}");
                            continue;
                        }

                        var oldState = ResourceState.Unknown;
                        var flags = StateTransitionFlags.UpdateState;

                        if (barrier.OldState == ResourceState.UnorderedAccess && barrier.NewState == ResourceState.UnorderedAccess)
                        {
                            oldState = ResourceState.UnorderedAccess;
                            flags = StateTransitionFlags.None;
                        }

                        transitions.Add(
                            new StateTransitionDesc
                            {
                                Resource = deviceObj,
                                OldState = oldState,
                                NewState = barrier.NewState,
                                Flags = flags,
                                MipLevelsCount = Diligent.Native.RemainingMipLevels,
                                ArraySliceCount = Diligent.Native.RemainingArraySlices,
                            }
                        );
                    }
                }

                if (transitions.Count > 0)
                {
                    deviceContext.TransitionResourceStates([.. transitions]);
                }
            }

            foreach (var (handle, requiredState) in compiledPass.RequiredStates)
            {
                if (!handle.IsValid)
                    continue;

                var res = _resources[handle.Id];
                res.CurrentState = requiredState;
            }

            compiledPass.Pass.Execute(context, rgContext);
        }

        ResolveExtractions();
    }

    private void ResolveExtractions()
    {
        if (_textureExtractionQueue.Count > 0)
        {
            foreach (var request in _textureExtractionQueue)
            {
                ITexture? texture = null;
                if (TryGetTextureResource(request.Handle, out var resource))
                {
                    texture = resource.InternalTexture;
                    resource.IsExternal = true;
                }

                request.Assign(texture);
            }

            _textureExtractionQueue.Clear();
        }

        if (_bufferExtractionQueue.Count > 0)
        {
            foreach (var request in _bufferExtractionQueue)
            {
                IBuffer? buffer = null;
                if (TryGetBufferResource(request.Handle, out var resource))
                {
                    buffer = resource.InternalBuffer;
                    resource.IsExternal = true;
                }

                request.Assign(buffer);
            }

            _bufferExtractionQueue.Clear();
        }
    }

    private bool TryGetTextureResource(RGResourceHandle handle, out RGTexture texture)
    {
        if (
            handle.Id >= 0
            && handle.Id < _resources.Count
            && _resources[handle.Id] is RGTexture tex
        )
        {
            texture = tex;
            return true;
        }

        texture = null!;
        return false;
    }

    private bool TryGetBufferResource(RGResourceHandle handle, out RGBuffer buffer)
    {
        if (handle.Id >= 0 && handle.Id < _resources.Count && _resources[handle.Id] is RGBuffer buf)
        {
            buffer = buf;
            return true;
        }

        buffer = null!;
        return false;
    }

    internal RGResourceHandle RegisterResourceRead(
        RGResourceHandle handle,
        RenderPass pass,
        ResourceState state
    )
    {
        if (!handle.IsValid)
            return handle;

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
        if (!handle.IsValid)
            return handle;

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
        _textureExtractionQueue.Clear();
        _bufferExtractionQueue.Clear();
        _passes.Clear();
        _resources.Clear();
        _resourceMap.Clear();
        _markedOutputResources.Clear();
        _compiledPasses.Clear();
        _executionOrder.Clear();
        _activeResourceIds.Clear();
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
            if (!res.IsImported && !res.IsExternal)
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
