using SomeEngine.Core.Collections;

namespace SomeEngine.Rhi;

internal struct CommandState : IDisposable
{
    private InlineFlatCore<BufferHandle, BufferTransition> _buffers;
    private InlineFlatCore<SubresourceKey, TextureSlot> _textures;
    private InlineList<TextureSpan, Inline8<TextureSpan>> _textureSpans;
    private InlineList<TextureTransition, Inline8<TextureTransition>> _textureEvents;
    private int _textureOrder;

    public ResourceState GetBuffer(BufferHandle buffer, ResourceState current)
    {
        if (_buffers.TryGetValue(buffer, out var transition))
            return transition.FinalState;
        _buffers.Add(buffer, new BufferTransition(buffer, current, current, Commit: false));
        return current;
    }

    public ResourceState GetTexture(SubresourceKey key, ResourceState current)
    {
        if (TryTexture(key, out var slot))
            return slot.State;
        SetTextureSlot(key, current);
        AddTextureEvent(new TextureTransition(key.Texture, SingleRange(key), current, current, Commit: false));
        return current;
    }

    public void RequireBuffer(BufferHandle buffer, ResourceState current, ResourceState expected, string label, string scope)
    {
        if (_buffers.TryGetValue(buffer, out var transition))
        {
            if (!ResourceStateCompatibility.Satisfies(transition.FinalState, expected))
                throw new RhiException(ErrorCode.ValidationFailure, $"{label} requires buffer state {expected}, actual {scope} state is {transition.FinalState}.");
            return;
        }

        var initial = ResourceStateCompatibility.Satisfies(current, expected) ? current : expected;
        _buffers.Add(buffer, new BufferTransition(buffer, initial, initial, Commit: false));
    }

    public void ValidateBuffer(BufferHandle buffer, ResourceState current, ResourceState expected, string label, string scope)
    {
        ResourceState actual = _buffers.TryGetValue(buffer, out var transition)
            ? transition.FinalState
            : current;
        if (!ResourceStateCompatibility.Satisfies(actual, expected))
            throw new RhiException(ErrorCode.ValidationFailure, $"{label} requires buffer state {expected}, actual {scope} state is {actual}.");
    }

    public void ExpectBuffer(BufferHandle buffer, ResourceState expected, string label, string scope)
    {
        if (_buffers.TryGetValue(buffer, out var transition))
        {
            if (transition.FinalState != expected)
                throw new RhiException(ErrorCode.ValidationFailure, $"{label} expected buffer state {expected}, actual {scope} state is {transition.FinalState}.");
            return;
        }

        _buffers.Add(buffer, new BufferTransition(buffer, expected, expected, Commit: false));
    }

    public void SetBuffer(BufferHandle buffer, ResourceState current, ResourceState state)
    {
        int slot = _buffers.GetOrAddSlot(buffer, out bool exists);
        BufferTransition transition = exists
            ? _buffers.GetValueAt(slot)
            : new BufferTransition(buffer, current, current, Commit: false);
        transition = transition with { FinalState = state, Commit = true };
        _buffers.SetValueAt(slot, transition);
    }

    public void SetBufferFrom(BufferHandle buffer, ResourceState before, ResourceState after)
    {
        int slot = _buffers.GetOrAddSlot(buffer, out bool exists);
        BufferTransition transition = exists
            ? _buffers.GetValueAt(slot)
            : new BufferTransition(buffer, before, before, Commit: false);
        transition = transition with { FinalState = after, Commit = true };
        _buffers.SetValueAt(slot, transition);
    }

    public void EnsureAdditionalBufferCapacity(int additionalCount)
        => _buffers.EnsureCapacity(checked(_buffers.Count + additionalCount));

    public void ClearNoResize()
    {
        _buffers.ClearNoResize();
        _textures.ClearNoResize();
        _textureSpans.Clear();
        _textureEvents.Clear();
        _textureOrder = 0;
    }

    public void RequireTexture(SubresourceKey key, ResourceState expected, string label, string scope)
    {
        if (TryTexture(key, out var slot))
        {
            if (slot.State != expected)
                throw new RhiException(ErrorCode.ValidationFailure, $"{label} requires texture state {expected}, actual {scope} state is {slot.State}.");
            return;
        }

        SetTextureSlot(key, expected);
        AddTextureEvent(new TextureTransition(key.Texture, SingleRange(key), expected, expected, Commit: false));
    }

    public void ValidateTexture(SubresourceKey key, ResourceState current, ResourceState expected, string label, string scope)
    {
        ResourceState actual = TryTexture(key, out var slot) ? slot.State : current;
        if (actual != expected)
            throw new RhiException(ErrorCode.ValidationFailure, $"{label} requires texture state {expected}, actual {scope} state is {actual}.");
    }

    public void ExpectTexture(SubresourceKey key, ResourceState expected, string label, string scope)
    {
        if (TryTexture(key, out var slot))
        {
            if (slot.State != expected)
                throw new RhiException(ErrorCode.ValidationFailure, $"{label} expected texture state {expected}, actual {scope} state is {slot.State}.");
            return;
        }

        SetTextureSlot(key, expected);
        AddTextureEvent(new TextureTransition(key.Texture, SingleRange(key), expected, expected, Commit: false));
    }

