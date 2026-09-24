using System.Threading;
using System.Threading.Tasks;
using SomeEngine.Core.Collections;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Graph;

public sealed partial class RenderGraph
{
    internal TextureHandle GetTexture(RenderGraphHandle handle)
    {
        int index = GetResourceIndex(handle, nameof(GetTexture));
        var resource = _resources[index];
        if (resource.Kind != ResourceKind.Texture)
            throw new InvalidOperationException($"RenderGraph resource '{resource.Name}' is not a texture.");
        if (!resource.Texture.IsValid)
            throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has not been resolved.");
        return resource.Texture;
    }

    internal BufferHandle GetBuffer(RenderGraphHandle handle)
    {
        int index = GetResourceIndex(handle, nameof(GetBuffer));
        var resource = _resources[index];
        if (resource.Kind != ResourceKind.Buffer)
            throw new InvalidOperationException($"RenderGraph resource '{resource.Name}' is not a buffer.");
        if (!resource.Buffer.IsValid)
            throw new InvalidOperationException($"RenderGraph buffer '{resource.Name}' has not been resolved.");
        return resource.Buffer;
    }

    internal TextureDesc GetTextureDesc(RenderGraphHandle handle)
    {
        int index = GetResourceIndex(handle, nameof(GetTextureDesc));
        var resource = _resources[index];
        return resource.TextureDesc
            ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
    }

    internal BufferDesc GetBufferDesc(RenderGraphHandle handle)
    {
        int index = GetResourceIndex(handle, nameof(GetBufferDesc));
        var resource = _resources[index];
        return resource.BufferDesc
            ?? throw new InvalidOperationException($"RenderGraph buffer '{resource.Name}' has no descriptor.");
    }

    internal ResourceKind GetResourceKind(RenderGraphHandle handle, string operation)
    {
        int index = GetResourceIndex(handle, operation);
        return _resources[index].Kind;
    }

    internal TextureViewHandle GetTextureView(
        IDevice device,
        RenderGraphHandle handle,
        TextureViewDesc desc)
    {
        int index = GetResourceIndex(handle, nameof(GetTextureView));
        var resource = _resources[index];
        if (resource.Kind != ResourceKind.Texture)
            throw new InvalidOperationException($"RenderGraph resource '{resource.Name}' is not a texture.");

        var key = TextureViewKey.From(desc);
        lock (_viewCacheGate)
        {
            RegisterPendingImportedTextureViews(device, index, resource);
            var views = resource.EnsureTextureViews();
            if (views.TryGetValue(key, out var existing))
            {
                if (resource.Imported)
                {
                    ValidateImportedTextureViewOwner(device, resource.Texture, existing);
                    TextureViewDesc actual = device.GetTextureViewDesc(existing);
                    RegisterTextureView(index, actual, existing);
                }
                else
                {
                    RegisterTextureView(index, desc, existing);
                }
                return existing;
            }

            var view = device.CreateTextureView(resource.Texture, desc);
            views[key] = view;
            RegisterTextureView(index, desc, view);
            if (resource.Imported && resource.OwnsImportedViews)
                _ownedTextureViews.Add(view);
            return view;
        }
    }

    internal BufferViewHandle GetBufferView(
        IDevice device,
        RenderGraphHandle handle,
        BufferViewDesc desc)
    {
        int index = GetResourceIndex(handle, nameof(GetBufferView));
        var resource = _resources[index];
        if (resource.Kind != ResourceKind.Buffer)
            throw new InvalidOperationException($"RenderGraph resource '{resource.Name}' is not a buffer.");

        var key = BufferViewKey.From(desc);
        lock (_viewCacheGate)
        {
            RegisterPendingImportedBufferViews(device, index, resource);
            var views = resource.EnsureBufferViews();
            if (views.TryGetValue(key, out var existing))
            {
                if (resource.Imported)
                {
                    ValidateImportedBufferViewOwner(device, resource.Buffer, existing);
                    BufferViewDesc actual = device.GetBufferViewDesc(existing);
                    RegisterBufferView(index, actual, existing);
                }
                else
                {
                    RegisterBufferView(index, desc, existing);
                }
                return existing;
            }

            var view = device.CreateBufferView(resource.Buffer, desc);
            views[key] = view;
            RegisterBufferView(index, desc, view);
            if (resource.Imported && resource.OwnsImportedViews)
                _ownedBufferViews.Add(view);
            return view;
        }
    }

    private void RegisterPendingImportedTextureViews(IDevice device, int resourceIndex, Resource resource)
    {
        if (resource.PendingImportedTextureViews is not { Length: > 0 } pending)
            return;

        var views = resource.EnsureTextureViews();
        for (int viewIndex = 0; viewIndex < pending.Length; viewIndex++)
        {
            TextureViewHandle importedView = pending[viewIndex];
            if (!importedView.IsValid)
                throw new InvalidOperationException("RenderGraph pending imported texture view handle is invalid.");

            ValidateImportedTextureViewOwner(device, resource.Texture, importedView);
            TextureViewDesc viewDesc = device.GetTextureViewDesc(importedView);
            views[TextureViewKey.From(viewDesc)] = importedView;
            RegisterTextureView(resourceIndex, viewDesc, importedView);
            if (resource.HasCallerOwnedImportedViews)
                TrackExternalTextureView(importedView);
        }

        resource.PendingImportedTextureViews = null;
    }

    private void RegisterPendingImportedBufferViews(IDevice device, int resourceIndex, Resource resource)
    {
        if (resource.PendingImportedBufferViews is not { Length: > 0 } pending)
            return;

        var views = resource.EnsureBufferViews();
        for (int viewIndex = 0; viewIndex < pending.Length; viewIndex++)
        {
            BufferViewHandle importedView = pending[viewIndex];
            if (!importedView.IsValid)
                throw new InvalidOperationException("RenderGraph pending imported buffer view handle is invalid.");

            ValidateImportedBufferViewOwner(device, resource.Buffer, importedView);
            BufferViewDesc viewDesc = device.GetBufferViewDesc(importedView);
            views[BufferViewKey.From(viewDesc)] = importedView;
            RegisterBufferView(resourceIndex, viewDesc, importedView);
            if (resource.HasCallerOwnedImportedViews)
                TrackExternalBufferView(importedView);
        }

        resource.PendingImportedBufferViews = null;
    }

    private void RegisterTextureView(int resourceIndex, TextureViewDesc desc, TextureViewHandle view)
    {
        if (!view.IsValid)
            throw new InvalidOperationException("RenderGraph cannot register an invalid texture view handle.");

        lock (_frameUseGate)
            _textureViewBindings[view] = new RegisteredTextureView(resourceIndex, desc);
    }

    private void RegisterBufferView(int resourceIndex, BufferViewDesc desc, BufferViewHandle view)
    {
        if (!view.IsValid)
            throw new InvalidOperationException("RenderGraph cannot register an invalid buffer view handle.");

        lock (_frameUseGate)
            _bufferViewBindings[view] = new RegisteredBufferView(resourceIndex, desc);
    }

    private void TrackExternalTextureView(TextureViewHandle view)
    {
        if (!view.IsValid)
            throw new InvalidOperationException("RenderGraph cannot track an invalid external texture view handle.");

        lock (_frameUseGate)
            _frameExternalTextureViews.Add(view);
    }

    private void TrackExternalBufferView(BufferViewHandle view)
    {
        if (!view.IsValid)
            throw new InvalidOperationException("RenderGraph cannot track an invalid external buffer view handle.");

        lock (_frameUseGate)
            _frameExternalBufferViews.Add(view);
    }

    private void UnregisterTextureView(TextureViewHandle view)
    {
        if (!view.IsValid)
            return;

        lock (_frameUseGate)
            _textureViewBindings.Remove(view);
    }

    private void UnregisterBufferView(BufferViewHandle view)
    {
        if (!view.IsValid)
            return;

        lock (_frameUseGate)
            _bufferViewBindings.Remove(view);
    }

    private static void ValidateImportedTextureViewOwner(IDevice device, TextureHandle texture, TextureViewHandle view)
    {
        if (!device.TryGetTextureViewOwner(view, out TextureHandle owner, out _)
            || owner != texture)
        {
            throw new InvalidOperationException(
                "RenderGraph imported texture views must belong to the imported texture handle.");
        }
    }

    private static void ValidateImportedBufferViewOwner(IDevice device, BufferHandle buffer, BufferViewHandle view)
    {
        if (!device.TryGetBufferViewOwner(view, out BufferHandle owner, out _)
            || owner != buffer)
        {
            throw new InvalidOperationException(
                "RenderGraph imported buffer views must belong to the imported buffer handle.");
        }
    }

    private bool ResolvePendingImportedTextureView(TextureViewHandle view)
    {
        if (_lastDevice == null || !view.IsValid)
            return false;

        for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
        {
            Resource resource = _resources[resourceIndex];
            if (resource.PendingImportedTextureViews is not { Length: > 0 } pending)
                continue;

            for (int viewIndex = 0; viewIndex < pending.Length; viewIndex++)
            {
                if (pending[viewIndex] != view)
                    continue;

                RegisterPendingImportedTextureViews(_lastDevice, resourceIndex, resource);
                return _textureViewBindings.ContainsKey(view);
            }
        }

        return false;
    }

