using System;
using System.Collections.Generic;
using Diligent;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Graph;

internal class PassMetadata
{
    public bool Active;
    public List<(RenderGraphHandle Handle, ResourceState State, SubResourceRange Range)> Reads = [];
    public List<(RenderGraphHandle Handle, ResourceState State, SubResourceRange Range)> Writes =
    [];
}

public class RenderGraph : IDisposable
{
    // ── 帧内 ──
    private readonly List<IRenderGraphPass> _passes = [];
    private readonly List<RenderGraphResource> _resources = [];
    private readonly Dictionary<string, int> _resourceLookup = [];
    private readonly Dictionary<int, ITexture> _importedTextures = [];
    private readonly Dictionary<int, IBuffer> _importedBuffers = [];
    private readonly List<PassMetadata> _passMetadata = [];
    private readonly HashSet<int> _markedOutputResources = [];

    // Compile state
    private readonly struct CompiledBarrier(
        RenderGraphHandle handle,
        ResourceState oldState,
        ResourceState newState,
        uint firstMipLevel = 0,
        uint mipLevelCount = uint.MaxValue,
        uint firstArraySlice = 0,
        uint arraySliceCount = uint.MaxValue
    )
    {
        public RenderGraphHandle Handle { get; } = handle;
        public ResourceState OldState { get; } = oldState;
        public ResourceState NewState { get; } = newState;
        public uint FirstMipLevel { get; } = firstMipLevel;
        public uint MipLevelCount { get; } = mipLevelCount;
        public uint FirstArraySlice { get; } = firstArraySlice;
        public uint ArraySliceCount { get; } = arraySliceCount;
    }

    private class CompiledPass(IRenderGraphPass pass, int originalIndex)
    {
        public IRenderGraphPass Pass { get; } = pass;
        public int OriginalIndex { get; } = originalIndex;
        public bool Active { get; set; }
        public List<CompiledBarrier> PreBarriers { get; } = [];
        public List<(
            RenderGraphHandle Handle,
            ResourceState State,
            SubResourceRange Range
        )> RequiredStates { get; } = [];
    }

    private readonly List<CompiledPass> _compiledPasses = [];
    private readonly List<int> _executionOrder = [];
    private readonly HashSet<int> _activeResourceIds = [];

    // ── 跨帧 ──
    private readonly List<IRenderFeature> _features = [];
    private readonly Dictionary<string, CachedTexture> _textureCache = [];
    private readonly Dictionary<string, CachedBuffer> _bufferCache = [];

    // Persisted per-subresource state across frames: (resourceName, mip, slice) -> ResourceState
    private Dictionary<
        (string Name, uint Mip, uint Slice),
        ResourceState
    > _persistedSubresourceStates = [];
    private readonly List<RenderGraphMemoryHeap> _heaps = [];
    private IRenderDevice? _device;
    private IFence? _fence;
    private ulong _fenceValue;
    private readonly Queue<(ulong Fence, IDisposable Resource)> _deferredReleases = new();
    private readonly List<(string Name, double Ms)> _lastPassTimings = [];
    private int _executeFrameCount;

    // Physical resource resolution (per-frame, keyed by resource index)
    private ITexture?[] _resolvedTextures = [];
    private IBuffer?[] _resolvedBuffers = [];

    private struct PlacementInfo
    {
        public ulong Size;
        public ulong Alignment;
        public ulong Offset;
        public int HeapIndex;
    }

    private PlacementInfo[] _placements = [];

    // Store descs for transient resources
    private readonly Dictionary<int, TextureDesc> _textureDescs = [];
    private readonly Dictionary<int, BufferDesc> _bufferDescs = [];

    // ── Feature API ──

    public void AddFeature(IRenderFeature feature)
    {
        _features.Add(feature);
    }

    public void RemoveFeature(IRenderFeature feature)
    {
        _features.Remove(feature);
    }

    // ── API ──

    public void BeginFrame()
    {
        ProcessDeferredReleases();

        _passes.Clear();
        _resources.Clear();
        _resourceLookup.Clear();
        _importedTextures.Clear();
        _importedBuffers.Clear();
        _passMetadata.Clear();
        _markedOutputResources.Clear();
        _compiledPasses.Clear();
        _executionOrder.Clear();
        _activeResourceIds.Clear();
        _textureDescs.Clear();
        _bufferDescs.Clear();
        Array.Clear(_placements);
    }

    public void Initialize(IRenderDevice device)
    {
        _device = device;
        _fence = device.CreateFence(
            new FenceDesc { Name = "RenderGraph Fence", Type = FenceType.General }
        );
    }

    public RenderGraphHandle CreateTexture(string name, TextureDesc desc)
    {
        int index = _resources.Count;
        _resources.Add(new RenderGraphResource(name, ResourceKind.Texture));
        _resourceLookup[name] = index;
        _textureDescs[index] = desc;
        return new RenderGraphHandle(index + 1);
    }

