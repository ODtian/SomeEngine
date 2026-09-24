using SomeEngine.Render.Pipelines;

namespace SomeEngine.Tests.Systems;

public sealed class PageHeapTests
{
    [Fact]
    public void OwnsCapacity()
    {
        var heap = new PageHeap();

        Assert.Equal(64u * 1024u * 1024u, PageHeap.CapacityBytes);
        Assert.Equal(PageHeap.CapacityBytes, heap.Largest());
        Assert.Equal(PageHeap.CapacityBytes, heap.FreeBytes);
        Assert.Equal(0u, heap.UsedBytes);
        Assert.Equal(1, heap.FreeBlockCount);
    }

    [Fact]
    public void AllocAligns()
    {
        var heap = new PageHeap();

        Assert.True(heap.TryAlloc(1, out uint first));
        Assert.True(heap.TryAlloc(1, out uint second));

        Assert.Equal(0u, first);
        Assert.Equal(16u, second);
        Assert.Equal(32u, heap.UsedBytes);
        Assert.Equal(PageHeap.CapacityBytes - 32u, heap.FreeBytes);
    }

    [Fact]
    public void FreeMerges()
    {
        var heap = new PageHeap();

        Assert.True(heap.TryAlloc(16, out uint first));
        Assert.True(heap.TryAlloc(16, out uint second));
        heap.Free(second, 16);
        heap.Free(first, 16);

        Assert.True(heap.TryAlloc(32, out uint merged));
        Assert.Equal(0u, merged);
        Assert.Equal(1, heap.FreeBlockCount);
    }

    [Fact]
    public void TracksLargest()
    {
        var heap = new PageHeap();

        Assert.True(heap.TryAlloc(16, out _));

        Assert.Equal((64u * 1024u * 1024u) - 16u, heap.Largest());
        Assert.Equal((64u * 1024u * 1024u) - 16u, heap.FreeBytes);
        Assert.Equal(16u, heap.UsedBytes);
    }
}
