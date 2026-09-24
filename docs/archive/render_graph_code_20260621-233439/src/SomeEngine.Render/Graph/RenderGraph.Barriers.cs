using System.Runtime.InteropServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Rhi;
using RhiComputePass = SomeEngine.Rhi.IComputePass;

namespace SomeEngine.Render.Graph;

public sealed partial class RenderGraph
{
    private void GatherBarriers(CompiledGraph compile)
    {
        var state = CreateBarrierState(compile);
        for (int passSlot = 0; passSlot < compile.Count; passSlot++)
        {
            int passIndex = compile.Passes[passSlot];
            var transitions = compile.Transitions[passIndex];
            transitions.Clear();
            var uses = compile.Uses[passIndex];
            for (int useIndex = 0; useIndex < uses.Count; useIndex++)
            {
                var use = uses[useIndex];
                var resource = _resources[use.ResourceIndex];
                if (resource.Kind == ResourceKind.Texture)
                    TrackTextureUse(state, transitions, resource, use);
                else
                    TrackBufferUse(state, transitions, use);
            }
        }

        BuildFinalTransitions(compile, state);
        GatherChecks(compile);
    }

    private static void GatherChecks(CompiledGraph compile)
    {
        for (int passSlot = 0; passSlot < compile.Count; passSlot++)
        {
            int passIndex = compile.Passes[passSlot];
            compile.Transitions[passIndex].GatherChecks(compile.Resources);
        }

        compile.FinalTransitions.GatherChecks(compile.Resources);
    }

    private BarrierState CreateBarrierState(CompiledGraph compile)
    {
        BarrierState state = _scratch.Barriers;
        state.Reset(_resources.Count);

        for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
        {
            var resource = _resources[resourceIndex];
            if (!compile.Resources[resourceIndex].Live)
            {
                state.ClearTexture(resourceIndex);
                continue;
            }

            if (resource.Kind == ResourceKind.Buffer)
            {
                state.Buffers[resourceIndex] = resource.Imported
                    ? resource.CurrentState
                    : (resource.BufferDesc
                        ?? throw new InvalidOperationException($"RenderGraph buffer '{resource.Name}' has no descriptor.")).InitialState;
                state.ClearTexture(resourceIndex);
                continue;
            }

            var desc = resource.TextureDesc
                ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
            ResourceState initialState = resource.Imported ? resource.CurrentState : desc.InitialState;
            state.SetTexture(resourceIndex, TextureStateCount(desc), initialState);
        }

        return state;
    }

    private static int TextureStateCount(TextureDesc desc)
        => checked((int)((ulong)desc.MipLevels * desc.ArraySize));

    private static void TrackBufferUse(BarrierState state, TransitionBatch transitions, ResourceUse use)
    {
        int resourceIndex = use.ResourceIndex;
        ResourceState current = state.Buffers[resourceIndex];
        ResourceState entryState = use.EffectiveBarrierEntryState;
        bool transition = current != entryState;
        bool uavDependency = !transition
            && entryState == ResourceState.UnorderedAccess
            && RequiresUavDependency(state.BufferAccesses[resourceIndex], use.Access);
        transitions.Add(new ResourceTransition(
            resourceIndex,
            current,
            entryState,
            use.Access,
            SubresourceRange.All,
            transition || uavDependency,
            StateMatch.Exact));

        if (transition || uavDependency)
        {
            current = entryState;
            state.Buffers[resourceIndex] = current;
            state.BufferAccesses[resourceIndex] = RenderGraphAccess.None;
        }

        ResourceState requestedExitState = use.EffectiveBarrierExitState;
        ResourceState exitState = current == requestedExitState
            ? current
            : requestedExitState;
        state.Buffers[resourceIndex] = exitState;
        if (exitState != ResourceState.UnorderedAccess)
            state.BufferAccesses[resourceIndex] = RenderGraphAccess.None;
        if (entryState == ResourceState.UnorderedAccess
            && exitState == ResourceState.UnorderedAccess)
            state.BufferAccesses[resourceIndex] |= use.Access;
    }