    private bool ResolvePendingImportedBufferView(BufferViewHandle view)
    {
        if (_lastDevice == null || !view.IsValid)
            return false;

        for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
        {
            Resource resource = _resources[resourceIndex];
            if (resource.PendingImportedBufferViews is not { Length: > 0 } pending)
                continue;

            for (int viewIndex = 0; viewIndex < pending.Length; viewIndex++)
            {
                if (pending[viewIndex] != view)
                    continue;

                RegisterPendingImportedBufferViews(_lastDevice, resourceIndex, resource);
                return _bufferViewBindings.ContainsKey(view);
            }
        }

        return false;
    }

    internal BindingSetHandle GetBindingSet(
        IDevice device,
        PassParameters parameters,
        string name)
    {
        BindingLayoutHandle layout = parameters.Layout;
        if (!layout.IsValid)
            throw new ArgumentException("binding set owner requires a valid binding layout.", nameof(parameters));

        return GetBindingSet(
            device,
            layout,
            parameters.ResourceSpan,
            parameters.Hash,
            name,
            parameters.OwnedResources);
    }

    internal BindingSetHandle GetBindingSet(
        IDevice device,
        PassBindings bindings,
        string name)
    {
        BindingLayoutHandle layout = bindings.Layout;
        if (!layout.IsValid)
            throw new ArgumentException("binding set owner requires a valid binding layout.", nameof(bindings));
        ReadOnlySpan<BindingResourceDesc> resources = bindings.ResourceSpan;

        return GetBindingSet(
            device,
            layout,
            resources,
            bindings.Hash,
            name);
    }

    private BindingSetHandle GetBindingSet(
        IDevice device,
        BindingLayoutHandle layout,
        ReadOnlySpan<BindingResourceDesc> resources,
        int hash,
        string name,
        BindingResourceDesc[]? ownedResources = null)
    {
        BindingResourceDesc[] snapshot;
        bool cacheable = !UsesFrameExternalViews(resources);
        if (!cacheable)
        {
            snapshot = ownedResources ?? resources.ToArray();
            BindingSetHandle transientHandle = device.CreateBindingSet(layout, snapshot, name);
            TrackTransientBindingSet(transientHandle);
            return transientHandle;
        }

        lock (_bindingSetGate)
        {
            if (_bindingCache.TryGet(hash, layout, resources, out BindingSetHandle cached))
            {
                Profiler.BindingLocalHit();
                TrackBindingSet(cached);
                return cached;
            }

            if (_bindingSetOwner.TryGetValue(hash, out var bucket))
            {
                for (int i = 0; i < bucket.Count; i++)
                {
                    var entry = bucket[i];
                    if (entry.Layout == layout && BindingHash.Equal(entry.Resources, resources))
                    {
                        _bindingCache.Store(hash, entry);
                        Profiler.BindingShared();
                        TrackBindingSet(entry.Handle);
                        return entry.Handle;
                    }
                }
            }

            snapshot = ownedResources ?? resources.ToArray();
        }

        BindingSetHandle createdHandle = device.CreateBindingSet(layout, snapshot, name);
        BindingSetHandle result = createdHandle;
        bool destroyCreated = false;
        lock (_bindingSetGate)
        {
            if (_bindingCache.TryGet(hash, layout, snapshot, out BindingSetHandle cached))
            {
                destroyCreated = true;
                Profiler.BindingLocalHit();
                TrackBindingSet(cached);
                result = cached;
            }
            else if (_bindingSetOwner.TryGetValue(hash, out var bucket))
            {
                for (int i = 0; i < bucket.Count; i++)
                {
                    var entry = bucket[i];
                    if (entry.Layout == layout && BindingHash.Equal(entry.Resources, snapshot))
                    {
                        destroyCreated = true;
                        _bindingCache.Store(hash, entry);
                        Profiler.BindingShared();
                        TrackBindingSet(entry.Handle);
                        result = entry.Handle;
                        break;
                    }
                }

                if (!destroyCreated)
                {
                    var created = new BindingSetEntry(layout, snapshot, createdHandle);
                    bucket.Add(created);
                    _bindingCache.Store(hash, created);
                    _bindingIndex.Add(createdHandle, hash, layout, snapshot);
                    Profiler.BindingCreated();
                    TrackBindingSet(createdHandle);
                }
            }
            else
            {
                bucket = [];
                _bindingSetOwner.Add(hash, bucket);
                var created = new BindingSetEntry(layout, snapshot, createdHandle);
                bucket.Add(created);
                _bindingCache.Store(hash, created);
                _bindingIndex.Add(createdHandle, hash, layout, snapshot);
                Profiler.BindingCreated();
                TrackBindingSet(createdHandle);
            }
        }

        if (destroyCreated)
            device.Destroy(createdHandle);
        return result;
    }

    private bool UsesFrameExternalViews(ReadOnlySpan<BindingResourceDesc> resources)
    {
        lock (_frameUseGate)
        {
            for (int i = 0; i < resources.Length; i++)
            {
                BindingResourceDesc resource = resources[i];
                if ((resource.TextureView.IsValid && _frameExternalTextureViews.Contains(resource.TextureView))
                    || (resource.BufferView.IsValid && _frameExternalBufferViews.Contains(resource.BufferView)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void TrackTransientBindingSet(BindingSetHandle handle)
    {
        if (!handle.IsValid)
            throw new InvalidOperationException("RenderGraph cannot track an invalid transient binding set handle.");

        lock (_frameUseGate)
            _frameTransientBindingSets.Add(handle);
    }

    private void TrackBindingSet(BindingSetHandle handle)
    {
        if (!handle.IsValid)
            throw new InvalidOperationException("RenderGraph cannot track an invalid binding set handle.");

        lock (_frameUseGate)
        {
            _frameBindingSets.Add(handle);
        }
    }

    public void ClearBindSets(bool waitForGpu = false)
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(ClearBindSets));
        if (waitForGpu)
        {
            _lastDevice?.WaitIdle();
            RetirePendingFrames(wait: true);
        }
        DestroyBindSets();
    }

    public bool TryRetireSubmittedFrames()
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(TryRetireSubmittedFrames));
        if (!TryCompleteBackgroundCompile())
            return false;

        RetirePendingFrames(wait: false, throttle: false);
        return _executor.PendingFrames.Count == 0;
    }

    private bool TryCompleteBackgroundCompile()
    {
        Task<CompiledGraph>? task;
        lock (_compiler.Gate)
            task = _compiler.CompileTask;
        if (task == null)
            return true;
        if (!task.IsCompleted)
            return false;

        WaitForBackgroundCompile();
        return Volatile.Read(ref _backgroundCompileActive) == 0
            && Volatile.Read(ref _backgroundCompilePending) == 0;
    }

    internal void ClearBindings(
        ReadOnlySpan<BindingLayoutHandle> layouts,
        bool waitForGpu = false)
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(ClearBindings));
        var device = _lastDevice;
        if (device == null || layouts.Length == 0)
            return;

        if (waitForGpu)
        {
            device.WaitIdle();
            RetirePendingFrames(wait: true);
        }