    public RenderGraphHandle CreateBuffer(string name, BufferDesc desc)
    {
        int index = _resources.Count;
        _resources.Add(new RenderGraphResource(name, ResourceKind.Buffer));
        _resourceLookup[name] = index;
        _bufferDescs[index] = desc;
        return new RenderGraphHandle(index + 1);
    }

    public RenderGraphHandle Import(
        string name,
        ITexture texture,
        ResourceState state = ResourceState.Unknown
    )
    {
        int index = _resources.Count;
        var res = new RenderGraphResource(name, ResourceKind.Texture) { CurrentState = state };
        _resources.Add(res);
        _resourceLookup[name] = index;
        _importedTextures[index] = texture;
        // Store desc from the actual texture
        _textureDescs[index] = texture.GetDesc();
        return new RenderGraphHandle(index + 1);
    }

    public RenderGraphHandle Import(
        string name,
        IBuffer buffer,
        ResourceState state = ResourceState.Unknown
    )
    {
        int index = _resources.Count;
        var res = new RenderGraphResource(name, ResourceKind.Buffer) { CurrentState = state };
        _resources.Add(res);
        _resourceLookup[name] = index;
        _importedBuffers[index] = buffer;
        _bufferDescs[index] = buffer.GetDesc();
        return new RenderGraphHandle(index + 1);
    }

    public void AddPass(IRenderGraphPass pass)
    {
        int passIndex = _passes.Count;
        _passes.Add(pass);
        _passMetadata.Add(new PassMetadata { Active = true });
    }

    public void AddPass<TData>(
        string name,
        Action<RenderGraphBuilder, TData> setup,
        Action<RenderGraphContext, TData> execute
    )
        where TData : class, new()
    {
        var pass = new LambdaRenderGraphPass<TData>(name, new TData(), setup, execute);
        AddPass(pass);
    }

    public void AddPass(
        string name,
        Action<RenderGraphBuilder> setup,
        Action<RenderGraphContext> execute
    )
    {
        AddPass(new LambdaRenderGraphPass(name, setup, execute));
    }

    public void MarkOutput(RenderGraphHandle h)
    {
        if (!h.IsValid)
            return;
        _markedOutputResources.Add(h.Index);
    }

    public RenderGraphHandle GetResourceHandle(string name)
    {
        if (_resourceLookup.TryGetValue(name, out int index))
            return new RenderGraphHandle(index + 1);
        return RenderGraphHandle.Invalid;
    }

    public delegate MemoryRequirements GetMemoryRequirementsDelegate(
        string name,
        ResourceKind kind,
        TextureDesc? texDesc,
        BufferDesc? bufDesc
    );

    public void Compile(
        IRenderDevice? device = null,
        GetMemoryRequirementsDelegate? getMemoryReqs = null
    )
    {
        // Feature 注册 pass（按添加顺序）
        foreach (var feature in _features)
            feature.AddPasses(this);

        _device = device;
        PrepareForCompile();
        foreach (var pass in _passes)
        {
            int passIndex = _passes.IndexOf(pass);
            var builder = new RenderGraphBuilder(this, passIndex);
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

            var meta = _passMetadata[passIndex];
            if (!meta.Active)
                continue;

            foreach (var (handle, _, _) in meta.Reads)
            {
                if (!handle.IsValid)
                    continue;

                _activeResourceIds.Add(handle.Index);
                if (!firstPass.ContainsKey(handle.Index))
                    firstPass[handle.Index] = executionIndex;
                lastPass[handle.Index] = executionIndex;
            }

            foreach (var (handle, _, _) in meta.Writes)
            {
                if (!handle.IsValid)
                    continue;

                _activeResourceIds.Add(handle.Index);
                if (!firstPass.ContainsKey(handle.Index))
                    firstPass[handle.Index] = executionIndex;
                lastPass[handle.Index] = executionIndex;
            }
        }

        if (device != null || getMemoryReqs != null)
        {
            AllocateMemoryHeaps(device, getMemoryReqs, firstPass, lastPass);
        }
    }

