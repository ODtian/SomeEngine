using System;
using System.Collections.Generic;
using Diligent;

namespace SomeEngine.Render.Graph;

internal class RGMemoryHeap : IDisposable
{
    public IDeviceMemory Memory { get; private set; } = null!;
    public ulong Capacity { get; private set; }
    public uint MemoryTypeBits { get; private set; }

    private readonly IRenderDevice _device;

    public struct AllocationEntry
    {
        public ulong Offset;
        public ulong Size;
        public int ResourceId; // The ID of the RGResource
        public int FirstPassIndex; // Start of lifetime
        public int LastPassIndex; // End of lifetime
    }

    private readonly List<AllocationEntry> _allocations = [];

    // Only for unit tests
    internal RGMemoryHeap(ulong capacity)
    {
        _device = null!;
        Capacity = capacity;
        MemoryTypeBits = uint.MaxValue;
    }

    public RGMemoryHeap(
        IRenderDevice device,
        ulong initialCapacity,
        uint memoryTypeBits = uint.MaxValue
    )
    {
        _device = device;
        MemoryTypeBits = memoryTypeBits;

        var createInfo = new DeviceMemoryCreateInfo
        {
            Desc = new DeviceMemoryDesc
            {
                Type = DeviceMemoryType.Placed,
                PageSize = initialCapacity,
            },
            InitialSize = initialCapacity,
        };

        Memory = device.CreateDeviceMemory(createInfo);
        Capacity = Memory.GetCapacity();
    }

    /// <summary>
    /// Attempts to allocate space for a resource taking lifetime aliasing into account.
    /// </summary>
    /// <returns>True if allocation succeeded, false if not enough contiguous space.</returns>
    public bool TryAllocate(
        ulong size,
        ulong alignment,
        int resourceId,
        int firstPassIndex,
        int lastPassIndex,
        out ulong offset
    )
    {
        offset = 0;

        // Find all existing allocations that overlap in lifetime
        var overlappingAllocs = new List<AllocationEntry>();
        foreach (var alloc in _allocations)
        {
            // Two lifetimes overlap if the start of one is before or at the end of the other,
            // AND the end of one is after or at the start of the other.
            // (Assuming pass indices are inclusive)
            if (firstPassIndex <= alloc.LastPassIndex && lastPassIndex >= alloc.FirstPassIndex)
            {
                overlappingAllocs.Add(alloc);
            }
        }

        // Sort overlapping allocations by offset to find gaps
        overlappingAllocs.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        ulong currentOffset = 0;

        foreach (var alloc in overlappingAllocs)
        {
            // Align current offset
            ulong alignedOffset = AlignUp(currentOffset, alignment);

            // Is there enough space before this allocation?
            if (alignedOffset + size <= alloc.Offset)
            {
                // Found a gap!
                offset = alignedOffset;
                _allocations.Add(
                    new AllocationEntry
                    {
                        Offset = offset,
                        Size = size,
                        ResourceId = resourceId,
                        FirstPassIndex = firstPassIndex,
                        LastPassIndex = lastPassIndex,
                    }
                );
                return true;
            }

            // Move current offset past this allocation
            ulong nextOffset = alloc.Offset + alloc.Size;
            if (nextOffset > currentOffset)
            {
                currentOffset = nextOffset;
            }
        }

        // Check space after the last overlapping allocation
        ulong finalAlignedOffset = AlignUp(currentOffset, alignment);
        if (finalAlignedOffset + size <= Capacity)
        {
            offset = finalAlignedOffset;
            _allocations.Add(
                new AllocationEntry
                {
                    Offset = offset,
                    Size = size,
                    ResourceId = resourceId,
                    FirstPassIndex = firstPassIndex,
                    LastPassIndex = lastPassIndex,
                }
            );
            return true;
        }

        // Not enough space
        return false;
    }

    public void Reset()
    {
        _allocations.Clear();
    }

    public void Dispose()
    {
        Memory?.Dispose();
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        if (alignment == 0)
            return value;
        return (value + alignment - 1) & ~(alignment - 1);
    }
}