    private void TrackTextureUse(
        BarrierState state,
        TransitionBatch transitions,
        Resource resource,
        ResourceUse use)
    {
        var desc = resource.TextureDesc
            ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
        var range = ActualRange(desc, use.Range);
        var states = state.Textures[use.ResourceIndex];
        var accesses = state.TextureAccesses[use.ResourceIndex];

        if (range.CoversAll(desc)
            && TryWholeTexture(transitions, states, accesses, use, range, StateMatch.Exact))
        {
            TrackTextureExit(desc, range, states, accesses, use);
            return;
        }

        for (uint slice = range.FirstSlice; slice < range.FirstSlice + range.SliceCount; slice++)
        {
            for (uint mip = range.FirstMip; mip < range.FirstMip + range.MipCount; mip++)
            {
                int stateIndex = TextureStateIndex(desc, mip, slice);
                TrackTextureTransition(
                    transitions,
                    use.ResourceIndex,
                    states,
                    accesses,
                    stateIndex,
                    SingleSubresourceRange(mip, slice),
                    use,
                    StateMatch.Exact);
            }
        }

        TrackTextureExit(desc, range, states, accesses, use);
    }

    private static bool TryWholeTexture(
        TransitionBatch transitions,
        ResourceState[] states,
        RenderGraphAccess[] accesses,
        ResourceUse use,
        SubresourceRange range,
        StateMatch match)
    {
        ResourceState entryState = use.EffectiveBarrierEntryState;
        if (states.Length == 0)
            return false;

        ResourceState current = states[0];
        bool uavDependency = false;
        for (int stateIndex = 0; stateIndex < states.Length; stateIndex++)
        {
            if (states[stateIndex] != current)
                return false;
            if (current == entryState
                && entryState == ResourceState.UnorderedAccess
                && RequiresUavDependency(accesses[stateIndex], use.Access))
            {
                uavDependency = true;
            }
        }

        bool transition = match == StateMatch.Exact
            ? current != entryState
            : !ResourceStateCompatibility.Satisfies(current, entryState);
        bool required = transition || uavDependency;
        transitions.Add(new ResourceTransition(
            use.ResourceIndex,
            current,
            entryState,
            use.Access,
            range,
            required,
            match));
        if (!required)
            return true;

        for (int stateIndex = 0; stateIndex < states.Length; stateIndex++)
        {
            states[stateIndex] = entryState;
            accesses[stateIndex] = RenderGraphAccess.None;
        }

        return true;
    }

    private static void TrackTextureTransition(
        TransitionBatch transitions,
        int resourceIndex,
        ResourceState[] states,
        RenderGraphAccess[] accesses,
        int stateIndex,
        SubresourceRange range,
        ResourceUse use,
        StateMatch match)
    {
        ResourceState current = states[stateIndex];
        ResourceState entryState = use.EffectiveBarrierEntryState;
        bool transition = match == StateMatch.Exact
            ? current != entryState
            : !ResourceStateCompatibility.Satisfies(current, entryState);
        bool uavDependency = !transition
            && entryState == ResourceState.UnorderedAccess
            && RequiresUavDependency(accesses[stateIndex], use.Access);
        bool required = transition || uavDependency;

        transitions.Add(new ResourceTransition(
            resourceIndex,
            current,
            entryState,
            use.Access,
            range,
            required,
            match));
        if (!required)
            return;

        states[stateIndex] = entryState;
        accesses[stateIndex] = RenderGraphAccess.None;
    }

    private static void TrackTextureExit(
        TextureDesc desc,
        SubresourceRange range,
        ResourceState[] states,
        RenderGraphAccess[] accesses,
        ResourceUse use)
    {
        for (uint slice = range.FirstSlice; slice < range.FirstSlice + range.SliceCount; slice++)
        {
            for (uint mip = range.FirstMip; mip < range.FirstMip + range.MipCount; mip++)
            {
                int stateIndex = TextureStateIndex(desc, mip, slice);
                ResourceState entryState = use.EffectiveBarrierEntryState;
                ResourceState requestedExitState = use.EffectiveBarrierExitState;
                ResourceState exitState = ResourceStateCompatibility.Satisfies(states[stateIndex], requestedExitState)
                    ? states[stateIndex]
                    : requestedExitState;
                states[stateIndex] = exitState;
                if (exitState != ResourceState.UnorderedAccess)
                    accesses[stateIndex] = RenderGraphAccess.None;
                if (entryState == ResourceState.UnorderedAccess
                    && requestedExitState == ResourceState.UnorderedAccess)
                    accesses[stateIndex] |= use.Access;
            }
        }
    }

    private void BuildFinalTransitions(CompiledGraph compile, BarrierState state)
    {
        compile.FinalTransitions.Clear();

        for (int stateIndex = 0; stateIndex < _finalStates.Count; stateIndex++)
        {
            var finalState = _finalStates[stateIndex];
            var resource = _resources[finalState.ResourceIndex];
            if (resource.Kind == ResourceKind.Texture)
                TrackFinalTexture(compile, state, finalState.ResourceIndex, finalState.State);
            else
                TrackFinalBuffer(compile, state, finalState.ResourceIndex, finalState.State);
        }
    }