    public void Execute(RenderContext context)
    {
        var deviceContext = context.ImmediateContext;

        if (_compiledPasses.Count == 0 || _executionOrder.Count == 0)
        {
            return;
        }

        // Resolve physical resources
        if (_resolvedTextures.Length < _resources.Count)
        {
            Array.Resize(ref _resolvedTextures, _resources.Count);
            Array.Resize(ref _resolvedBuffers, _resources.Count);
        }

        Array.Clear(_resolvedTextures, 0, _resources.Count);
        Array.Clear(_resolvedBuffers, 0, _resources.Count);

        for (int i = 0; i < _resources.Count; i++)
        {
            if (!_activeResourceIds.Contains(i))
                continue;

            var res = _resources[i];

            // Check imported
            if (_importedTextures.TryGetValue(i, out var importedTex))
            {
                _resolvedTextures[i] = importedTex;
                continue;
            }
            if (_importedBuffers.TryGetValue(i, out var importedBuf))
            {
                _resolvedBuffers[i] = importedBuf;
                continue;
            }

            // Resolve from cache by name
            if (res.Kind == ResourceKind.Texture && _textureDescs.TryGetValue(i, out var texDesc))
            {
                if (
                    _textureCache.TryGetValue(res.Name, out var cached)
                    && cached.Texture != null
                    && AreCompatible(cached.Desc, texDesc)
                )
                {
                    _resolvedTextures[i] = cached.Texture;
                    // Opt out of Diligent's whole-resource state tracking;
                    // RenderGraph manages per-subresource state.
                    cached.Texture.SetState(ResourceState.Unknown);
                    cached.IdleFrames = 0;
                    cached.LastUsedFence = _fenceValue + 1; // Will be signaled at end of Execute
                }
                else
                {
                    // Create new
                    var newTex = context.Device?.CreateTexture(texDesc, null);
                    // Opt out of Diligent's whole-resource state tracking
                    newTex?.SetState(ResourceState.Unknown);
                    _resolvedTextures[i] = newTex;

                    // Defer old resource release using its last fence
                    if (cached != null)
                    {
                        DeferRelease(cached, cached.LastUsedFence);
                    }

                    _textureCache[res.Name] = new CachedTexture
                    {
                        Desc = texDesc,
                        Texture = newTex,
                        IdleFrames = 0,
                        LastUsedFence = _fenceValue + 1,
                    };
                }
            }
            else if (
                res.Kind == ResourceKind.Buffer
                && _bufferDescs.TryGetValue(i, out var bufDesc)
            )
            {
                if (
                    _bufferCache.TryGetValue(res.Name, out var cached)
                    && cached.Buffer != null
                    && AreCompatible(cached.Desc, bufDesc)
                )
                {
                    _resolvedBuffers[i] = cached.Buffer;
                    // Only opt out of Diligent tracking for default-usage buffers.
                    // Dynamic buffers have no D3D12 resource and must stay in GENERIC_READ.
                    if (bufDesc.Usage == Usage.Default || bufDesc.Usage == Usage.Immutable)
                        cached.Buffer.SetState(ResourceState.Unknown);
                    cached.IdleFrames = 0;
                    cached.LastUsedFence = _fenceValue + 1;
                }
                else
                {
                    var newBuf = context.Device?.CreateBuffer(bufDesc, null);
                    if (
                        newBuf != null
                        && (bufDesc.Usage == Usage.Default || bufDesc.Usage == Usage.Immutable)
                    )
                        newBuf.SetState(ResourceState.Unknown);
                    _resolvedBuffers[i] = newBuf;

                    if (cached != null)
                    {
                        DeferRelease(cached, cached.LastUsedFence);
                    }

                    _bufferCache[res.Name] = new CachedBuffer
                    {
                        Desc = bufDesc,
                        Buffer = newBuf,
                        IdleFrames = 0,
                        LastUsedFence = _fenceValue + 1,
                    };
                }
            }
        }

        // Execute passes
        var rgContext = new RenderGraphContext(this, context);
        var passSw = new System.Diagnostics.Stopwatch();
        _lastPassTimings.Clear();

        foreach (int passIndex in _executionOrder)
        {
            var compiledPass = _compiledPasses[passIndex];
            if (!compiledPass.Active)
                continue;

            passSw.Restart();

            if (deviceContext != null && compiledPass.PreBarriers.Count > 0)
            {
                var transitions = new List<StateTransitionDesc>();

                foreach (var barrier in compiledPass.PreBarriers)
                {
                    if (barrier.Handle.Index < 0 || barrier.Handle.Index >= _resources.Count)
                        continue;

                    IDeviceObject? deviceObj = null;
                    var res = _resources[barrier.Handle.Index];
                    if (res.Kind == ResourceKind.Texture)
                    {
                        deviceObj = _resolvedTextures[barrier.Handle.Index];
                    }
                    else if (res.Kind == ResourceKind.Buffer)
                    {
                        deviceObj = _resolvedBuffers[barrier.Handle.Index];
                    }

                    if (deviceObj != null)
                    {
                        var resIdx = barrier.Handle.Index;
                        // Resources that still use Diligent's internal state tracking:
                        // imported resources, and dynamic/staging buffers (no D3D12 backing).
                        bool isImported =
                            _importedTextures.ContainsKey(resIdx)
                            || _importedBuffers.ContainsKey(resIdx);
                        if (
                            !isImported
                            && res.Kind == ResourceKind.Buffer
                            && _bufferDescs.TryGetValue(resIdx, out var bd)
                            && bd.Usage != Usage.Default
                            && bd.Usage != Usage.Immutable
                        )
                        {
                            isImported = true;
                        }

                        ResourceState oldState;
                        StateTransitionFlags flags;

                        if (isImported)
                        {
                            // Imported resources: let Diligent auto-detect from its
                            // internal whole-resource tracker (which is correct for these).
                            oldState = ResourceState.Unknown;
                            flags = StateTransitionFlags.UpdateState;

                            if (
                                barrier.OldState == ResourceState.UnorderedAccess
                                && barrier.NewState == ResourceState.UnorderedAccess
                            )
                            {
                                oldState = ResourceState.UnorderedAccess;
                                flags = StateTransitionFlags.None;
                            }
                        }
                        else
                        {
                            // RenderGraph-managed resources: SetState(Unknown) was called,
                            // so Diligent will use our explicit OldState directly.
                            oldState = barrier.OldState;
                            flags = StateTransitionFlags.None;

                            if (
                                barrier.OldState == ResourceState.UnorderedAccess
                                && barrier.NewState == ResourceState.UnorderedAccess
                            )
                            {
                                oldState = ResourceState.UnorderedAccess;
                            }

                            // Map tracker's Unknown (never used) to Undefined
                            // (= D3D12_RESOURCE_STATE_COMMON, the initial physical state).
                            if (oldState == ResourceState.Unknown)
                                oldState = ResourceState.Undefined;
                        }

                        transitions.Add(
                            new StateTransitionDesc
                            {
                                Resource = deviceObj,
                                OldState = oldState,
                                NewState = barrier.NewState,
                                Flags = flags,
                                FirstMipLevel = barrier.FirstMipLevel,
                                MipLevelsCount =
                                    barrier.MipLevelCount == uint.MaxValue
                                        ? Diligent.Native.RemainingMipLevels
                                        : barrier.MipLevelCount,
                                FirstArraySlice = barrier.FirstArraySlice,
                                ArraySliceCount =
                                    barrier.ArraySliceCount == uint.MaxValue
                                        ? Diligent.Native.RemainingArraySlices
                                        : barrier.ArraySliceCount,
                            }
                        );
                    }
                }

                if (transitions.Count > 0)
                {
                    // Unbind RTs before transitioning to avoid Diligent info spam
                    deviceContext.SetRenderTargets(
                        [],
                        null,
                        ResourceStateTransitionMode.None
                    );
                    deviceContext.TransitionResourceStates([.. transitions]);
                }
            }

            foreach (var (handle, requiredState, _) in compiledPass.RequiredStates)
            {
                if (!handle.IsValid)
                    continue;

                _resources[handle.Index] = _resources[handle.Index] with
                {
                    CurrentState = requiredState,
                };
            }

            compiledPass.Pass.Execute(rgContext);
            passSw.Stop();
            _lastPassTimings.Add((compiledPass.Pass.Name, passSw.Elapsed.TotalMilliseconds));
        }

        _executeFrameCount++;
        if (_executeFrameCount % 120 == 0)
        {
            Console.WriteLine("[Pass Timings]");
            foreach (var (name, ms) in _lastPassTimings)
            {
                if (ms >= 0.1)
                    Console.WriteLine($"  {name,-40} {ms,6:F1}ms");
            }
        }

        // Signal fence on GPU timeline after all passes
        if (_fence != null)
        {
            _fenceValue++;
            deviceContext?.EnqueueSignal(_fence, _fenceValue);
        }
    }

