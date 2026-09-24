using System.Buffers;

namespace SomeEngine.Rhi;

internal static class RhiBindingValidation
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private const int StackKeyLimit = 64;

    public static bool HasShapeRule(BindingSlotDesc slot)
        => (slot.Flags & BindingFlags.PartiallyBound) != 0
            || slot.Shape.Format != Format.Unknown
            || slot.Shape.StrideInBytes != 0
            || slot.Shape.TextureDimension != TextureViewDimension.Texture2D;

    public static void ValidateResourceLayout(BindingLayoutDesc layout, ReadOnlySpan<BindingResourceDesc> resources)
    {
        int slotCount = layout.Slots.Count;
        int[]? rentedCounts = null;
        BindingElementKey[]? rentedKeys = null;
        Span<int> resourceCounts = slotCount <= StackKeyLimit
            ? stackalloc int[slotCount]
            : (rentedCounts = ArrayPool<int>.Shared.Rent(slotCount)).AsSpan(0, slotCount);
        Span<BindingElementKey> resourceKeys = resources.Length <= StackKeyLimit
            ? stackalloc BindingElementKey[resources.Length]
            : (rentedKeys = ArrayPool<BindingElementKey>.Shared.Rent(resources.Length)).AsSpan(0, resources.Length);

        resourceCounts.Clear();
        try
        {
            for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
            {
                var resource = resources[resourceIndex];
                int slotIndex = FindSlotIndex(layout.Slots, resource.Binding, resource.ResourceType);
                if (slotIndex < 0)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding resource targets undeclared binding {resource.Binding} for {resource.ResourceType}.");
                var slot = layout.Slots[slotIndex];
                resourceCounts[slotIndex]++;
                resourceKeys[resourceIndex] = new BindingElementKey(resource.Binding, resource.ArrayElement, RegisterClass(resource.ResourceType));
                if (resource.ResourceType != slot.Type)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {slot.Binding} expects {slot.Type}, got {resource.ResourceType}.");
                if (resource.ArrayElement >= slot.Count)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {slot.Binding} array index {resource.ArrayElement} exceeds descriptor count {slot.Count}.");
            }

            CheckDupes(resourceKeys, "Binding");

            for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
            {
                var slot = layout.Slots[slotIndex];
                int resourceCount = resourceCounts[slotIndex];
                if ((slot.Flags & BindingFlags.PartiallyBound) == 0 && resourceCount != slot.Count)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {slot.Binding} expects {slot.Count} descriptors, got {resourceCount}.");
                if (resourceCount > slot.Count)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {slot.Binding} has {resourceCount} descriptors, exceeding declared count {slot.Count}.");
            }
        }
        finally
        {
            if (rentedCounts != null)
                ArrayPool<int>.Shared.Return(rentedCounts);
            if (rentedKeys != null)
                ArrayPool<BindingElementKey>.Shared.Return(rentedKeys);
        }
    }

    public static void ValidateTransient(BindingLayoutDesc layout)
    {
        for (int slotIndex = 0; slotIndex < layout.Slots.Count; slotIndex++)
        {
            var slot = layout.Slots[slotIndex];
            if ((slot.Flags & (BindingFlags.PartiallyBound | BindingFlags.Bindless)) != 0)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Transient SetBindings does not support partially bound or bindless slots; create a BindingSet and update it instead.");
        }
    }

    public static void ValidateUpdates(BindingLayoutDesc layout, ReadOnlySpan<BindingResourceDesc> updates)
    {
        for (int updateIndex = 0; updateIndex < updates.Length; updateIndex++)
        {
            var update = updates[updateIndex];
            var slot = update.ResourceType == BindingType.None
                ? FindSingleSlot(layout.Slots, update.Binding)
                : FindSlot(layout.Slots, update.Binding, update.ResourceType);
            if (slot == null)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding update targets undeclared binding {update.Binding}.");
            if (update.ArrayElement >= slot.Count)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {update.Binding} array element {update.ArrayElement} exceeds descriptor count {slot.Count}.");
            if (update.ResourceType == BindingType.None
                && (slot.Flags & BindingFlags.PartiallyBound) == 0)
            {
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {update.Binding} cannot be cleared because the slot is not partially bound.");
            }
        }
    }

    public static BindingResourceDesc[] MergeResources(ReadOnlySpan<BindingResourceDesc> current, ReadOnlySpan<BindingResourceDesc> updates)
    {
        var merged = new BindingResourceDesc[current.Length + updates.Length];
        for (int index = 0; index < current.Length; index++)
            merged[index] = current[index];
        int count = current.Length;

        for (int updateIndex = 0; updateIndex < updates.Length; updateIndex++)
        {
            var update = updates[updateIndex];
            for (int previous = 0; previous < updateIndex; previous++)
            {
                var candidate = updates[previous];
                if (candidate.Binding == update.Binding
                    && candidate.ArrayElement == update.ArrayElement
                    && ShareKey(candidate, update))
                {
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {update.Binding} array element {update.ArrayElement} is duplicated in the update.");
                }
            }

            int existing = update.ResourceType == BindingType.None
                ? FindResourceIndex(merged.AsSpan(0, count), update.Binding, update.ArrayElement)
                : FindResourceIndex(merged.AsSpan(0, count), update.Binding, update.ArrayElement, update.ResourceType);
            if (update.ResourceType == BindingType.None)
            {
                if (existing < 0)
                    continue;
                merged[existing] = merged[--count];
                merged[count] = default;
            }
            else if (existing >= 0)
                merged[existing] = update;
            else
                merged[count++] = update;
        }

        if (count == merged.Length)
            return merged;

        var trimmed = new BindingResourceDesc[count];
        Array.Copy(merged, trimmed, count);
        return trimmed;
    }

    public static int FindResourceIndex(ReadOnlySpan<BindingResourceDesc> resources, uint binding, uint arrayElement)
    {
        for (int index = 0; index < resources.Length; index++)
        {
            var resource = resources[index];
            if (resource.Binding == binding && resource.ArrayElement == arrayElement)
                return index;
        }

        return -1;
    }

    public static int FindResourceIndex(ReadOnlySpan<BindingResourceDesc> resources, uint binding, uint arrayElement, BindingType resourceType)
    {
        var registerClass = RegisterClass(resourceType);
        for (int index = 0; index < resources.Length; index++)
        {
            var resource = resources[index];
            if (resource.Binding == binding
                && resource.ArrayElement == arrayElement
                && RegisterClass(resource.ResourceType) == registerClass)
            {
                return index;
            }
        }

        return -1;
    }

    public static BindingSlotDesc? FindSlot(IReadOnlyList<BindingSlotDesc> slots, uint binding)
    {
        int index = FindSlotIndex(slots, binding);
        if (index >= 0)
            return slots[index];

        return null;
    }

    public static BindingSlotDesc? FindSlot(IReadOnlyList<BindingSlotDesc> slots, uint binding, BindingType resourceType)
    {
        int index = FindSlotIndex(slots, binding, resourceType);
        if (index >= 0)
            return slots[index];

        return null;
    }

    public static int FindSlotIndex(IReadOnlyList<BindingSlotDesc> slots, uint binding)
    {
        for (int slotIndex = 0; slotIndex < slots.Count; slotIndex++)
        {
            if (slots[slotIndex].Binding == binding)
                return slotIndex;
        }

        return -1;
    }

    public static int FindSlotIndex(IReadOnlyList<BindingSlotDesc> slots, uint binding, BindingType resourceType)
    {
        if (resourceType == BindingType.None)
            return FindSlotIndex(slots, binding);

        var registerClass = RegisterClass(resourceType);
        for (int slotIndex = 0; slotIndex < slots.Count; slotIndex++)
        {
            var slot = slots[slotIndex];
            if (slot.Binding == binding && RegisterClass(slot.Type) == registerClass)
                return slotIndex;
        }

        return -1;
    }

    public static BindingSlotDesc? FindSingleSlot(IReadOnlyList<BindingSlotDesc> slots, uint binding)
    {
        BindingSlotDesc? match = null;
        for (int slotIndex = 0; slotIndex < slots.Count; slotIndex++)
        {
            var slot = slots[slotIndex];
            if (slot.Binding != binding)
                continue;
            if (match != null)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {binding} is ambiguous across descriptor register classes.");
            match = slot;
        }

        return match;
    }

    public static bool ShareRegisterClass(BindingType left, BindingType right)
        => RegisterClass(left) == RegisterClass(right);

    private static bool ShareKey(BindingResourceDesc left, BindingResourceDesc right)
        => left.ResourceType == BindingType.None
            || right.ResourceType == BindingType.None
            || ShareRegisterClass(left.ResourceType, right.ResourceType);

    public static bool SupportsDynamicOffset(BindingType type)
        => type is BindingType.ConstantBuffer
            or BindingType.StorageBufferRead
            or BindingType.StorageBufferReadWrite
            or BindingType.RawBufferRead
            or BindingType.RawBufferReadWrite;

    public static bool HasDynOffsets(BindingLayoutDesc layout)
    {
        for (int slotIndex = 0; slotIndex < layout.Slots.Count; slotIndex++)
        {
            var slot = layout.Slots[slotIndex];
            if ((slot.Flags & BindingFlags.DynamicOffset) != 0)
                return true;
        }

        return false;
    }

    public static void ValidateOffsets(
        BindingLayoutDesc layout,
        ReadOnlySpan<BindingResourceDesc> resources,
        ReadOnlySpan<DynamicOffset> dynamicOffsets)
    {
        BindingElementKey[]? rentedKeys = null;
        Span<BindingElementKey> offsetKeys = dynamicOffsets.Length <= StackKeyLimit
            ? stackalloc BindingElementKey[dynamicOffsets.Length]
            : (rentedKeys = ArrayPool<BindingElementKey>.Shared.Rent(dynamicOffsets.Length)).AsSpan(0, dynamicOffsets.Length);

        try
        {
            for (int offsetIndex = 0; offsetIndex < dynamicOffsets.Length; offsetIndex++)
            {
                var offset = dynamicOffsets[offsetIndex];
                var slot = FindSingleSlot(layout.Slots, offset.Binding)
                    ?? throw new RhiException(ErrorCode.InvalidDescriptor, $"Dynamic offset targets undeclared binding {offset.Binding}.");
                if ((slot.Flags & BindingFlags.DynamicOffset) == 0)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {offset.Binding} does not declare DynamicOffset.");
                if (!SupportsDynamicOffset(slot.Type))
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {offset.Binding} type {slot.Type} does not support DynamicOffset.");
                if (offset.ArrayElement >= slot.Count)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Dynamic offset binding {offset.Binding} array element {offset.ArrayElement} exceeds descriptor count {slot.Count}.");
                if (FindResourceIndex(resources, offset.Binding, offset.ArrayElement) < 0)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Dynamic offset binding {offset.Binding} array element {offset.ArrayElement} has no bound resource.");
                offsetKeys[offsetIndex] = new BindingElementKey(offset.Binding, offset.ArrayElement, RegisterClass(slot.Type));
            }

            CheckDupes(offsetKeys, "Dynamic offset binding");

            for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
            {
                var resource = resources[resourceIndex];
                var slot = FindSlot(layout.Slots, resource.Binding, resource.ResourceType);
                if (slot == null || (slot.Flags & BindingFlags.DynamicOffset) == 0)
                    continue;
                if (!SupportsDynamicOffset(slot.Type))
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {slot.Binding} type {slot.Type} does not support DynamicOffset.");
                if (!ContainsSortedKey(offsetKeys, new BindingElementKey(resource.Binding, resource.ArrayElement, RegisterClass(resource.ResourceType))))
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {slot.Binding} array element {resource.ArrayElement} requires a dynamic offset.");
            }
        }
        finally
        {
            if (rentedKeys != null)
                ArrayPool<BindingElementKey>.Shared.Return(rentedKeys);
        }
    }

    public static int FindOffset(ReadOnlySpan<DynamicOffset> dynamicOffsets, uint binding, uint arrayElement)
    {
        for (int index = 0; index < dynamicOffsets.Length; index++)
        {
            var offset = dynamicOffsets[index];
            if (offset.Binding == binding && offset.ArrayElement == arrayElement)
                return index;
        }

        return -1;
    }

    public static void ValidateBufferShape(BindingSlotDesc slot, BufferViewDesc view, BufferDesc? buffer)
        => ValidateBufferShape(slot.Binding, slot.Shape, view, buffer);

    public static void ValidateBufferShape(uint binding, BindShapeDesc shape, BufferViewDesc view, BufferDesc? buffer)
    {
        if (shape.Format != Format.Unknown)
        {
            if (view.Format != shape.Format)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {binding} expects buffer view format {shape.Format}, got {view.Format}.");
            return;
        }

        if (shape.StrideInBytes == 0)
            return;

        uint stride = view.StrideInBytes != 0
            ? view.StrideInBytes
            : buffer?.StrideInBytes ?? throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {binding} structured buffer shape requires a backing buffer stride.");
        if (stride != shape.StrideInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {binding} expects structured buffer stride {shape.StrideInBytes}, got {stride}.");
    }

    public static void ValidateTextureShape(BindingSlotDesc slot, TextureViewDesc view)
        => ValidateTextureShape(slot.Binding, slot.Shape, view);

    public static void ValidateTextureShape(uint binding, BindShapeDesc shape, TextureViewDesc view)
    {
        if (view.Dimension != shape.TextureDimension)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {binding} expects texture view dimension {shape.TextureDimension}, got {view.Dimension}.");
        if (shape.Format != Format.Unknown && view.Format != shape.Format)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {binding} expects texture view format {shape.Format}, got {view.Format}.");
    }

    public static ulong LayoutHash(BindingLayoutDesc desc)
    {
        ulong hash = FnvOffsetBasis;
        foreach (var next in desc.Slots
            .OrderBy(static slot => RegisterClass(slot.Type))
            .ThenBy(static slot => slot.Binding)
            .ThenBy(static slot => slot.Type))
        {
            hash = Hash(hash, next.Binding);
            hash = Hash(hash, (uint)next.Type);
            hash = Hash(hash, (uint)next.Stages);
            hash = Hash(hash, next.Count);
            hash = Hash(hash, (uint)next.Flags);
            hash = Hash(hash, (uint)next.Shape.TextureDimension);
            hash = Hash(hash, (uint)next.Shape.Format);
            hash = Hash(hash, next.Shape.StrideInBytes);
            if (next.Type == BindingType.Sampler)
                hash = HashSamplerShape(hash, next.Shape.Sampler);
        }

        return hash;
    }

    public static ulong HashSamplerShape(ulong hash, SamplerDesc sampler)
    {
        hash = Hash(hash, (uint)sampler.MinFilter);
        hash = Hash(hash, (uint)sampler.MagFilter);
        hash = Hash(hash, (uint)sampler.MipmapMode);
        hash = Hash(hash, (uint)sampler.AddressU);
        hash = Hash(hash, (uint)sampler.AddressV);
        hash = Hash(hash, (uint)sampler.AddressW);
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(sampler.MipLodBias));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(sampler.MinLod));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(sampler.MaxLod));
        hash = Hash(hash, sampler.MaxAnisotropy);
        hash = Hash(hash, sampler.Compare.HasValue ? 1u : 0u);
        hash = Hash(hash, sampler.Compare.HasValue ? (uint)sampler.Compare.Value : 0u);
        hash = Hash(hash, (uint)sampler.BorderColor);
        return hash;
    }

    private static ulong Hash(ulong hash, uint value)
    {
        hash ^= value;
        hash *= FnvPrime;
        return hash;
    }

    private static void CheckDupes(Span<BindingElementKey> keys, string label)
    {
        if (keys.Length <= 1)
            return;

        keys.Sort();
        for (int index = 1; index < keys.Length; index++)
        {
            if (keys[index - 1].CompareTo(keys[index]) == 0)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} {keys[index].Binding} array index {keys[index].ArrayElement} is duplicated.");
        }
    }

    private static bool ContainsSortedKey(ReadOnlySpan<BindingElementKey> keys, BindingElementKey key)
    {
        int low = 0;
        int high = keys.Length - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            int compare = keys[mid].CompareTo(key);
            if (compare == 0)
                return true;
            if (compare < 0)
                low = mid + 1;
            else
                high = mid - 1;
        }

        return false;
    }

    private static BindingRegisterClass RegisterClass(BindingType type)
        => type switch
        {
            BindingType.ConstantBuffer => BindingRegisterClass.ConstantBuffer,
            BindingType.StorageBufferRead
                or BindingType.RawBufferRead
                or BindingType.TextureRead
                or BindingType.AccelerationStructure => BindingRegisterClass.ShaderResource,
            BindingType.StorageBufferReadWrite
                or BindingType.RawBufferReadWrite
                or BindingType.TextureReadWrite => BindingRegisterClass.UnorderedAccess,
            BindingType.Sampler => BindingRegisterClass.Sampler,
            BindingType.None => BindingRegisterClass.None,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding type {type} is not defined."),
        };

    private enum BindingRegisterClass
    {
        None,
        ConstantBuffer,
        ShaderResource,
        UnorderedAccess,
        Sampler,
    }

    private readonly record struct BindingElementKey(uint Binding, uint ArrayElement, BindingRegisterClass RegisterClass) : IComparable<BindingElementKey>
    {
        public int CompareTo(BindingElementKey other)
        {
            int binding = Binding.CompareTo(other.Binding);
            if (binding != 0)
                return binding;
            int arrayElement = ArrayElement.CompareTo(other.ArrayElement);
            return arrayElement != 0 ? arrayElement : RegisterClass.CompareTo(other.RegisterClass);
        }
    }
}