    private void TrackFinalBuffer(CompiledGraph compile, BarrierState state, int resourceIndex, ResourceState targetState)
    {
        ResourceState current = state.Buffers[resourceIndex];
        compile.FinalTransitions.Add(new ResourceTransition(
            resourceIndex,
            current,
            targetState,
            RenderGraphAccess.None,
            SubresourceRange.All,
            current != targetState,
            StateMatch.Exact));
        state.Buffers[resourceIndex] = targetState;
        state.BufferAccesses[resourceIndex] = RenderGraphAccess.None;
    }

    private void TrackFinalTexture(CompiledGraph compile, BarrierState state, int resourceIndex, ResourceState targetState)
    {
        var resource = _resources[resourceIndex];
        var desc = resource.TextureDesc
            ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
        var states = state.Textures[resourceIndex];
        var accesses = state.TextureAccesses[resourceIndex];
        var use = new ResourceUse(
            resourceIndex,
            targetState,
            targetState,
            RenderGraphAccess.None,
            SubResourceRange.All);

        if (TryWholeTexture(
            compile.FinalTransitions,
            states,
            accesses,
            use,
            SubresourceRange.All,
            StateMatch.Exact))
        {
            return;
        }

        for (uint slice = 0; slice < desc.ArraySize; slice++)
        {
            for (uint mip = 0; mip < desc.MipLevels; mip++)
            {
                int stateIndex = TextureStateIndex(desc, mip, slice);
                TrackTextureTransition(
                    compile.FinalTransitions,
                    resourceIndex,
                    states,
                    accesses,
                    stateIndex,
                    SingleSubresourceRange(mip, slice),
                    use,
                    StateMatch.Exact);
            }
        }
    }

    private void BeginCommandBarriers(CompiledGraph compile)
    {
        EnsurePassBarriers(_passes.Count);
        _resourceStates.Begin(_resources, compile);
    }

    private void PreparePassBarriers(IDevice device, CompiledGraph compile, int passIndex)
    {
        var transitions = compile.Transitions[passIndex];
        CommandBarriers barriers = _passBarriers[passIndex];
        barriers.Clear();
        int passSlot = compile.SlotOf(passIndex);
        string passName = PassName(passIndex);
        using (Profiler.BeginGraphPass(passName, passSlot, "PrepareBarriers"))
        {
            if (!transitions.IsEmpty
                && NeedsBarriers(compile, _resourceStates, transitions, out bool includeChecks, out bool includeAliases)
                && BuildBarriers(device, compile, _resourceStates, transitions, includeChecks, includeAliases, barriers))
            {
                _dirtyBarrierSlots.Add(passIndex);
            }
        }

        using (Profiler.BeginGraphPass(passName, passSlot, "PassEpilogue"))
            TrackExitStates(_resourceStates, compile, passIndex);
    }

    private void CreatePassBarriers(IDevice device, CompiledGraph compile)
    {
        for (int passSlot = 0; passSlot < compile.Count; passSlot++)
        {
            int passIndex = compile.Passes[passSlot];
            PreparePassBarriers(device, compile, passIndex);
        }

        if (compile.FinalWork)
            PrepareFinalBarriers(device, compile);
    }

    private void PrepareFinalBarriers(IDevice device, CompiledGraph compile)
    {
        _finalBarriers.Clear();
        if (!compile.FinalTransitions.IsEmpty
            && NeedsBarriers(compile, _resourceStates, compile.FinalTransitions, out bool finalChecks, out bool finalAliases))
        {
            BuildBarriers(device, compile, _resourceStates, compile.FinalTransitions, finalChecks, finalAliases, _finalBarriers);
        }
    }

    private void CommitResourceStates(CompiledGraph compile)
        => _resourceStates.Commit(_resources, compile);

    private void EnsurePassBarriers(int passCount)
    {
        if (_passBarriers.Length < passCount)
        {
            int oldCount = _passBarriers.Length;
            Array.Resize(ref _passBarriers, passCount);
            for (int passIndex = oldCount; passIndex < _passBarriers.Length; passIndex++)
                _passBarriers[passIndex] = new CommandBarriers();
        }

        for (int i = 0; i < _dirtyBarrierSlots.Count; i++)
        {
            int passIndex = _dirtyBarrierSlots[i];
            if ((uint)passIndex < (uint)_passBarriers.Length)
                _passBarriers[passIndex].Clear();
        }

        _dirtyBarrierSlots.Clear();
        _finalBarriers.Clear();
    }