        ulong completedFenceValue = CompletedFenceValue(device);
        RetireDeferredSets(device, completedFenceValue);
        for (int i = 0; i < layouts.Length; i++)
        {
            if (layouts[i].IsValid)
                DropLayout(device, layouts[i], completedFenceValue);
        }
    }

    internal bool RetireBindingSets()
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(RetireBindingSets));
        var device = _lastDevice;
        if (device != null)
            RetireDeferredSets(device, CompletedFenceValue(device));
        return _deferredBindingSetDestroys.Count == 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        ThrowIfActive(nameof(Dispose));
        WaitForBackgroundCompile();
        _lastDevice?.WaitIdle();
        RetirePendingFrames(wait: true);
        if (!_executor.CurrentFrameSubmitted)
            ReleaseFrameResources();
        DestroyTimestamps();
        DestroyBindSets();
        if (_lastDevice != null)
            _frameUploadBuffer.Destroy(_lastDevice);
        DestroyResourcePools();
        DestroyFrameFence();
        _passes.Clear();
        _resources.Clear();
        _activeCompile = null;
        MarkGraphChanged();
        _compiler.CompileScratch = null;
        _compiler.CompileWork = null;
        _textureExports.Clear();
        _bufferExports.Clear();
        _finalStates.Clear();
        _executor.PendingFramePool.Clear();
        foreach (var feature in _features)
            feature.Dispose();
        _features.Clear();
        _disposed = true;
    }

    private void GatherResolves(CompiledGraph compile)
    {
        for (int passSlot = 0; passSlot < compile.Count; passSlot++)
        {
            int passIndex = compile.Passes[passSlot];
            var resolves = compile.Resolves[passIndex];
            resolves.Clear();
            _scratch.BeginResolve(_resources.Count);
            var uses = compile.Uses[passIndex];
            for (int useIndex = 0; useIndex < uses.Count; useIndex++)
            {
                int resourceIndex = uses[useIndex].ResourceIndex;
                if (compile.Resources[resourceIndex].FirstPass != passSlot)
                    continue;

                AddResolve(compile, resolves, resourceIndex);
            }
        }

        compile.RootResolves.Clear();
        _scratch.BeginResolve(_resources.Count);
        for (int i = 0; i < _finalStates.Count; i++)
            AddRootResolveFallback(compile, _finalStates[i].ResourceIndex);
        for (int i = 0; i < _textureExports.Count; i++)
            AddRootResolveFallback(compile, _textureExports[i].ResourceIndex);
        for (int i = 0; i < _bufferExports.Count; i++)
            AddRootResolveFallback(compile, _bufferExports[i].ResourceIndex);
    }

    private void GatherResources(CompiledGraph compile)
    {
        for (int i = 0; i < _textureExports.Count; i++)
        {
            int resourceIndex = _textureExports[i].ResourceIndex;
            compile.Resources[resourceIndex].Exported = true;
        }
        for (int i = 0; i < _bufferExports.Count; i++)
        {
            int resourceIndex = _bufferExports[i].ResourceIndex;
            compile.Resources[resourceIndex].Exported = true;
        }
        for (int i = 0; i < _finalStates.Count; i++)
            compile.Resources[_finalStates[i].ResourceIndex].FinalState = true;

        compile.Retains.Clear();
        for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
        {
            var resource = _resources[resourceIndex];
            var record = compile.Resources[resourceIndex];
            if (!record.Live || resource.Imported)
            {
                compile.Resources[resourceIndex] = record with { Reusable = false };
                continue;
            }

            bool retained = !record.Exported;
            record.Retained = retained;
            if (retained)
                compile.Retains.Add(resourceIndex);

            if (resource.Kind == ResourceKind.Texture)
            {
                var desc = resource.TextureDesc
                    ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
                compile.Resources[resourceIndex] = record with
                {
                    TextureKey = TexturePoolKey.From(desc),
                    Reusable = retained
                        && (resource.Lifetime is ResourceLifetime.Pooled or ResourceLifetime.Transient),
                };
                continue;
            }

            var bufferDesc = resource.BufferDesc
                ?? throw new InvalidOperationException($"RenderGraph buffer '{resource.Name}' has no descriptor.");
            compile.Resources[resourceIndex] = record with
            {
                BufferKey = BufferPoolKey.From(bufferDesc),
                Reusable = retained
                    && (resource.Lifetime is ResourceLifetime.Pooled or ResourceLifetime.Transient)
                    && (!HasBufferInitialData(resource) || bufferDesc.Memory == MemoryClass.CpuUpload),
            };
        }
    }

    private void BuildAliases(IDevice device, CompiledGraph compile)
    {
        if (!EnableResourceAliasing || !device.Features.PlacedResources || !device.Features.ResourceAliasing)
        {
            ResetAliases(compile);
            GatherChecks(compile);
            compile.FinalWork = compile.RootResolves.Count != 0 || !compile.FinalTransitions.IsEmpty;
            _resourceStates.ClearAliases();
            return;
        }

        GraphSchema schema = CurrentSchema();
        if (_aliasCache.TryGet(schema, out AliasLayout? cached, out _) && TryApplyAliases(cached, compile))
        {
            Profiler.GraphAliasHit();
            compile.FinalWork = compile.RootResolves.Count != 0 || !compile.FinalTransitions.IsEmpty;
            _resourceStates.BeginAliases(schema, compile.Resources);
            return;
        }

        Profiler.GraphAliasMiss();
        ResetAliases(compile);
        _scratch.BeginAliases();
        var candidates = _scratch.AliasCandidates;
        for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
        {
            if (CanAlias(compile, resourceIndex))
                candidates.Add(resourceIndex);
        }

        if (candidates.Count < 2)
        {
            GatherChecks(compile);
            _aliasCache.Store(schema, CaptureAliases(compile));
            compile.FinalWork = compile.RootResolves.Count != 0 || !compile.FinalTransitions.IsEmpty;
            _resourceStates.BeginAliases(schema, compile.Resources);
            return;
        }

        candidates.Sort((left, right) =>
        {
            var leftRecord = compile.Resources[left];
            var rightRecord = compile.Resources[right];
            int first = leftRecord.FirstPass.CompareTo(rightRecord.FirstPass);
            return first != 0 ? first : left.CompareTo(right);
        });

        var blocks = _scratch.AliasBlocks;
        for (int i = 0; i < candidates.Count; i++)
        {
            int resourceIndex = candidates[i];
            var resource = _resources[resourceIndex];
            var record = compile.Resources[resourceIndex];
            var needs = AliasNeeds(device, resource);
            if (needs.RequiresDedicatedAllocation)
                continue;

            var key = new AliasKey(AliasMemory(resource), needs.HeapKind);
            int blockIndex = FindBlock(blocks, key, record.FirstPass);
            AliasBlock block;
            if (blockIndex < 0)
            {
                int heapIndex = FindHeap(key);
                block = new AliasBlock(heapIndex);
                blocks.Add(block);
                blockIndex = blocks.Count - 1;
            }
            else
            {
                block = blocks[blockIndex];
            }

            block.LastPass = record.LastPass;
            block.Size = Math.Max(block.Size, needs.SizeInBytes);
            block.Align = Math.Max(block.Align, needs.Alignment);
            block.Count++;
            compile.Resources[resourceIndex] = record with
            {
                AliasHeap = block.HeapIndex,
                AliasBlock = blockIndex,
                AliasSize = needs.SizeInBytes,
            };
        }

        PackAliases(device, compile, blocks);
        GatherChecks(compile);
        _aliasCache.Store(schema, CaptureAliases(compile));
        compile.FinalWork = compile.RootResolves.Count != 0 || !compile.FinalTransitions.IsEmpty;
        _resourceStates.BeginAliases(schema, compile.Resources);
    }

    private void ResetAliases(CompiledGraph compile)
    {
        _aliasHeaps.Clear();

        for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
        {
            var record = compile.Resources[resourceIndex];
            compile.Resources[resourceIndex] = record with
            {
                Aliased = false,
                AliasHeap = -1,
                AliasBlock = -1,
                AliasOffset = 0,
                AliasSize = 0,
            };
        }
    }

    private AliasLayout CaptureAliases(CompiledGraph compile)
    {
        var heaps = new AliasHeapData[_aliasHeaps.Count];
        for (int heapIndex = 0; heapIndex < _aliasHeaps.Count; heapIndex++)
        {
            var heap = _aliasHeaps[heapIndex];
            heaps[heapIndex] = new AliasHeapData(heap.Memory, heap.Kind, heap.Size);
        }

        var uses = new AliasUse[compile.Resources.Length];
        for (int resourceIndex = 0; resourceIndex < compile.Resources.Length; resourceIndex++)
        {
            var record = compile.Resources[resourceIndex];
            uses[resourceIndex] = new AliasUse(
                resourceIndex,
                record.FirstPass,
                record.LastPass,
                record.Reusable,
                record.Aliased,
                record.AliasHeap,
                record.AliasBlock,
                record.AliasOffset,
                record.AliasSize);
        }

        var checks = new ResourceTransition[compile.Transitions.Length][];
        var checkResources = new int[compile.Transitions.Length][];
        var aliasResources = new int[compile.Transitions.Length][];
        for (int passIndex = 0; passIndex < compile.Transitions.Length; passIndex++)
        {
            TransitionBatch transitions = compile.Transitions[passIndex];
            checks[passIndex] = transitions.CheckTransitions.ToArray();
            checkResources[passIndex] = transitions.CheckResources.ToArray();
            aliasResources[passIndex] = transitions.AliasResources.ToArray();
        }

        return new AliasLayout(
            heaps,
            uses,
            checks,
            checkResources,
            aliasResources,
            compile.FinalTransitions.CheckTransitions.ToArray(),
            compile.FinalTransitions.CheckResources.ToArray(),
            compile.FinalTransitions.AliasResources.ToArray());
    }

    private bool TryApplyAliases(AliasLayout layout, CompiledGraph compile)
    {
        if (layout.Uses.Length != compile.Resources.Length)
            return false;

        for (int useIndex = 0; useIndex < layout.Uses.Length; useIndex++)
        {
            AliasUse use = layout.Uses[useIndex];
            if (use.Resource < 0 || use.Resource >= compile.Resources.Length)
                return false;

            var record = compile.Resources[use.Resource];
            if (record.FirstPass != use.FirstPass
                || record.LastPass != use.LastPass
                || record.Reusable != use.Reusable)
            {
                return false;
            }
        }

        _aliasHeaps.Clear();

        for (int heapIndex = 0; heapIndex < layout.Heaps.Length; heapIndex++)
        {
            AliasHeapData data = layout.Heaps[heapIndex];
            _aliasHeaps.Add(new AliasHeap(data.Memory, data.Kind)
            {
                Size = data.Size,
            });
        }

        for (int useIndex = 0; useIndex < layout.Uses.Length; useIndex++)
        {
            AliasUse use = layout.Uses[useIndex];
            if (use.Resource < 0 || use.Resource >= compile.Resources.Length)
                continue;
            var record = compile.Resources[use.Resource];
            compile.Resources[use.Resource] = record with
            {
                Reusable = use.Reusable,
                Aliased = use.Aliased,
                AliasHeap = use.Heap,
                AliasBlock = use.Block,
                AliasOffset = use.Offset,
                AliasSize = use.Size,
            };
        }

        for (int passIndex = 0; passIndex < compile.Transitions.Length; passIndex++)
        {
            TransitionBatch transitions = compile.Transitions[passIndex];
            transitions.CheckTransitions.Clear();
            transitions.CheckResources.Clear();
            transitions.AliasResources.Clear();
            transitions.SharedCheckTransitions = layout.Checks[passIndex];
            transitions.SharedCheckResources = layout.CheckResources[passIndex];
            transitions.SharedAliasResources = layout.AliasResources[passIndex];
            transitions.HasAliases = transitions.SharedAliasResources.Length != 0;
        }

        compile.FinalTransitions.CheckTransitions.Clear();
        compile.FinalTransitions.CheckResources.Clear();
        compile.FinalTransitions.AliasResources.Clear();
        compile.FinalTransitions.SharedCheckTransitions = layout.FinalChecks;
        compile.FinalTransitions.SharedCheckResources = layout.FinalCheckResources;
        compile.FinalTransitions.SharedAliasResources = layout.FinalAliasResources;
        compile.FinalTransitions.HasAliases = compile.FinalTransitions.SharedAliasResources.Length != 0;
        return true;
    }

    private bool CanAlias(CompiledGraph compile, int resourceIndex)
    {
        var record = compile.Resources[resourceIndex];
        if (!record.Live
            || record.FirstPass < 0
            || record.Exported
            || !record.Reusable)
        {
            return false;
        }

        var resource = _resources[resourceIndex];
        if (resource.Imported)
            return false;
        if (AliasMemory(resource) != MemoryClass.DeviceLocal)
            return false;
        if (resource.Kind == ResourceKind.Buffer && HasBufferInitialData(resource))
            return false;
        return true;
    }

    private static MemoryClass AliasMemory(Resource resource)
        => resource.Kind == ResourceKind.Texture
            ? (resource.TextureDesc
                ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.")).Memory
            : (resource.BufferDesc
                ?? throw new InvalidOperationException($"RenderGraph buffer '{resource.Name}' has no descriptor.")).Memory;

    private static ResourceMemoryRequirements AliasNeeds(IDevice device, Resource resource)
        => resource.Kind == ResourceKind.Texture
            ? device.Get<IMemoryDevice>()!.GetTextureReqs(resource.TextureDesc
                ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor."))
            : device.Get<IMemoryDevice>()!.GetBufferReqs(resource.BufferDesc
                ?? throw new InvalidOperationException($"RenderGraph buffer '{resource.Name}' has no descriptor."));

    private int FindBlock(List<AliasBlock> blocks, AliasKey key, int firstPass)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var heap = _aliasHeaps[block.HeapIndex];
            if (heap.Memory == key.Memory
                && heap.Kind == key.Kind
                && block.LastPass < firstPass)
            {
                return i;
            }
        }

        return -1;
    }

    private int FindHeap(AliasKey key)
    {
        for (int i = 0; i < _aliasHeaps.Count; i++)
        {
            var heap = _aliasHeaps[i];
            if (heap.Memory == key.Memory && heap.Kind == key.Kind)
                return i;
        }

        _aliasHeaps.Add(new AliasHeap(key.Memory, key.Kind));
        return _aliasHeaps.Count - 1;
    }

    private void PackAliases(IDevice device, CompiledGraph compile, List<AliasBlock> blocks)
    {
        for (int blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            var block = blocks[blockIndex];
            if (block.Count < 2)
                continue;

            var heap = _aliasHeaps[block.HeapIndex];
            ulong offset = AlignUp(heap.Size, block.Align);
            block.Offset = offset;
            heap.Size = checked(offset + block.Size);

            for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
            {
                var record = compile.Resources[resourceIndex];
                if (record.AliasBlock != blockIndex)
                {
                    continue;
                }

                compile.Resources[resourceIndex] = record with
                {
                    Aliased = true,
                    AliasOffset = offset,
                };
            }
        }

        for (int heapIndex = _aliasHeaps.Count - 1; heapIndex >= 0; heapIndex--)
        {
            var heap = _aliasHeaps[heapIndex];
            heap.Size = AlignUp(heap.Size, device.Limits.MinMemoryHeapAlignment);
            if (heap.Size != 0)
                continue;

            _aliasHeaps.RemoveAt(heapIndex);
            for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
            {
                var record = compile.Resources[resourceIndex];
                if (record.AliasHeap == heapIndex)
                    compile.Resources[resourceIndex] = record with { AliasHeap = -1, AliasBlock = -1 };
                else if (record.AliasHeap > heapIndex)
                    compile.Resources[resourceIndex] = record with { AliasHeap = record.AliasHeap - 1 };
            }
        }

        for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
        {
            var record = compile.Resources[resourceIndex];
            if (record.Aliased)
                continue;

            compile.Resources[resourceIndex] = record with
            {
                AliasHeap = -1,
                AliasBlock = -1,
                AliasOffset = 0,
                AliasSize = 0,
            };
        }
    }

    private void AddResolve(CompiledGraph compile, List<int> resolves, int resourceIndex)
    {
        if (resourceIndex < 0 || resourceIndex >= compile.Resources.Length)
            throw new InvalidOperationException("RenderGraph resolve compile received an invalid resource index.");
        var record = compile.Resources[resourceIndex];
        if (!record.Live)
            return;

        if (_scratch.MarkResolve(resourceIndex))
            resolves.Add(resourceIndex);
    }

    private void AddRootResolveFallback(CompiledGraph compile, int resourceIndex)
    {
        if (resourceIndex < 0 || resourceIndex >= compile.Resources.Length)
            throw new InvalidOperationException("RenderGraph root resolve compile received an invalid resource index.");

        if (compile.Resources[resourceIndex].FirstPass >= 0)
            return;

        AddResolve(compile, compile.RootResolves, resourceIndex);
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        if (alignment == 0)
            return value;
        ulong remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private void ResolvePassResources(IDevice device, CompiledGraph compile, int passIndex)
    {
        var resolves = compile.Resolves[passIndex];
        for (int i = 0; i < resolves.Count; i++)
            ResolveTrackedResource(device, compile, resolves[i]);
    }

    private void ResolveRootResources(IDevice device, CompiledGraph compile)
    {
        for (int i = 0; i < compile.RootResolves.Count; i++)
            ResolveTrackedResource(device, compile, compile.RootResolves[i]);
    }

    private void FinalizeResources(IDevice device, CompiledGraph compile)
    {
        for (int passSlot = 0; passSlot < compile.Count; passSlot++)
        {
            int passIndex = compile.Passes[passSlot];
            ResolvePassResources(device, compile, passIndex);
        }

        ResolveRootResources(device, compile);
    }

    private void ResolveTrackedResource(IDevice device, CompiledGraph compile, int resourceIndex)
    {
        ResolveResource(device, compile, resourceIndex);
    }

    private void ValidateImportedResources(IDevice device, CompiledGraph compile)
    {
        for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
        {
            if (!compile.Resources[resourceIndex].Live)
                continue;

            Resource resource = _resources[resourceIndex];
            if (!resource.Imported)
                continue;

            if (resource.Kind == ResourceKind.Texture)
            {
                ValidateImportedTextureDevice(device, resource);
                continue;
            }

            ValidateImportedBufferDevice(device, resource);
        }
    }

    private static void ValidateImportedTextureDevice(IDevice device, Resource resource)
    {
        try
        {
            device.GetTextureDesc(resource.Texture);
        }
        catch (RhiException ex)
        {
            throw new InvalidOperationException(
                $"RenderGraph imported texture '{resource.Name}' does not belong to the executing device.", ex);
        }
    }

    private static void ValidateImportedBufferDevice(IDevice device, Resource resource)
    {
        try
        {
            device.GetBufferDesc(resource.Buffer);
        }
        catch (RhiException ex)
        {
            throw new InvalidOperationException(
                $"RenderGraph imported buffer '{resource.Name}' does not belong to the executing device.", ex);
        }
    }

    private bool IsResolved(int resourceIndex)
    {
        var resource = _resources[resourceIndex];
        return resource.Kind == ResourceKind.Texture
            ? resource.Texture.IsValid
            : resource.Buffer.IsValid;
    }

    private void ResolveResource(IDevice device, CompiledGraph compile, int resourceIndex)
    {
        if (resourceIndex < 0 || resourceIndex >= _resources.Count)
            throw new InvalidOperationException("RenderGraph resource resolve received an invalid resource index.");
        var record = compile.Resources[resourceIndex];
        if (!record.Live)
            return;

        var resource = _resources[resourceIndex];
        if (resource.Imported)
            return;

        if (resource.Kind == ResourceKind.Texture)
        {
            if (resource.Texture.IsValid)
                return;
            var desc = resource.TextureDesc
                ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
            if (record.Aliased)
            {
                resource.Texture = PlaceTexture(device, desc, record, resource);
                return;
            }

            resource.Texture = RentTexture(device, desc, record.TextureKey, resource);
            return;
        }

        if (resource.Buffer.IsValid)
            return;
        var bufferDesc = resource.BufferDesc
            ?? throw new InvalidOperationException($"RenderGraph buffer '{resource.Name}' has no descriptor.");
        if (record.Aliased)
        {
            resource.Buffer = PlaceBuffer(device, bufferDesc, record, resource);
            return;
        }

        resource.Buffer = RentBuffer(device, bufferDesc, record.BufferKey, resource.BufferInitialData, resource);
    }

    private TextureHandle PlaceTexture(
        IDevice device,
        TextureDesc desc,
        ResourceRecord record,
        Resource resource)
    {
        var heap = GetAliasHeap(device, record.AliasHeap);
        var key = new PlacedTexturePoolKey(record.TextureKey, heap, record.AliasOffset);
        if (_placedTexturePool.TryGetValue(key, out var pool) && pool.Count > 0)
        {
            PooledTexture pooled = pool.Pop();
            resource.TextureStates = pooled.States;
            resource.TextureUavAccesses = null;
            resource.TextureViews = pooled.Views;
            UpdateTextureState(resource);
            resource.StateTrusted = resource.CurrentState == desc.InitialState;
            return pooled.Handle;
        }

        resource.CurrentState = desc.InitialState;
        InitializeTextureStates(resource, desc, desc.InitialState);
        resource.TextureUavAccesses = null;
        resource.StateTrusted = true;
        resource.EnsureTextureViews().Clear();
        return device.Get<IMemoryDevice>()!.CreatePlacedTexture(heap, record.AliasOffset, TextureCreateDesc(desc, resource.Name));
    }

    private BufferHandle PlaceBuffer(
        IDevice device,
        BufferDesc desc,
        ResourceRecord record,
        Resource resource)
    {
        var heap = GetAliasHeap(device, record.AliasHeap);
        var key = new PlacedBufferPoolKey(record.BufferKey, heap, record.AliasOffset);
        if (_placedBufferPool.TryGetValue(key, out var pool) && pool.Count > 0)
        {
            PooledBuffer pooled = pool.Pop();
            resource.CurrentState = pooled.State;
            resource.BufferUavAccess = RenderGraphAccess.None;
            resource.BufferViews = pooled.Views;
            resource.StateTrusted = pooled.State == desc.InitialState;
            return pooled.Handle;
        }

        resource.CurrentState = desc.InitialState;
        resource.BufferUavAccess = RenderGraphAccess.None;
        resource.StateTrusted = true;
        resource.EnsureBufferViews().Clear();
        return device.Get<IMemoryDevice>()!.CreatePlacedBuffer(heap, record.AliasOffset, BufferCreateDesc(desc, resource.Name));
    }

    private MemoryHeapHandle GetAliasHeap(IDevice device, int heapIndex)
    {
        if (heapIndex < 0 || heapIndex >= _aliasHeaps.Count)
            throw new InvalidOperationException("RenderGraph alias compile referenced an invalid memory heap.");

        var heap = _aliasHeaps[heapIndex];
        if (heap.Handle.IsValid)
            return heap.Handle;

        heap.Handle = RentAliasHeap(device, new AliasHeapPoolKey(heap.Memory, heap.Kind, heap.Size));
        return heap.Handle;
    }

    private MemoryHeapHandle RentAliasHeap(IDevice device, AliasHeapPoolKey key)
    {
        if (_aliasHeapPool.TryGetValue(key, out var pool) && pool.Count > 0)
        {
            return pool.Pop();
        }

        return device.Get<IMemoryDevice>()!.CreateMemoryHeap(new MemoryHeapDesc
        {
            Name = $"RenderGraph Alias {key.Kind} {key.Memory}",
            SizeInBytes = key.Size,
            Memory = key.Memory,
            Kind = key.Kind,
            Flags = MemoryHeapFlags.AllowAliasing,
        });
    }

    private TextureHandle RentTexture(IDevice device, TextureDesc desc, TexturePoolKey key, Resource resource)
    {
        if (_texturePool.TryGetValue(key, out var pool) && pool.Count > 0)
        {
            PooledTexture pooled = pool.Pop();
            resource.TextureStates = pooled.States;
            resource.TextureUavAccesses = null;
            resource.TextureViews = pooled.Views;
            UpdateTextureState(resource);
            resource.StateTrusted = resource.CurrentState == desc.InitialState;
            return pooled.Handle;
        }

        resource.CurrentState = desc.InitialState;
        InitializeTextureStates(resource, desc, desc.InitialState);
        resource.TextureUavAccesses = null;
        resource.StateTrusted = true;
        resource.EnsureTextureViews().Clear();
        return device.CreateTexture(TextureCreateDesc(desc, resource.Name));
    }

    private BufferHandle RentBuffer(
        IDevice device,
        BufferDesc desc,
        BufferPoolKey key,
        byte[]? initialData,
        Resource resource)
    {
        if (_bufferPool.TryGetValue(key, out var pool) && pool.Count > 0)
        {
            PooledBuffer pooled = pool.Pop();
            resource.CurrentState = pooled.State;
            resource.BufferUavAccess = RenderGraphAccess.None;
            resource.BufferViews = pooled.Views;
            resource.StateTrusted = pooled.State == desc.InitialState;
            if (initialData is { Length: > 0 })
            {
                UploadInitialData(device, pooled.Handle, desc, initialData);
            }
            return pooled.Handle;
        }

        resource.CurrentState = desc.InitialState;
        resource.BufferUavAccess = RenderGraphAccess.None;
        resource.StateTrusted = true;
        resource.EnsureBufferViews().Clear();
        return device.CreateBuffer(BufferCreateDesc(desc, resource.Name), initialData);
    }

    private static TextureDesc TextureCreateDesc(TextureDesc desc, string fallbackName)
        => string.IsNullOrWhiteSpace(desc.Name) ? desc with { Name = fallbackName } : desc;

    private static BufferDesc BufferCreateDesc(BufferDesc desc, string fallbackName)
        => string.IsNullOrWhiteSpace(desc.Name) ? desc with { Name = fallbackName } : desc;

    private static void UploadInitialData(
        IDevice device,
        BufferHandle buffer,
        BufferDesc desc,
        ReadOnlySpan<byte> initialData)
    {
        if (initialData.IsEmpty)
            return;
        if ((ulong)initialData.Length > desc.SizeInBytes)
            throw new ArgumentException("buffer initial data exceeds buffer size.", nameof(initialData));
        if (desc.Memory != MemoryClass.CpuUpload)
        {
            throw new InvalidOperationException(
                $"RenderGraph pooled buffer '{desc.Name}' has initial data but is {desc.Memory}; dynamic initial-data buffers must use CpuUpload memory or an explicit upload pass.");
        }

        var mapped = device.MapBuffer(buffer, MapMode.Write, 0, initialData.Length);
        initialData.CopyTo(mapped.Span);
        device.UnmapBuffer(buffer);
    }

    private static void InitializeTextureStates(Resource resource, TextureDesc desc, ResourceState state)
    {
        resource.TextureStates = CreateTextureStates(desc, state);
        resource.TextureUavAccesses = null;
        resource.CurrentState = state;
    }

    private static ResourceState[] CreateTextureStates(TextureDesc desc, ResourceState state)
    {
        var states = new ResourceState[checked((int)((ulong)desc.MipLevels * desc.ArraySize))];
        Array.Fill(states, state);
        return states;
    }

    private static ResourceState[] EnsureTextureStates(Resource resource)
    {
        if (resource.Kind != ResourceKind.Texture)
            throw new InvalidOperationException($"RenderGraph resource '{resource.Name}' is not a texture.");

        var desc = resource.TextureDesc
            ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
        int count = checked((int)((ulong)desc.MipLevels * desc.ArraySize));
        if (resource.TextureStates is { } states && states.Length == count)
            return states;

        states = CreateTextureStates(desc, resource.CurrentState);
        resource.TextureStates = states;
        resource.TextureUavAccesses = null;
        return states;
    }

    private static ResourceState[] SnapshotTextureStates(Resource resource)
    {
        var states = EnsureTextureStates(resource);
        var snapshot = new ResourceState[states.Length];
        Array.Copy(states, snapshot, states.Length);
        return snapshot;
    }

    private static ResourceState PublishedState(Resource resource)
    {
        if (resource.Kind != ResourceKind.Texture)
            return resource.CurrentState;

        if (TryUniformState(resource, out var state))
            return state;

        throw new InvalidOperationException(
            $"RenderGraph texture '{resource.Name}' has mixed subresource states; set an export final state before publishing it through a single-state API.");
    }

    private static bool TryUniformState(Resource resource, out ResourceState state)
    {
        var states = EnsureTextureStates(resource);
        return TryUniformState(states, out state);
    }

    private static void UpdateTextureState(Resource resource)
    {
        resource.CurrentState = TryUniformState(resource, out var state)
            ? state
            : ResourceState.Undefined;
    }

    private static RenderGraphAccess[] EnsureUavAccesses(Resource resource)
    {
        var states = EnsureTextureStates(resource);
        if (resource.TextureUavAccesses is { } accesses && accesses.Length == states.Length)
            return accesses;

        accesses = new RenderGraphAccess[states.Length];
        resource.TextureUavAccesses = accesses;
        return accesses;
    }

    private static bool RequiresUavDependency(RenderGraphAccess previousAccess, RenderGraphAccess currentAccess)
        => previousAccess != RenderGraphAccess.None
            && currentAccess != RenderGraphAccess.None
            && !IsWriteOnly(currentAccess)
            && (Writes(previousAccess) || Writes(currentAccess));

    private static SomeEngine.Rhi.SubresourceRange ActualRange(TextureDesc desc, SubResourceRange range)
        => ActualRange(desc, range.ToRange());

    private static SomeEngine.Rhi.SubresourceRange ActualRange(
        TextureDesc desc,
        SomeEngine.Rhi.SubresourceRange range)
    {
        uint firstMip = range.FirstMip;
        uint mipCount = range.MipCount == uint.MaxValue ? desc.MipLevels - firstMip : range.MipCount;
        uint firstSlice = range.FirstSlice;
        uint sliceCount = range.SliceCount == uint.MaxValue ? desc.ArraySize - firstSlice : range.SliceCount;
        if (firstMip >= desc.MipLevels || mipCount == 0 || mipCount > desc.MipLevels - firstMip)
            throw new InvalidOperationException("RenderGraph texture subresource mip range is outside the texture.");
        if (firstSlice >= desc.ArraySize || sliceCount == 0 || sliceCount > desc.ArraySize - firstSlice)
            throw new InvalidOperationException("RenderGraph texture subresource array range is outside the texture.");

        return new SomeEngine.Rhi.SubresourceRange(firstMip, mipCount, firstSlice, sliceCount);
    }

    private static SomeEngine.Rhi.SubresourceRange SingleSubresourceRange(uint mip, uint slice)
        => new(mip, 1, slice, 1);

    private static string MarkerName(string? name, int passIndex)
        => string.IsNullOrWhiteSpace(name)
            ? $"Pass {passIndex}"
            : name;
    private void PublishExports()
    {
        IDevice? device = _lastDevice;
        foreach (var export in _textureExports)
        {
            var resource = _resources[export.ResourceIndex];
            if (!resource.Texture.IsValid)
                throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' was exported before it was resolved.");

            export.Sink(resource.Texture, PublishedState(resource));
            if (!resource.Imported)
            {
                if (device != null && resource.TextureViews is { } views)
                    DestroyTextureViews(device, views);
                resource.Texture = default;
            }
        }

        foreach (var export in _bufferExports)
        {
            var resource = _resources[export.ResourceIndex];
            if (!resource.Buffer.IsValid)
                throw new InvalidOperationException($"RenderGraph buffer '{resource.Name}' was exported before it was resolved.");

            export.Sink(resource.Buffer, PublishedState(resource));
            if (!resource.Imported)
            {
                if (device != null && resource.BufferViews is { } views)
                    DestroyBufferViews(device, views);
                resource.Buffer = default;
            }
        }
    }

    private void ReleaseFrameResources()
    {
        var device = _lastDevice;
        _frameUploadBuffer.DiscardFrame(device);
        if (device == null)
        {
            ClearFrameUsage();
            return;
        }

        DestroyFrameTransientBindingSets(device);

        EvictBufferViews(device, _ownedBufferViews);
        for (int i = _ownedBufferViews.Count - 1; i >= 0; i--)
            device.Destroy(_ownedBufferViews[i]);
        _ownedBufferViews.Clear();

        EvictTextureViews(device, _ownedTextureViews);
        for (int i = _ownedTextureViews.Count - 1; i >= 0; i--)
            device.Destroy(_ownedTextureViews[i]);
        _ownedTextureViews.Clear();

        for (int i = _resources.Count - 1; i >= 0; i--)
        {
            var resource = _resources[i];
            if (resource.Imported)
                continue;

            if (resource.Kind == ResourceKind.Buffer && resource.Buffer.IsValid)
            {
                if (resource.BufferViews is { } views)
                    DestroyBufferViews(device, views);
                device.Destroy(resource.Buffer);
                resource.Buffer = default;
            }
            else if (resource.Kind == ResourceKind.Texture && resource.Texture.IsValid)
            {
                if (resource.TextureViews is { } views)
                    DestroyTextureViews(device, views);
                device.Destroy(resource.Texture);
                resource.Texture = default;
            }
        }

        for (int i = 0; i < _aliasHeaps.Count; i++)
        {
            var heap = _aliasHeaps[i];
            if (!heap.Handle.IsValid)
                continue;

            ReturnAliasHeap(new SubmittedAliasHeap(
                heap.Handle,
                new AliasHeapPoolKey(heap.Memory, heap.Kind, heap.Size)));
            heap.Handle = default;
        }

        _aliasHeaps.Clear();
        ClearFrameUsage();
    }

    private void DestroyFrameTransientBindingSets(IDevice device)
    {
        foreach (BindingSetHandle bindingSet in _frameTransientBindingSets)
            device.Destroy(bindingSet);
        _frameTransientBindingSets.Clear();
    }

    private void QueueFrame(
        CompiledGraph compile,
        FenceHandle fence,
        ulong fenceValue,
        IReadOnlyList<CommandBufferHandle> commandBuffers,
        DeviceTimestamps? timestamps)
    {
        int textureViewCount = _ownedTextureViews.Count;
        int bufferViewCount = _ownedBufferViews.Count;
        int aliasHeapCount = 0;
        int textureCount = 0;
        int bufferCount = 0;
        for (int i = 0; i < _aliasHeaps.Count; i++)
        {
            if (_aliasHeaps[i].Handle.IsValid)
                aliasHeapCount++;
        }

        foreach (var export in _textureExports)
        {
            var resource = _resources[export.ResourceIndex];
            if (!resource.Imported)
                textureViewCount += resource.TextureViews?.Count ?? 0;
        }

        foreach (var export in _bufferExports)
        {
            var resource = _resources[export.ResourceIndex];
            if (!resource.Imported)
                bufferViewCount += resource.BufferViews?.Count ?? 0;
        }

        for (int retainIndex = 0; retainIndex < compile.Retains.Count; retainIndex++)
        {
            int i = compile.Retains[retainIndex];
            var resource = _resources[i];
            if (resource.Imported)
                continue;
            if (resource.Kind == ResourceKind.Buffer && resource.Buffer.IsValid)
                bufferCount++;
            else if (resource.Kind == ResourceKind.Texture && resource.Texture.IsValid)
                textureCount++;
        }

        var pending = _executor.PendingFramePool.Count == 0
            ? new PendingFrameResources()
            : _executor.PendingFramePool.Pop();
        pending.Fence = fence;
        pending.FenceValue = fenceValue;
        pending.Timestamps = timestamps;
        pending.EnsureCapacity(
            commandBuffers.Count,
            _frameTransientBindingSets.Count,
            textureViewCount,
            bufferViewCount,
            aliasHeapCount,
            textureCount,
            bufferCount,
            _textureExports.Count,
            _bufferExports.Count);

        for (int i = 0; i < commandBuffers.Count; i++)
            pending.CommandBuffers.Add(commandBuffers[i]);
        foreach (BindingSetHandle bindingSet in _frameTransientBindingSets)
            pending.BindingSets.Add(bindingSet);

        pending.TextureViews.AddRange(_ownedTextureViews);
        pending.BufferViews.AddRange(_ownedBufferViews);
        for (int i = 0; i < _aliasHeaps.Count; i++)
        {
            if (!_aliasHeaps[i].Handle.IsValid)
                continue;

            var heap = _aliasHeaps[i];
            pending.Heaps.Add(new SubmittedAliasHeap(
                heap.Handle,
                new AliasHeapPoolKey(heap.Memory, heap.Kind, heap.Size)));
            heap.Handle = default;
        }
        _ownedTextureViews.Clear();
        _ownedBufferViews.Clear();

        foreach (var export in _textureExports)
        {
            var resource = _resources[export.ResourceIndex];
            if (!resource.Texture.IsValid)
                throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' was exported before it was resolved.");

            if (resource.Imported)
            {
                export.Sink(resource.Texture, PublishedState(resource));
                continue;
            }

            pending.TextureExports.Add(new SubmittedTextureExport(
                resource.Texture,
                PublishedState(resource),
                export.Sink));

            if (resource.TextureViews is { } views)
            {
                foreach (var pair in views)
                    pending.TextureViews.Add(pair.Value);
                views.Clear();
            }

            resource.Texture = default;
        }

        foreach (var export in _bufferExports)
        {
            var resource = _resources[export.ResourceIndex];
            if (!resource.Buffer.IsValid)
                throw new InvalidOperationException($"RenderGraph buffer '{resource.Name}' was exported before it was resolved.");

            if (resource.Imported)
            {
                export.Sink(resource.Buffer, PublishedState(resource));
                continue;
            }

            pending.BufferExports.Add(new SubmittedBufferExport(
                resource.Buffer,
                PublishedState(resource),
                export.Sink));

            if (resource.BufferViews is { } views)
            {
                foreach (var pair in views)
                    pending.BufferViews.Add(pair.Value);
                views.Clear();
            }

            resource.Buffer = default;
        }

        for (int retainIndex = 0; retainIndex < compile.Retains.Count; retainIndex++)
        {
            int i = compile.Retains[retainIndex];
            var resource = _resources[i];
            if (resource.Imported)
                continue;

            if (resource.Kind == ResourceKind.Buffer && resource.Buffer.IsValid)
            {
                var record = compile.Resources[i];
                pending.Buffers.Add(new SubmittedBuffer(
                    resource.Buffer,
                    record.BufferKey,
                    resource.CurrentState,
                    resource.DetachBufferViews(),
                    record.Aliased,
                    record.Reusable));
                resource.Buffer = default;
            }
            else if (resource.Kind == ResourceKind.Texture && resource.Texture.IsValid)
            {
                var record = compile.Resources[i];
                pending.Textures.Add(new SubmittedTexture(
                    resource.Texture,
                    record.TextureKey,
                    SnapshotTextureStates(resource),
                    resource.DetachTextureViews(),
                    record.Aliased,
                    record.Reusable));
                resource.Texture = default;
            }
        }

        lock (_frameUseGate)
        {
            foreach (var bindingSet in _frameBindingSets)
                _bindingSetLastUse[bindingSet] = fenceValue;
            _frameBindingSets.Clear();
            _frameTransientBindingSets.Clear();

            if (_pipelineCache != null)
            {
                foreach (PipelineHandle pipeline in _framePipelines)
                    _pipelineCache.Track(pipeline, fenceValue);
            }

            _framePipelines.Clear();
        }

        _executor.PendingFrames.Add(pending);
        _frameUploadBuffer.QueueFrame(pending.FrameUploadBuffers);
        _executor.CurrentFrameSubmitted = true;
    }

    private void RetirePendingFrames(bool wait, bool throttle = true)
    {
        var device = _lastDevice;
        if (device == null)
            return;

        if (!wait && throttle && _executor.PendingFrames.Count >= MaxPendingSubmittedFrames)
        {
            var oldest = _executor.PendingFrames[0];
            using (Profiler.BeginScope("RenderGraph.RetirePendingFrames.WaitOldest"))
            {
                device.WaitFence(oldest.Fence, oldest.FenceValue);
            }
        }

        for (int frameIndex = _executor.PendingFrames.Count - 1; frameIndex >= 0; frameIndex--)
        {
            var frame = _executor.PendingFrames[frameIndex];
            if (wait)
            {
                device.WaitFence(frame.Fence, frame.FenceValue);
            }
            else
            {
                ulong completedValue;
                using (Profiler.BeginScope("RenderGraph.RetirePendingFrames.GetFenceValue"))
                {
                    completedValue = device.GetFenceValue(frame.Fence);
                }

                if (completedValue < frame.FenceValue)
                    continue;
            }

            using (Profiler.BeginScope("RenderGraph.RetirePendingFrames.RetireFrameResources"))
            {
                RetireFrameResources(device, frame);
            }
            _executor.PendingFrames.RemoveAt(frameIndex);
            frame.ClearForReuse();
            _executor.PendingFramePool.Push(frame);
        }
    }

    private void RetireFrameResources(IDevice device, PendingFrameResources frame)
    {
        DeviceTimestamps? timestamps = frame.Timestamps;
        bool timestampsReady = false;
        try
        {
            timestamps?.Flush();
            timestampsReady = true;
        }
        finally
        {
            if (timestampsReady)
                ReturnTimestamps(timestamps);
            else
                timestamps?.Destroy();
            frame.Timestamps = null;
        }

        for (int i = frame.CommandBuffers.Count - 1; i >= 0; i--)
            device.Destroy(frame.CommandBuffers[i]);
        frame.CommandBuffers.Clear();
        for (int i = frame.BindingSets.Count - 1; i >= 0; i--)
            device.Destroy(frame.BindingSets[i]);
        frame.BindingSets.Clear();

        RetireDeferredSets(device, frame.FenceValue);
        EvictBufferViews(device, frame.BufferViews);
        for (int i = frame.BufferViews.Count - 1; i >= 0; i--)
            device.Destroy(frame.BufferViews[i]);
        EvictTextureViews(device, frame.TextureViews);
        for (int i = frame.TextureViews.Count - 1; i >= 0; i--)
            device.Destroy(frame.TextureViews[i]);
        if (frame.FrameUploadBuffers.Count > 0)
            _frameUploadBuffer.RetireFrame(frame.FrameUploadBuffers);

        foreach (var export in frame.TextureExports)
            export.Sink(export.Handle, export.State);
        foreach (var export in frame.BufferExports)
            export.Sink(export.Handle, export.State);

        for (int i = frame.Buffers.Count - 1; i >= 0; i--)
            ReturnBuffer(device, frame.Buffers[i]);
        for (int i = frame.Textures.Count - 1; i >= 0; i--)
            ReturnTexture(device, frame.Textures[i]);
        for (int i = frame.Heaps.Count - 1; i >= 0; i--)
            ReturnAliasHeap(frame.Heaps[i]);
        _pipelineCache?.Retire(frame.FenceValue);
    }

    private FenceHandle GetFrameFence(IDevice device)
    {
        if (_executor.FrameFence.IsValid)
            return _executor.FrameFence;

        _executor.FrameFence = device.CreateFence("Render frame graph");
        _executor.FrameFenceValue = 0;
        return _executor.FrameFence;
    }

    private void DestroyFrameFence()
    {
        var device = _lastDevice;
        if (device == null)
            return;

        if (_executor.FrameFence.IsValid)
        {
            device.Destroy(_executor.FrameFence);
            _executor.FrameFence = default;
        }
        _executor.FrameFenceValue = 0;

        for (int i = 0; i < _executor.QueueFences.Length; i++)
        {
            if (_executor.QueueFences[i].IsValid)
            {
                device.Destroy(_executor.QueueFences[i]);
                _executor.QueueFences[i] = default;
            }

            _executor.QueueFenceValues[i] = 0;
        }
    }

    private void ReturnTexture(IDevice device, SubmittedTexture texture)
    {
        if (!texture.Reusable)
        {
            DestroyTextureViews(device, texture.Views);
            device.Destroy(texture.Handle);
            return;
        }

        if (texture.Aliased)
        {
            var allocation = device.GetTextureAlloc(texture.Handle);
            var key = new PlacedTexturePoolKey(texture.Key, allocation.Heap, allocation.HeapOffset);
            if (!_placedTexturePool.TryGetValue(key, out var placedPool))
            {
                placedPool = new Stack<PooledTexture>();
                _placedTexturePool.Add(key, placedPool);
            }

            placedPool.Push(new PooledTexture(texture.Handle, texture.States, texture.Views));
            return;
        }

        if (!_texturePool.TryGetValue(texture.Key, out var pool))
        {
            pool = new Stack<PooledTexture>();
            _texturePool.Add(texture.Key, pool);
        }

        pool.Push(new PooledTexture(texture.Handle, texture.States, texture.Views));
    }

    private void ReturnBuffer(IDevice device, SubmittedBuffer buffer)
    {
        if (!buffer.Reusable)
        {
            DestroyBufferViews(device, buffer.Views);
            device.Destroy(buffer.Handle);
            return;
        }

        if (buffer.Aliased)
        {
            var allocation = device.GetBufferAlloc(buffer.Handle);
            var key = new PlacedBufferPoolKey(buffer.Key, allocation.Heap, allocation.HeapOffset);
            if (!_placedBufferPool.TryGetValue(key, out var placedPool))
            {
                placedPool = new Stack<PooledBuffer>();
                _placedBufferPool.Add(key, placedPool);
            }

            placedPool.Push(new PooledBuffer(buffer.Handle, buffer.State, buffer.Views));
            return;
        }

        if (!_bufferPool.TryGetValue(buffer.Key, out var pool))
        {
            pool = new Stack<PooledBuffer>();
            _bufferPool.Add(buffer.Key, pool);
        }

        pool.Push(new PooledBuffer(buffer.Handle, buffer.State, buffer.Views));
    }

    private void ReturnAliasHeap(SubmittedAliasHeap heap)
    {
        if (!_aliasHeapPool.TryGetValue(heap.Key, out var pool))
        {
            pool = new Stack<MemoryHeapHandle>();
            _aliasHeapPool.Add(heap.Key, pool);
        }

        pool.Push(heap.Handle);
    }

    private void DestroyResourcePools()
    {
        var device = _lastDevice;
        if (device == null)
            return;

        foreach (var pair in _bufferPool)
        {
            var pool = pair.Value;
            while (pool.Count > 0)
            {
                var buffer = pool.Pop();
                DestroyBufferViews(device, buffer.Views);
                device.Destroy(buffer.Handle);
            }
        }

        foreach (var pair in _texturePool)
        {
            var pool = pair.Value;
            while (pool.Count > 0)
            {
                var texture = pool.Pop();
                DestroyTextureViews(device, texture.Views);
                device.Destroy(texture.Handle);
            }
        }

        foreach (var pair in _placedBufferPool)
        {
            var pool = pair.Value;
            while (pool.Count > 0)
            {
                var buffer = pool.Pop();
                DestroyBufferViews(device, buffer.Views);
                device.Destroy(buffer.Handle);
            }
        }

        foreach (var pair in _placedTexturePool)
        {
            var pool = pair.Value;
            while (pool.Count > 0)
            {
                var texture = pool.Pop();
                DestroyTextureViews(device, texture.Views);
                device.Destroy(texture.Handle);
            }
        }

        _bufferPool.Clear();
        _texturePool.Clear();
        _placedBufferPool.Clear();
        _placedTexturePool.Clear();
        _frameUploadBuffer.Destroy(device);
        DestroyAliasHeapPool(device);
        DestroyAliasHeaps(device);
    }

    private void DestroyAliasHeapPool(IDevice device)
    {
        foreach (var pair in _aliasHeapPool)
        {
            var pool = pair.Value;
            while (pool.Count > 0)
                device.Destroy(pool.Pop());
        }

        _aliasHeapPool.Clear();
    }

    private void DestroyAliasHeaps(IDevice device)
    {
        for (int i = _aliasHeaps.Count - 1; i >= 0; i--)
        {
            if (_aliasHeaps[i].Handle.IsValid)
                device.Destroy(_aliasHeaps[i].Handle);
        }

        _aliasHeaps.Clear();
    }

    private void DestroyBindSets()
    {
        var device = _lastDevice;
        if (device == null)
            return;

        ulong completedFenceValue = CompletedFenceValue(device);
        RetireDeferredSets(device, completedFenceValue);
        if (_bindingSetOwner.Count == 0)
        {
            _bindingCache.Clear();
            return;
        }

        foreach (var pair in _bindingSetOwner)
        {
            var bucket = pair.Value;
            for (int i = bucket.Count - 1; i >= 0; i--)
            {
                DestroyOrDefer(device, bucket[i].Handle, completedFenceValue);
            }
        }

        _bindingSetOwner.Clear();
        _bindingIndex.Clear();
        _bindingCache.Clear();
    }

    private ulong CompletedFenceValue(IDevice device)
        => _executor.FrameFence.IsValid ? device.GetFenceValue(_executor.FrameFence) : ulong.MaxValue;

    private void DestroyOrDefer(
        IDevice device,
        BindingSetHandle handle,
        ulong completedFenceValue)
    {
        _bindingIndex.Remove(handle);
        if (_bindingSetLastUse.TryGetValue(handle, out ulong lastUseFenceValue)
            && lastUseFenceValue > completedFenceValue)
        {
            _deferredBindingSetDestroys.Add((handle, lastUseFenceValue));
            return;
        }

        device.Destroy(handle);
        _bindingSetLastUse.Remove(handle);
    }

    private void RetireDeferredSets(IDevice device, ulong completedFenceValue)
    {
        for (int i = _deferredBindingSetDestroys.Count - 1; i >= 0; i--)
        {
            var pending = _deferredBindingSetDestroys[i];
            if (pending.FenceValue > completedFenceValue)
                continue;

            device.Destroy(pending.Handle);
            _bindingSetLastUse.Remove(pending.Handle);
            _deferredBindingSetDestroys.RemoveAt(i);
        }
    }

    private void EvictTextureViews(IDevice device, List<TextureViewHandle> views)
    {
        for (int i = 0; i < views.Count; i++)
        {
            var view = views[i];
            if (view.IsValid)
                EvictTextureView(device, view);
        }
    }

    private void EvictBufferViews(IDevice device, List<BufferViewHandle> views)
    {
        for (int i = 0; i < views.Count; i++)
        {
            var view = views[i];
            if (view.IsValid)
                EvictBufferView(device, view);
        }
    }

    private void EvictTextureView(IDevice device, TextureViewHandle view)
    {
        UnregisterTextureView(view);
        var handles = _bindingIndex.Texture(view);
        for (int i = handles.Length - 1; i >= 0; i--)
            EvictBindingSet(device, handles[i]);
    }

    private void EvictBufferView(IDevice device, BufferViewHandle view)
    {
        UnregisterBufferView(view);
        var handles = _bindingIndex.Buffer(view);
        for (int i = handles.Length - 1; i >= 0; i--)
            EvictBindingSet(device, handles[i]);
    }

    private void DropLayout(
        IDevice device,
        BindingLayoutHandle layout,
        ulong completedFenceValue)
    {
        var handles = _bindingIndex.Layout(layout);
        for (int i = handles.Length - 1; i >= 0; i--)
            DropBindingSet(device, handles[i], completedFenceValue);
    }

    private void DropBindingSet(
        IDevice device,
        BindingSetHandle handle,
        ulong completedFenceValue)
    {
        RemoveBindingSet(handle);
        DestroyOrDefer(device, handle, completedFenceValue);
    }

    private void EvictBindingSet(IDevice device, BindingSetHandle handle)
    {
        RemoveBindingSet(handle);
        device.Destroy(handle);
        _bindingSetLastUse.Remove(handle);
    }

    private void RemoveBindingSet(BindingSetHandle handle)
    {
        if (!_bindingIndex.TryHash(handle, out int hash))
            throw new InvalidOperationException("RenderGraph binding index lost a cached binding set hash.");

        if (!_bindingSetOwner.TryGetValue(hash, out var bucket))
            throw new InvalidOperationException("RenderGraph binding set owner map lost a cached binding set bucket.");

        for (int i = bucket.Count - 1; i >= 0; i--)
        {
            if (bucket[i].Handle != handle)
                continue;

            bucket.RemoveAt(i);
            if (bucket.Count == 0)
                _bindingSetOwner.Remove(hash);
            _bindingIndex.Remove(handle);
            _bindingCache.Remove(handle);
            return;
        }

        throw new InvalidOperationException("RenderGraph binding set owner bucket lost a cached binding set handle.");
    }

    private void DestroyTextureViews(IDevice device, FlatDictionary<TextureViewKey, TextureViewHandle> views)
    {
        foreach (var pair in views)
        {
            var view = pair.Value;
            if (view.IsValid)
                EvictTextureView(device, view);
        }
        foreach (var pair in views)
            device.Destroy(pair.Value);
        views.Clear();
    }

    private void DestroyBufferViews(IDevice device, FlatDictionary<BufferViewKey, BufferViewHandle> views)
    {
        foreach (var pair in views)
        {
            var view = pair.Value;
            if (view.IsValid)
                EvictBufferView(device, view);
        }
        foreach (var pair in views)
            device.Destroy(pair.Value);
        views.Clear();
    }

    private int AddResource(string name, ResourceKind kind)
    {
        ThrowIfBackgroundCompile(nameof(AddResource));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        int index = _resources.Count;
        _resources.Add(TakeResource(name, kind));
        return index;
    }

    private Resource TakeResource(string name, ResourceKind kind)
    {
        if (_resourcePool.Count == 0)
            return new Resource(name, kind);

        Resource resource = _resourcePool.Pop();
        resource.Reset(name, kind);
        return resource;
    }

    private void RecycleResources()
    {
        for (int i = 0; i < _resources.Count; i++)
        {
            _resources[i].Reset(string.Empty, ResourceKind.Buffer);
            _resourcePool.Push(_resources[i]);
        }
    }

    private int GetResourceIndex(RenderGraphHandle handle, string operation)
    {
        if (!handle.IsValid)
            throw new InvalidOperationException($"{operation} requires a valid RenderGraph handle.");
        if (!handle.TryGetIndex(_graphId, _generation, _resources.Count, out int index))
            throw new InvalidOperationException($"{operation} received a stale or invalid RenderGraph handle.");
        return index;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RenderGraph));
    }

    private void ThrowIfActive(string operation)
    {
        int executePassIndex = ReferenceEquals(threadExecuteGraph, this) && threadExecutePassIndex > 0
            ? threadExecutePassIndex - 1
            : -1;
        int passIndex = _setupPassIndex >= 0 ? _setupPassIndex : executePassIndex;
        if (passIndex < 0 && Volatile.Read(ref _activeCallbackDepth) > 0)
            passIndex = Volatile.Read(ref _activeCallbackPassIndex);
        if (passIndex < 0)
            return;

        string timeline = _setupPassIndex >= 0 || Volatile.Read(ref _activeCallbackIsSetup) != 0
            ? "pass setup"
            : "pass execute";
        throw new InvalidOperationException(
            $"RenderGraph {operation} cannot be called during {timeline} for '{_passes[passIndex].Name}'. Pass setup may only declare resource uses through {nameof(RenderGraphBuilder)}, and pass execute may only access declared resources through {nameof(RenderGraphContext)}.");
    }
}