    public void EndFrame()
    {
        // Mark idle frames for unused cache entries
        foreach (var (_, cached) in _textureCache)
        {
            cached.IdleFrames++;
        }
        foreach (var (_, cached) in _bufferCache)
        {
            cached.IdleFrames++;
        }

        // Evict resources idle for too long
        const int maxIdleFrames = 4;
        var texToRemove = new List<string>();
        foreach (var (name, cached) in _textureCache)
        {
            if (cached.IdleFrames > maxIdleFrames)
            {
                DeferRelease(cached, cached.LastUsedFence);
                texToRemove.Add(name);
            }
        }
        foreach (var name in texToRemove)
            _textureCache.Remove(name);

        var bufToRemove = new List<string>();
        foreach (var (name, cached) in _bufferCache)
        {
            if (cached.IdleFrames > maxIdleFrames)
            {
                DeferRelease(cached, cached.LastUsedFence);
                bufToRemove.Add(name);
            }
        }
        foreach (var name in bufToRemove)
            _bufferCache.Remove(name);
    }

    private void DeferRelease(IDisposable resource, ulong lastUsedFence)
    {
        _deferredReleases.Enqueue((lastUsedFence, resource));
    }

    private void ProcessDeferredReleases()
    {
        if (_fence == null)
        {
            // No fence available, drain everything
            while (_deferredReleases.Count > 0)
            {
                var (_, resource) = _deferredReleases.Dequeue();
                resource.Dispose();
            }
            return;
        }

        ulong completedValue = _fence.GetCompletedValue();
        while (_deferredReleases.Count > 0 && _deferredReleases.Peek().Fence <= completedValue)
        {
            var (_, resource) = _deferredReleases.Dequeue();
            resource.Dispose();
        }
    }

    // ── Internal methods for Builder ──

    internal void RegisterResourceRead(
        RenderGraphHandle handle,
        int passIndex,
        ResourceState state,
        SubResourceRange range
    )
    {
        if (!handle.IsValid || passIndex < 0 || passIndex >= _passMetadata.Count)
            return;

        _passMetadata[passIndex].Reads.Add((handle, state, range));
    }