    private static bool HasBarriers(CommandBarriers barriers)
        => !barriers.IsEmpty;

    private void EmitBarriers(ICommandList list, CommandBarriers barriers)
    {
        if (!HasBarriers(barriers))
            return;

        Profiler.RenderGraphBarriers(
            barriers.Textures.Count,
            barriers.Buffers.Count,
            barriers.Aliases.Count);
        list.Barrier(
            CollectionsMarshal.AsSpan(barriers.Textures),
            CollectionsMarshal.AsSpan(barriers.Buffers),
            CollectionsMarshal.AsSpan(barriers.Aliases));
    }

    private void EmitBarriers(RhiComputePass list, CommandBarriers barriers)
    {
        if (!HasBarriers(barriers))
            return;

        Profiler.RenderGraphBarriers(
            barriers.Textures.Count,
            barriers.Buffers.Count,
            barriers.Aliases.Count);
        list.Barrier(
            CollectionsMarshal.AsSpan(barriers.Textures),
            CollectionsMarshal.AsSpan(barriers.Buffers),
            CollectionsMarshal.AsSpan(barriers.Aliases));
    }

    private bool NeedsBarriers(
        CompiledGraph compile,
        ResourceStateTracker states,
        TransitionBatch transitions,
        out bool includeChecks,
        out bool includeAliases)
    {
        includeChecks = false;
        includeAliases = false;
        if (transitions.IsEmpty)
            return false;

        includeAliases = NeedsAlias(compile, states, transitions);
        includeChecks = NeedsChecks(compile, states, transitions);
        return transitions.HasRequired || includeAliases || includeChecks;
    }

    private bool BuildBarriers(
        IDevice device,
        CompiledGraph compile,
        ResourceStateTracker states,
        TransitionBatch transitions,
        bool includeChecks,
        bool includeAliases,
        CommandBarriers barriers)
    {
        if (includeAliases)
        {
            if (transitions.SharedAliasResources.Length != 0)
            {
                foreach (int resourceIndex in transitions.SharedAliasResources)
                    AddAlias(device, compile, states, resourceIndex, _resources[resourceIndex], barriers);
            }
            else
            {
                foreach (int resourceIndex in transitions.AliasResources)
                    AddAlias(device, compile, states, resourceIndex, _resources[resourceIndex], barriers);
            }
        }

        IReadOnlyList<ResourceTransition> requiredTransitions = transitions.RequiredTransitionSource;
        for (int transitionIndex = 0; transitionIndex < requiredTransitions.Count; transitionIndex++)
            AddResourceBarrier(compile, states, requiredTransitions[transitionIndex], barriers);

        if (includeChecks)
        {
            if (transitions.SharedCheckTransitions.Length != 0)
            {
                foreach (var transition in transitions.SharedCheckTransitions)
                {
                    if (compile.Resources[transition.ResourceIndex].Reusable)
                        AddResourceBarrier(compile, states, transition, barriers);
                }
            }
            else
            {
                foreach (var transition in transitions.CheckTransitions)
                {
                    if (compile.Resources[transition.ResourceIndex].Reusable)
                        AddResourceBarrier(compile, states, transition, barriers);
                }
            }
        }

        if (barriers.IsEmpty)
            return false;

        return true;
    }