    public void CheckTexture(TextureHandle texture, SubresourceRange range, ResourceState expected, string label, string scope)
    {
        uint sliceEnd = RangeEnd(range.FirstSlice, range.SliceCount);
        uint mipEnd = RangeEnd(range.FirstMip, range.MipCount);
        for (uint slice = range.FirstSlice; slice < sliceEnd; slice++)
        {
            for (uint mip = range.FirstMip; mip < mipEnd; mip++)
            {
                var key = new SubresourceKey(texture, mip, slice);
                if (!TryTexture(key, out var slot))
                    continue;
                if (slot.State != expected)
                    throw new RhiException(ErrorCode.ValidationFailure, $"{label} expected texture state {expected}, actual {scope} state is {slot.State}.");
            }
        }
    }

    public void SetTexture(SubresourceKey key, ResourceState current, ResourceState state)
    {
        ResourceState before = TryTexture(key, out var slot) ? slot.State : current;
        SetTextureSlot(key, state);
        AddTextureEvent(new TextureTransition(key.Texture, SingleRange(key), before, state, Commit: true));
    }

    public void SetTextureFrom(SubresourceKey key, ResourceState before, ResourceState after)
    {
        ResourceState current = TryTexture(key, out var slot) ? slot.State : before;
        SetTextureSlot(key, after);
        AddTextureEvent(new TextureTransition(key.Texture, SingleRange(key), current, after, Commit: true));
    }

    public void SetTextureRange(TextureHandle texture, SubresourceRange range, ResourceState before, ResourceState after)
    {
        int order = ++_textureOrder;
        _textureSpans.Add(new TextureSpan(texture, range, after, order));
        AddTextureEvent(new TextureTransition(texture, range, before, after, Commit: true));
    }

    public void ApplyBuffer(BufferTransition transition, ResourceState current, string name)
    {
        int slot = _buffers.GetOrAddSlot(transition.Buffer, out bool exists);
        BufferTransition stored = exists
            ? _buffers.GetValueAt(slot)
            : new BufferTransition(transition.Buffer, current, current, Commit: false);

        ResourceState actual = stored.FinalState;
        if (actual != transition.OriginalState)
        {
            throw new RhiException(
                ErrorCode.ValidationFailure,
                $"Command buffer expected buffer '{name}' state {transition.OriginalState}, current state is {actual}.");
        }

        _buffers.SetValueAt(slot, transition with { Commit = false });
    }

    public void ApplyTexture(SubresourceKey key, ResourceState before, ResourceState after, ResourceState current, string name)
    {
        ResourceState actual = TryTexture(key, out var slot) ? slot.State : current;
        if (actual != before)
        {
            throw new RhiException(
                ErrorCode.ValidationFailure,
                $"Command buffer expected texture '{name}' mip {key.MipLevel} slice {key.ArraySlice} state {before}, current state is {actual}.");
        }

        SetTextureSlot(key, after);
    }

    public BufferTransition[] CopyBuffers()
        => CopyValues(ref _buffers);

    public TextureTransition[] CopyTextures()
        => _textureEvents.Count == 0 ? [] : _textureEvents.AsSpan().ToArray();

    public void Dispose()
    {
        _buffers.Dispose();
        _textures.Dispose();
    }

    private bool TryTexture(SubresourceKey key, out TextureSlot slot)
    {
        bool found = _textures.TryGetValue(key, out slot);
        foreach (var span in _textureSpans)
        {
            if (span.Texture != key.Texture || !RangeContains(span.Range, key))
                continue;
            if (found && slot.Order > span.Order)
                continue;
            slot = new TextureSlot(span.State, span.Order);
            found = true;
        }

        return found;
    }

    private void SetTextureSlot(SubresourceKey key, ResourceState state)
        => _textures.Set(key, new TextureSlot(state, ++_textureOrder));

    private void AddTextureEvent(TextureTransition transition)
        => _textureEvents.Add(transition);

    private static SubresourceRange SingleRange(SubresourceKey key)
        => new(key.MipLevel, 1, key.ArraySlice, 1);

    private static bool RangeContains(SubresourceRange range, SubresourceKey key)
        => key.MipLevel >= range.FirstMip
            && key.MipLevel < RangeEnd(range.FirstMip, range.MipCount)
            && key.ArraySlice >= range.FirstSlice
            && key.ArraySlice < RangeEnd(range.FirstSlice, range.SliceCount);

    private static uint RangeEnd(uint start, uint count)
        => count == uint.MaxValue ? uint.MaxValue : checked(start + count);

    private static TValue[] CopyValues<TKey, TValue>(ref InlineFlatCore<TKey, TValue> values)
        where TKey : notnull
    {
        if (values.Count == 0)
            return [];
        var copy = new TValue[values.Count];
        int count = 0;
        for (int slot = 0; slot < values.SlotCount; slot++)
        {
            if (values.TryPairSlot(slot, out var pair))
                copy[count++] = pair.Value;
        }

        return copy;
    }
}

internal readonly record struct SubresourceKey(TextureHandle Texture, uint MipLevel, uint ArraySlice);

internal readonly record struct BufferTransition(BufferHandle Buffer, ResourceState OriginalState, ResourceState FinalState, bool Commit);

internal readonly record struct TextureTransition(TextureHandle Texture, SubresourceRange Range, ResourceState OriginalState, ResourceState FinalState, bool Commit);

internal readonly record struct TextureSlot(ResourceState State, int Order);

internal readonly record struct TextureSpan(TextureHandle Texture, SubresourceRange Range, ResourceState State, int Order);