    internal void RegisterResourceWrite(
        RenderGraphHandle handle,
        int passIndex,
        ResourceState state,
        SubResourceRange range
    )
    {
        if (!handle.IsValid || passIndex < 0 || passIndex >= _passMetadata.Count)
            return;

        _passMetadata[passIndex].Writes.Add((handle, state, range));
    }

    // ── Internal methods for Context (resource resolution) ──

    internal ITexture? GetPhysicalTexture(RenderGraphHandle handle)
    {
        if (handle.Index >= 0 && handle.Index < _resolvedTextures.Length)
            return _resolvedTextures[handle.Index];
        return null;
    }

    internal IBuffer? GetPhysicalBuffer(RenderGraphHandle handle)
    {
        if (handle.Index >= 0 && handle.Index < _resolvedBuffers.Length)
            return _resolvedBuffers[handle.Index];
        return null;
    }

    internal ITextureView? GetPhysicalTextureView(RenderGraphHandle handle, TextureViewType type)
    {
        var texture = GetPhysicalTexture(handle);
        return texture?.GetDefaultView(type);
    }

    internal ITextureView? GetOrCreateTextureView(
        RenderGraphHandle handle,
        TextureViewDesc viewDesc
    )
    {
        var texture = GetPhysicalTexture(handle);
        if (texture == null)
            return null;

        int idx = handle.Index;
        if (idx < 0 || idx >= _resources.Count)
            return null;

        var resName = _resources[idx].Name;
        if (_textureCache.TryGetValue(resName, out var cached))
        {
            if (cached.Views.TryGetValue(viewDesc.Name, out var existing))
                return existing;

            var view = texture.CreateView(viewDesc);
            if (view != null)
                cached.Views[viewDesc.Name] = view;
            return view;
        }

        // Imported texture: use GetDefaultView instead
        return texture.CreateView(viewDesc);
    }

    internal IBufferView? GetPhysicalBufferView(RenderGraphHandle handle, BufferViewType type)
    {
        var buffer = GetPhysicalBuffer(handle);
        return buffer?.GetDefaultView(type);
    }

    // ── Compile helpers ──

    private void PrepareForCompile()
    {
        _compiledPasses.Clear();
        _executionOrder.Clear();

        for (int i = 0; i < _passes.Count; i++)
        {
            _compiledPasses.Add(new CompiledPass(_passes[i], i));
        }

        foreach (var meta in _passMetadata)
        {
            meta.Active = true;
            meta.Reads.Clear();
            meta.Writes.Clear();
        }
    }

    private HashSet<int> CollectSinkResources()
    {
        return new HashSet<int>(_markedOutputResources);
    }