    private void AddResourceBarrier(
        CompiledGraph compile,
        ResourceStateTracker states,
        ResourceTransition transition,
        CommandBarriers barriers)
    {
        var resource = _resources[transition.ResourceIndex];
        var record = compile.Resources[transition.ResourceIndex];
        if (TrustsState(record, resource, states, transition.ResourceIndex))
        {
            if (transition.Required)
                AddKnownBarrier(states, transition.ResourceIndex, resource, transition, barriers);
            return;
        }

        if (StateSatisfied(resource, states, transition))
        {
            states.Trusted[transition.ResourceIndex] = !record.Aliased
                && !resource.Imported
                && states.States[transition.ResourceIndex] != ResourceState.Undefined;
            return;
        }

        if (resource.Kind == ResourceKind.Texture)
        {
            if (transition.Required && TryDirectTexture(states, transition.ResourceIndex, resource, transition, barriers))
                return;

            AddTextureBarriers(
                states,
                transition.ResourceIndex,
                resource,
                transition.After,
                transition.Range,
                transition.Access,
                barriers.Textures);
            states.Trusted[transition.ResourceIndex] = states.States[transition.ResourceIndex] != ResourceState.Undefined;
            return;
        }

        bool satisfied = transition.Match == StateMatch.Exact
            ? states.States[transition.ResourceIndex] == transition.After
            : ResourceStateCompatibility.Satisfies(states.States[transition.ResourceIndex], transition.After);
        if (satisfied)
        {
            if (transition.After == ResourceState.UnorderedAccess
                && RequiresUavDependency(states.BufferUavAccesses[transition.ResourceIndex], transition.Access))
            {
                barriers.AddBufferUav(resource.Buffer);
                states.BufferUavAccesses[transition.ResourceIndex] = RenderGraphAccess.None;
            }
            states.Trusted[transition.ResourceIndex] = !record.Aliased
                && !resource.Imported
                && states.States[transition.ResourceIndex] != ResourceState.Undefined;
            return;
        }

        barriers.AddBuffer(resource.Buffer, states.States[transition.ResourceIndex], transition.After);
        states.States[transition.ResourceIndex] = transition.After;
        states.BufferUavAccesses[transition.ResourceIndex] = RenderGraphAccess.None;
        states.Trusted[transition.ResourceIndex] = true;
    }

    private static bool TrustsState(
        ResourceRecord record,
        Resource resource,
        ResourceStateTracker states,
        int resourceIndex)
        => states.Trusted[resourceIndex]
            && !record.Aliased
            && !resource.Imported;

    private void AddKnownBarrier(
        ResourceStateTracker states,
        int resourceIndex,
        Resource resource,
        ResourceTransition transition,
        CommandBarriers barriers)
    {
        if (resource.Kind == ResourceKind.Buffer)
        {
            if (transition.Before == ResourceState.UnorderedAccess
                && transition.After == ResourceState.UnorderedAccess)
            {
                barriers.AddBufferUav(resource.Buffer);
            }
            else if (transition.Before != transition.After)
            {
                barriers.AddBuffer(resource.Buffer, transition.Before, transition.After);
            }
            else
            {
                states.States[resourceIndex] = transition.After;
                states.BufferUavAccesses[resourceIndex] = RenderGraphAccess.None;
                states.Trusted[resourceIndex] = true;
                return;
            }

            states.States[resourceIndex] = transition.After;
            states.BufferUavAccesses[resourceIndex] = RenderGraphAccess.None;
            states.Trusted[resourceIndex] = true;
            return;
        }

        if (transition.Before == transition.After
            && transition.After != ResourceState.UnorderedAccess)
        {
            SetKnownTexture(states, resourceIndex, resource, transition.Range, transition.After);
            return;
        }

            barriers.Textures.Add(new TextureBarrier(
                resource.Texture,
                transition.Before,
                transition.After,
                transition.Range));
        SetKnownTexture(states, resourceIndex, resource, transition.Range, transition.After);
    }

    private static void SetKnownTexture(
        ResourceStateTracker tracker,
        int resourceIndex,
        Resource resource,
        SomeEngine.Rhi.SubresourceRange range,
        ResourceState state)
    {
        var desc = resource.TextureDesc
            ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
        var states = tracker.TextureStates[resourceIndex];
        if (range.CoversAll(desc))
        {
            Array.Fill(states, state);
            Array.Clear(tracker.TextureUavAccesses[resourceIndex]);
            UpdateTextureState(tracker, resourceIndex);
            tracker.Trusted[resourceIndex] = tracker.States[resourceIndex] != ResourceState.Undefined;
            return;
        }

        for (uint slice = range.FirstSlice; slice < range.FirstSlice + range.SliceCount; slice++)
        {
            for (uint mip = range.FirstMip; mip < range.FirstMip + range.MipCount; mip++)
            {
                int stateIndex = TextureStateIndex(desc, mip, slice);
                states[stateIndex] = state;
                ClearUavAccess(tracker, resourceIndex, stateIndex);
            }
        }

        UpdateTextureState(tracker, resourceIndex);
        tracker.Trusted[resourceIndex] = tracker.States[resourceIndex] != ResourceState.Undefined;
    }

    private bool TryDirectTexture(
        ResourceStateTracker states,
        int resourceIndex,
        Resource resource,
        ResourceTransition transition,
        CommandBarriers barriers)
    {
        if (transition.Before == ResourceState.UnorderedAccess
            && transition.After == ResourceState.UnorderedAccess)
        {
            return false;
        }

        var desc = resource.TextureDesc
            ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
        if (!transition.Range.CoversAll(desc))
            return false;
        if (states.States[resourceIndex] != transition.Before)
            return false;

        barriers.Textures.Add(new TextureBarrier(
            resource.Texture,
            transition.Before,
            transition.After,
            SomeEngine.Rhi.SubresourceRange.All));

        ResourceState[] textureStates = states.TextureStates[resourceIndex];
        Array.Fill(textureStates, transition.After);
        Array.Clear(states.TextureUavAccesses[resourceIndex]);
        UpdateTextureState(states, resourceIndex);
        states.Trusted[resourceIndex] = true;
        return true;
    }

    private static bool StateSatisfied(
        Resource resource,
        ResourceStateTracker states,
        ResourceTransition transition)
    {
        if (resource.Kind == ResourceKind.Buffer)
        {
            bool satisfied = transition.Match == StateMatch.Exact
                ? states.States[transition.ResourceIndex] == transition.After
                : ResourceStateCompatibility.Satisfies(states.States[transition.ResourceIndex], transition.After);
            if (!satisfied)
                return false;
            return transition.After != ResourceState.UnorderedAccess
                || !RequiresUavDependency(states.BufferUavAccesses[transition.ResourceIndex], transition.Access);
        }

        if (transition.Range.FirstMip != 0
            || transition.Range.MipCount != uint.MaxValue
            || transition.Range.FirstSlice != 0
            || transition.Range.SliceCount != uint.MaxValue)
        {
            return false;
        }

        bool textureSatisfied = transition.Match == StateMatch.Exact
            ? states.States[transition.ResourceIndex] == transition.After
            : ResourceStateCompatibility.Satisfies(states.States[transition.ResourceIndex], transition.After);
        return states.States[transition.ResourceIndex] != ResourceState.Undefined
            && textureSatisfied
            && transition.After != ResourceState.UnorderedAccess;
    }

    private void AddAlias(
        IDevice device,
        CompiledGraph compile,
        ResourceStateTracker states,
        int resourceIndex,
        Resource resource,
        CommandBarriers barriers)
    {
        var record = compile.Resources[resourceIndex];
        if (!record.Aliased || states.AliasReady[resourceIndex])
            return;

        var slot = AliasSlot(device, resource);
        AliasingResource before = states.ActiveAliases.TryGetValue(slot, out var active)
            ? active
            : AliasingResource.None;
        AliasingResource after = AliasEndpoint(resource);
        if (before == after)
        {
            states.AliasReady[resourceIndex] = true;
            return;
        }

        barriers.Aliases.Add(new AliasingBarrier { Before = before, After = after });
        states.ActiveAliases[slot] = after;
        states.AliasReady[resourceIndex] = true;
    }

    private static AliasSlotKey AliasSlot(IDevice device, Resource resource)
    {
        ResourceAllocationInfo allocation = resource.Kind == ResourceKind.Texture
            ? device.GetTextureAlloc(resource.Texture)
            : device.GetBufferAlloc(resource.Buffer);
        if (allocation.Ownership != ResourceOwnership.Placed || !allocation.Heap.IsValid)
        {
            throw new InvalidOperationException(
                $"RenderGraph aliased resource '{resource.Name}' was resolved without a placed allocation.");
        }

        return new AliasSlotKey(allocation.Heap, allocation.HeapOffset);
    }

    private static AliasingResource AliasEndpoint(Resource resource)
        => resource.Kind == ResourceKind.Texture
            ? AliasingResource.TextureResource(resource.Texture)
            : AliasingResource.BufferResource(resource.Buffer);

    private bool NeedsAlias(CompiledGraph compile, ResourceStateTracker states, TransitionBatch transitions)
    {
        if (!transitions.HasAliases)
            return false;

        if (transitions.SharedAliasResources.Length != 0)
        {
            for (int i = 0; i < transitions.SharedAliasResources.Length; i++)
            {
                int resourceIndex = transitions.SharedAliasResources[i];
                if (!states.AliasReady[resourceIndex])
                    return true;
            }
        }
        else
        {
            for (int i = 0; i < transitions.AliasResources.Count; i++)
            {
                int resourceIndex = transitions.AliasResources[i];
                if (!states.AliasReady[resourceIndex])
                    return true;
            }
        }

        return false;
    }