    private Dictionary<int, List<int>> BuildProducerPassLookup()
    {
        var producerPassByResource = new Dictionary<int, List<int>>();

        for (int passIndex = 0; passIndex < _passes.Count; passIndex++)
        {
            var meta = _passMetadata[passIndex];
            if (!meta.Active)
                continue;

            foreach (var (handle, _, _) in meta.Writes)
            {
                if (!handle.IsValid)
                    continue;

                if (!producerPassByResource.TryGetValue(handle.Index, out var producers))
                {
                    producers = new List<int>();
                    producerPassByResource[handle.Index] = producers;
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
                if (_passMetadata[passIndex].Active)
                    reachablePasses.Add(passIndex);
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

                var meta = _passMetadata[producerPass];
                foreach (var (handle, _, _) in meta.Reads)
                {
                    if (handle.IsValid)
                        pendingResources.Enqueue(handle.Index);
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
            _passMetadata[i].Active = isActive;
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

            var meta = _passMetadata[passIndex];
            if (!meta.Active)
                continue;

            foreach (var (handle, _, _) in meta.Reads)
            {
                if (!handle.IsValid)
                    continue;

                if (lastWriterPassByResource.TryGetValue(handle.Index, out int producerPass))
                {
                    AddDependencyEdge(producerPass, passIndex, indegree, outgoingEdges);
                }
            }

            foreach (var (handle, _, _) in meta.Writes)
            {
                if (!handle.IsValid)
                    continue;

                if (lastWriterPassByResource.TryGetValue(handle.Index, out int producerPass))
                {
                    AddDependencyEdge(producerPass, passIndex, indegree, outgoingEdges);
                }

                lastWriterPassByResource[handle.Index] = passIndex;
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
        // Track state per subresource: (resourceId, mip, slice) -> ResourceState
        var trackedState = new Dictionary<(int ResourceId, uint Mip, uint Slice), ResourceState>();

        // Initialize tracked state
        for (int i = 0; i < _resources.Count; i++)
        {
            bool isImported = _importedTextures.ContainsKey(i) || _importedBuffers.ContainsKey(i);

            if (isImported)
            {
                // Imported resources: use their declared current state
                trackedState[(i, uint.MaxValue, uint.MaxValue)] = _resources[i].CurrentState;
            }
            else
            {
                // RenderGraph-managed resources: seed from persisted state (last frame's end state)
                // ONLY if the resource will be reused from cache (same desc).
                // If the desc changed (e.g., window resize), a new resource will be created
                // with physical state COMMON, so we must NOT use stale persisted state.
                var resName = _resources[i].Name;
                var res = _resources[i];
                bool canReuse = false;

                if (res.Kind == ResourceKind.Texture && _textureDescs.TryGetValue(i, out var td))
                {
                    canReuse =
                        _textureCache.TryGetValue(resName, out var ct)
                        && ct.Texture != null
                        && AreCompatible(ct.Desc, td);
                }
                else if (res.Kind == ResourceKind.Buffer && _bufferDescs.TryGetValue(i, out var bd))
                {
                    canReuse =
                        _bufferCache.TryGetValue(resName, out var cb)
                        && cb.Buffer != null
                        && AreCompatible(cb.Desc, bd);
                }

                bool seeded = false;
                if (canReuse)
                {
                    foreach (var kv in _persistedSubresourceStates)
                    {
                        if (kv.Key.Name == resName)
                        {
                            trackedState[(i, kv.Key.Mip, kv.Key.Slice)] = kv.Value;
                            seeded = true;
                        }
                    }
                }
                if (!seeded)
                {
                    trackedState[(i, uint.MaxValue, uint.MaxValue)] = ResourceState.Unknown;
                }
            }
        }

        foreach (int passIndex in _executionOrder)
        {
            var compiledPass = _compiledPasses[passIndex];
            compiledPass.PreBarriers.Clear();
            compiledPass.RequiredStates.Clear();

            var meta = _passMetadata[passIndex];
            if (!meta.Active)
                continue;

            // Collect required states per subresource for this pass
            var requiredBySubRes =
                new Dictionary<
                    (int ResourceId, uint Mip, uint Slice),
                    (RenderGraphHandle Handle, ResourceState State)
                >();

            void AccumulateRange(
                RenderGraphHandle handle,
                ResourceState state,
                SubResourceRange range
            )
            {
                if (!handle.IsValid)
                    return;

                int resourceId = handle.Index;
                uint mipCount = range.MipLevelCount;
                uint sliceCount = range.ArraySliceCount;

                // Resolve "all" counts from resource desc
                if (range.IsAll || mipCount == uint.MaxValue || sliceCount == uint.MaxValue)
                {
                    var res = _resources[resourceId];
                    if (
                        res.Kind == ResourceKind.Texture
                        && _textureDescs.TryGetValue(resourceId, out var td)
                    )
                    {
                        if (mipCount == uint.MaxValue)
                            mipCount = Math.Max(1u, td.MipLevels) - range.FirstMipLevel;
                        if (sliceCount == uint.MaxValue)
                            sliceCount = Math.Max(1u, td.ArraySizeOrDepth) - range.FirstArraySlice;
                    }
                    else
                    {
                        // Buffer or unknown texture: single entry
                        mipCount = 1;
                        sliceCount = 1;
                    }
                }

                for (uint m = range.FirstMipLevel; m < range.FirstMipLevel + mipCount; m++)
                {
                    for (
                        uint s = range.FirstArraySlice;
                        s < range.FirstArraySlice + sliceCount;
                        s++
                    )
                    {
                        var key = (resourceId, m, s);
                        if (requiredBySubRes.TryGetValue(key, out var existing))
                        {
                            requiredBySubRes[key] = (handle, existing.State | state);
                        }
                        else
                        {
                            requiredBySubRes[key] = (handle, state);
                        }
                    }
                }
            }

            foreach (var (handle, state, range) in meta.Writes)
                AccumulateRange(handle, state, range);
            foreach (var (handle, state, range) in meta.Reads)
                AccumulateRange(handle, state, range);

            if (requiredBySubRes.Count == 0)
                continue;

            // Group by resource, then determine barriers
            var byResource =
                new Dictionary<int, List<(uint Mip, uint Slice, ResourceState State)>>();
            foreach (var (key, val) in requiredBySubRes)
            {
                if (!byResource.TryGetValue(key.ResourceId, out var list))
                {
                    list = [];
                    byResource[key.ResourceId] = list;
                }
                list.Add((key.Mip, key.Slice, val.State));
            }

            var sortedResourceIds = new List<int>(byResource.Keys);
            sortedResourceIds.Sort();

            foreach (int resourceId in sortedResourceIds)
            {
                var entries = byResource[resourceId];
                var handle =
                    entries[0].Mip == 0 && entries[0].Slice == 0
                        ? requiredBySubRes[(resourceId, entries[0].Mip, entries[0].Slice)].Handle
                        : requiredBySubRes[(resourceId, entries[0].Mip, entries[0].Slice)].Handle;

                // Annotate each entry with its tracked (old) state, then merge
                // contiguous mip runs that share the same transition.
                var annotated = new List<(
                    uint Mip,
                    uint Slice,
                    ResourceState NewState,
                    ResourceState OldState
                )>(entries.Count);
                foreach (var entry in entries)
                {
                    var oldSt = GetTrackedState(trackedState, resourceId, entry.Mip, entry.Slice);
                    annotated.Add((entry.Mip, entry.Slice, entry.State, oldSt));
                }

                // Sort by (OldState, NewState, Slice, Mip) so mergeable runs are adjacent
                annotated.Sort(
                    (a, b) =>
                    {
                        int c = a.OldState.CompareTo(b.OldState);
                        if (c != 0)
                            return c;
                        c = a.NewState.CompareTo(b.NewState);
                        if (c != 0)
                            return c;
                        c = a.Slice.CompareTo(b.Slice);
                        return c != 0 ? c : a.Mip.CompareTo(b.Mip);
                    }
                );

                int ai = 0;
                while (ai < annotated.Count)
                {
                    var cur = annotated[ai];
                    bool needsBarrier =
                        cur.OldState != cur.NewState
                        || (
                            cur.OldState == ResourceState.UnorderedAccess
                            && cur.NewState == ResourceState.UnorderedAccess
                        );

                    // Extend run: same OldState, NewState, Slice, and contiguous Mip
                    uint runMipStart = cur.Mip;
                    uint runMipEnd = cur.Mip;
                    int runEnd = ai + 1;
                    while (runEnd < annotated.Count)
                    {
                        var next = annotated[runEnd];
                        if (
                            next.OldState == cur.OldState
                            && next.NewState == cur.NewState
                            && next.Slice == cur.Slice
                            && next.Mip == runMipEnd + 1
                        )
                        {
                            runMipEnd = next.Mip;
                            runEnd++;
                        }
                        else
                            break;
                    }

                    uint mipCount = runMipEnd - runMipStart + 1;
                    if (needsBarrier)
                    {
                        compiledPass.PreBarriers.Add(
                            new CompiledBarrier(
                                handle,
                                cur.OldState,
                                cur.NewState,
                                runMipStart,
                                mipCount,
                                cur.Slice,
                                1
                            )
                        );
                    }

                    for (uint m = runMipStart; m <= runMipEnd; m++)
                    {
                        compiledPass.RequiredStates.Add(
                            (handle, cur.NewState, new SubResourceRange(m, 1, cur.Slice, 1))
                        );
                        trackedState[(resourceId, m, cur.Slice)] = cur.NewState;
                    }

                    ai = runEnd;
                }
            }
        }

        // Persist tracked state for next frame, keyed by resource name
        var newPersisted = new Dictionary<(string Name, uint Mip, uint Slice), ResourceState>();
        foreach (var kv in trackedState)
        {
            int resId = kv.Key.ResourceId;
            if (
                resId >= 0
                && resId < _resources.Count
                && !_importedTextures.ContainsKey(resId)
                && !_importedBuffers.ContainsKey(resId)
            )
            {
                var resName = _resources[resId].Name;
                newPersisted[(resName, kv.Key.Mip, kv.Key.Slice)] = kv.Value;
            }
        }
        _persistedSubresourceStates = newPersisted;
    }

    private static ResourceState GetTrackedState(
        Dictionary<(int, uint, uint), ResourceState> trackedState,
        int resourceId,
        uint mip,
        uint slice
    )
    {
        // Try exact subresource first
        if (trackedState.TryGetValue((resourceId, mip, slice), out var state))
            return state;
        // Fall back to sentinel (whole-resource initial state)
        // Uses uint.MaxValue to never collide with real subresource keys
        if (trackedState.TryGetValue((resourceId, uint.MaxValue, uint.MaxValue), out state))
            return state;
        return ResourceState.Unknown;
    }

    private void AllocateMemoryHeaps(
        IRenderDevice? device,
        GetMemoryRequirementsDelegate? getMemoryReqs,
        Dictionary<int, int> firstPass,
        Dictionary<int, int> lastPass
    )
    {
        foreach (var heap in _heaps)
        {
            heap.Reset();
        }

        if (_placements.Length < _resources.Count)
        {
            Array.Resize(ref _placements, _resources.Count);
        }

        var transientResources = new List<(int Index, string Name, ResourceKind Kind)>();
        for (int i = 0; i < _resources.Count; i++)
        {
            // Skip imported resources
            if (_importedTextures.ContainsKey(i) || _importedBuffers.ContainsKey(i))
                continue;
            if (!firstPass.ContainsKey(i))
                continue; // Dead resource

            bool requiresCpuAccessibleMemory = false;
            if (
                _resources[i].Kind == ResourceKind.Buffer
                && _bufferDescs.TryGetValue(i, out var bDesc)
            )
            {
                requiresCpuAccessibleMemory =
                    bDesc.Usage == Usage.Dynamic
                    || bDesc.Usage == Usage.Staging
                    || bDesc.CPUAccessFlags != CpuAccessFlags.None;
            }
            else if (
                _resources[i].Kind == ResourceKind.Texture
                && _textureDescs.TryGetValue(i, out var tDesc)
            )
            {
                requiresCpuAccessibleMemory =
                    tDesc.Usage == Usage.Dynamic
                    || tDesc.Usage == Usage.Staging
                    || tDesc.CPUAccessFlags != CpuAccessFlags.None;
            }

            if (requiresCpuAccessibleMemory)
            {
                _placements[i].HeapIndex = -1;
                _placements[i].Offset = ulong.MaxValue;
                continue;
            }

            MemoryRequirements reqs = default;
            if (getMemoryReqs != null)
            {
                var res = _resources[i];
                TextureDesc? td = _textureDescs.TryGetValue(i, out var tdv) ? tdv : null;
                BufferDesc? bd = _bufferDescs.TryGetValue(i, out var bdv) ? bdv : null;
                reqs = getMemoryReqs(res.Name, res.Kind, td, bd);
            }
            else if (device != null)
            {
                if (
                    _resources[i].Kind == ResourceKind.Texture
                    && _textureDescs.TryGetValue(i, out var td)
                )
                {
                    reqs = device.GetTextureMemoryRequirements(td);
                }
                else if (
                    _resources[i].Kind == ResourceKind.Buffer
                    && _bufferDescs.TryGetValue(i, out var bd)
                )
                {
                    reqs = device.GetBufferMemoryRequirements(bd);
                }
                else
                    continue;
            }
            else
                continue;

            _placements[i].Size = reqs.Size;
            _placements[i].Alignment = reqs.Alignment;
            _placements[i].HeapIndex = -1;
            _placements[i].Offset = ulong.MaxValue;

            transientResources.Add((i, _resources[i].Name, _resources[i].Kind));
        }

        // Greedily allocate: largest resources first
        transientResources.Sort(
            (a, b) => _placements[b.Index].Size.CompareTo(_placements[a.Index].Size)
        );

        foreach (var (resIndex, _, _) in transientResources)
        {
            bool allocated = false;
            int first = firstPass[resIndex];
            int last = lastPass[resIndex];

            for (int i = 0; i < _heaps.Count; i++)
            {
                if (
                    _heaps[i]
                        .TryAllocate(
                            _placements[resIndex].Size,
                            _placements[resIndex].Alignment,
                            resIndex,
                            first,
                            last,
                            out ulong offset
                        )
                )
                {
                    _placements[resIndex].HeapIndex = i;
                    _placements[resIndex].Offset = offset;
                    allocated = true;
                    break;
                }
            }

            if (!allocated)
            {
                ulong minHeapSize = 64ul * 1024 * 1024;
                ulong heapCapacity = Math.Max(
                    minHeapSize,
                    _placements[resIndex].Size + _placements[resIndex].Alignment
                );

                int heapIdx = _heaps.Count;
                var newHeap =
                    device != null
                        ? new RenderGraphMemoryHeap(device, heapCapacity)
                        : new RenderGraphMemoryHeap(heapCapacity);
                _heaps.Add(newHeap);

                if (
                    newHeap.TryAllocate(
                        _placements[resIndex].Size,
                        _placements[resIndex].Alignment,
                        resIndex,
                        first,
                        last,
                        out ulong offset
                    )
                )
                {
                    _placements[resIndex].HeapIndex = heapIdx;
                    _placements[resIndex].Offset = offset;
                }
            }
        }
    }

    // ── Compatibility checks ──

    private static bool AreCompatible(TextureDesc a, TextureDesc b)
    {
        return a.Width == b.Width
            && a.Height == b.Height
            && a.Format == b.Format
            && a.BindFlags == b.BindFlags
            && a.Type == b.Type
            && a.ArraySizeOrDepth == b.ArraySizeOrDepth
            && a.MipLevels == b.MipLevels;
    }

    private static bool AreCompatible(BufferDesc a, BufferDesc b)
    {
        return a.Size == b.Size
            && a.BindFlags == b.BindFlags
            && a.Usage == b.Usage
            && a.Mode == b.Mode;
    }

    // ── Dispose ──

    public void Dispose()
    {
        // Wait for GPU to finish all pending work
        if (_fence != null && _fenceValue > 0)
        {
            _fence.Wait(_fenceValue);
        }

        // Flush all deferred releases
        while (_deferredReleases.Count > 0)
        {
            var (_, resource) = _deferredReleases.Dequeue();
            resource.Dispose();
        }

        foreach (var (_, cached) in _textureCache)
        {
            cached.Dispose();
        }
        _textureCache.Clear();

        foreach (var (_, cached) in _bufferCache)
        {
            cached.Dispose();
        }
        _bufferCache.Clear();

        foreach (var heap in _heaps)
        {
            heap.Dispose();
        }
        _heaps.Clear();

        _fence?.Dispose();

        // Dispose all features
        foreach (var feature in _features)
            feature.Dispose();
        _features.Clear();

        _resources.Clear();
        _passes.Clear();
        _resourceLookup.Clear();
    }
}