    private bool NeedsChecks(CompiledGraph compile, ResourceStateTracker states, TransitionBatch transitions)
        => !CanSkipChecks(compile, states, transitions);

    private bool CanSkipChecks(CompiledGraph compile, ResourceStateTracker states, TransitionBatch transitions)
    {
        if (!transitions.HasChecks)
            return true;

        if (transitions.SharedCheckResources.Length != 0)
        {
            for (int i = 0; i < transitions.SharedCheckResources.Length; i++)
            {
                int resourceIndex = transitions.SharedCheckResources[i];
                var record = compile.Resources[resourceIndex];
                if (!TrustsState(record, _resources[resourceIndex], states, resourceIndex))
                    return false;
            }
        }
        else
        {
            for (int i = 0; i < transitions.CheckResources.Count; i++)
            {
                int resourceIndex = transitions.CheckResources[i];
                var record = compile.Resources[resourceIndex];
                if (!TrustsState(record, _resources[resourceIndex], states, resourceIndex))
                    return false;
            }
        }

        return true;
    }

    private void TrackExitStates(ResourceStateTracker states, CompiledGraph compile, int passIndex)
    {
        PassEpilogueState[] epilogue = compile.PassEpilogues[passIndex];
        foreach (PassEpilogueState use in epilogue)
        {
            var resource = _resources[use.ResourceIndex];
            bool trusted = !resource.Imported && !compile.Resources[use.ResourceIndex].Aliased;
            if (resource.Kind == ResourceKind.Texture)
            {
                ResourceState entryState = use.EntryState;
                ResourceState exitState = use.ExitState;
                SetTextureState(states, use.ResourceIndex, resource, use.Range, exitState);
                if (entryState == ResourceState.UnorderedAccess
                    && exitState == ResourceState.UnorderedAccess)
                    SetUavAccess(states, use.ResourceIndex, resource, use.Range, use.Access);
                states.Trusted[use.ResourceIndex] = trusted && states.States[use.ResourceIndex] != ResourceState.Undefined;
            }
            else
            {
                ResourceState currentState = states.States[use.ResourceIndex];
                ResourceState entryState = use.EntryState;
                ResourceState requestedExitState = use.ExitState;
                ResourceState exitState = currentState == requestedExitState
                    ? currentState
                    : requestedExitState;
                states.States[use.ResourceIndex] = exitState;
                if (exitState != ResourceState.UnorderedAccess)
                    states.BufferUavAccesses[use.ResourceIndex] = RenderGraphAccess.None;
                if (entryState == ResourceState.UnorderedAccess
                    && exitState == ResourceState.UnorderedAccess)
                    states.BufferUavAccesses[use.ResourceIndex] |= use.Access;
                states.Trusted[use.ResourceIndex] = trusted && states.States[use.ResourceIndex] != ResourceState.Undefined;
            }
        }
    }

    private static void SetTextureState(
        ResourceStateTracker states,
        int resourceIndex,
        Resource resource,
        SubResourceRange range,
        ResourceState state)
    {
        var desc = resource.TextureDesc
            ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
        var actual = ActualRange(desc, range);
        var textureStates = states.TextureStates[resourceIndex];
        if (actual.CoversAll(desc))
        {
            Array.Fill(textureStates, state);
            if (state != ResourceState.UnorderedAccess)
                Array.Clear(states.TextureUavAccesses[resourceIndex]);
            states.States[resourceIndex] = state;
            return;
        }

        for (uint slice = actual.FirstSlice; slice < actual.FirstSlice + actual.SliceCount; slice++)
        {
            for (uint mip = actual.FirstMip; mip < actual.FirstMip + actual.MipCount; mip++)
            {
                int stateIndex = TextureStateIndex(desc, mip, slice);
                textureStates[stateIndex] = state;
                if (state != ResourceState.UnorderedAccess)
                    ClearUavAccess(states, resourceIndex, stateIndex);
            }
        }

        UpdateTextureState(states, resourceIndex);
    }

    private static void SetUavAccess(
        ResourceStateTracker states,
        int resourceIndex,
        Resource resource,
        SubResourceRange range,
        RenderGraphAccess access)
    {
        if (access == RenderGraphAccess.None)
            return;

        var desc = resource.TextureDesc
            ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
        var actual = ActualRange(desc, range);
        var accesses = states.TextureUavAccesses[resourceIndex];
        for (uint slice = actual.FirstSlice; slice < actual.FirstSlice + actual.SliceCount; slice++)
        {
            for (uint mip = actual.FirstMip; mip < actual.FirstMip + actual.MipCount; mip++)
                accesses[TextureStateIndex(desc, mip, slice)] |= access;
        }
    }


    private static void UpdateTextureState(ResourceStateTracker states, int resourceIndex)
    {
        var textureStates = states.TextureStates[resourceIndex];
        states.States[resourceIndex] = TryUniformState(textureStates, out var state)
            ? state
            : ResourceState.Undefined;
    }

    private static bool TryUniformState(ResourceState[] states, out ResourceState state)
    {
        state = states[0];
        for (int i = 1; i < states.Length; i++)
        {
            if (states[i] != state)
                return false;
        }

        return true;
    }

    private static void ClearUavAccess(ResourceStateTracker states, int resourceIndex, int stateIndex)
    {
        var accesses = states.TextureUavAccesses[resourceIndex];
        accesses[stateIndex] = RenderGraphAccess.None;
    }

    private static void AddTextureBarriers(
        ResourceStateTracker tracker,
        int resourceIndex,
        Resource resource,
        ResourceState targetState,
        SomeEngine.Rhi.SubresourceRange range,
        RenderGraphAccess currentAccess,
        List<TextureBarrier> barriers)
    {
        var desc = resource.TextureDesc
            ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
        var actual = ActualRange(desc, range);
        var states = tracker.TextureStates[resourceIndex];
        if (actual.CoversAll(desc)
            && TryWholeBarrier(tracker, resourceIndex, resource, targetState, currentAccess, states, barriers))
        {
            UpdateTextureState(tracker, resourceIndex);
            return;
        }

        for (uint slice = actual.FirstSlice; slice < actual.FirstSlice + actual.SliceCount; slice++)
        {
            for (uint mip = actual.FirstMip; mip < actual.FirstMip + actual.MipCount; mip++)
            {
                int stateIndex = TextureStateIndex(desc, mip, slice);
                ResourceState currentState = states[stateIndex];
                if (currentState == targetState)
                {
                    if (targetState == ResourceState.UnorderedAccess
                        && RequiresUavDependency(GetUavAccess(tracker, resourceIndex, stateIndex), currentAccess))
                    {
                        barriers.Add(new TextureBarrier(
                            resource.Texture,
                            ResourceState.UnorderedAccess,
                            ResourceState.UnorderedAccess,
                            SingleSubresourceRange(mip, slice)));
                        ClearUavAccess(tracker, resourceIndex, stateIndex);
                    }

                    continue;
                }

                barriers.Add(new TextureBarrier(
                    resource.Texture,
                    currentState,
                    targetState,
                    SingleSubresourceRange(mip, slice)));
                states[stateIndex] = targetState;
                ClearUavAccess(tracker, resourceIndex, stateIndex);
            }
        }

        UpdateTextureState(tracker, resourceIndex);
    }

    private static bool TryWholeBarrier(
        ResourceStateTracker tracker,
        int resourceIndex,
        Resource resource,
        ResourceState targetState,
        RenderGraphAccess currentAccess,
        ResourceState[] states,
        List<TextureBarrier> barriers)
    {
        if (states.Length == 0)
            return false;

        ResourceState currentState = states[0];
        bool needsUavDependency = false;
        for (int stateIndex = 0; stateIndex < states.Length; stateIndex++)
        {
            if (states[stateIndex] != currentState)
                return false;
            if (currentState == targetState
                && targetState == ResourceState.UnorderedAccess
                && RequiresUavDependency(GetUavAccess(tracker, resourceIndex, stateIndex), currentAccess))
            {
                needsUavDependency = true;
            }
        }

        if (currentState == targetState)
        {
            if (!needsUavDependency)
                return true;

            barriers.Add(new TextureBarrier(
                resource.Texture,
                ResourceState.UnorderedAccess,
                ResourceState.UnorderedAccess,
                SomeEngine.Rhi.SubresourceRange.All));
        }
        else
        {
            barriers.Add(new TextureBarrier(
                resource.Texture,
                currentState,
                targetState,
                SomeEngine.Rhi.SubresourceRange.All));
        }

        for (int stateIndex = 0; stateIndex < states.Length; stateIndex++)
        {
            states[stateIndex] = targetState;
            ClearUavAccess(tracker, resourceIndex, stateIndex);
        }

        return true;
    }

    private static RenderGraphAccess GetUavAccess(ResourceStateTracker states, int resourceIndex, int stateIndex)
    {
        var accesses = states.TextureUavAccesses[resourceIndex];
        return accesses[stateIndex];
    }

}
