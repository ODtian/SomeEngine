using System.Runtime.CompilerServices;
using SomeEngine.Core.Collections;

namespace SomeEngine.Core.Tests.Collections;

public class SmallListTests
{
    [Fact]
    public void SmallList_DefaultValue_UsesInlinePathAndEnumeratesInOrder()
    {
        SmallList<string> list = default;

        list.Add("a");
        list.Add("b");
        list.Add("c");

        Assert.Equal(3, list.Count);
        Assert.Equal(new[] { "a", "b", "c" }, list.AsSpan().ToArray());

        var enumerated = new List<string>();
        foreach (var item in list)
            enumerated.Add(item);

        Assert.Equal(new[] { "a", "b", "c" }, enumerated);
    }

    [Fact]
    public void SmallList_FourthItem_UsesOverflowPathButPreservesOrder()
    {
        SmallList<int> list = default;

        list.Add(10);
        list.Add(20);
        list.Add(30);
        list.Add(40);

        Assert.Equal(4, list.Count);
        Assert.Equal(new[] { 10, 20, 30, 40 }, list.AsSpan().ToArray());
        Assert.Equal(30, list[2]);
    }

    [Fact]
    public void SmallList_RemoveAt_PreservesOrder_ForInlineAndOverflow()
    {
        SmallList<int> inline = default;
        inline.Add(1);
        inline.Add(2);
        inline.Add(3);
        inline.RemoveAt(1);

        Assert.Equal(new[] { 1, 3 }, inline.AsSpan().ToArray());

        SmallList<int> overflow = default;
        overflow.Add(1);
        overflow.Add(2);
        overflow.Add(3);
        overflow.Add(4);
        overflow.RemoveAt(1);

        Assert.Equal(new[] { 1, 3, 4 }, overflow.AsSpan().ToArray());
    }

    [Fact]
    public void SmallList_Insert_PreservesOrder_ForInlineAndOverflow()
    {
        SmallList<int> inline = default;
        inline.Add(1);
        inline.Add(3);

        inline.Insert(1, 2);
        Assert.Equal(new[] { 1, 2, 3 }, inline.AsSpan().ToArray());

        SmallList<int> overflow = default;
        overflow.Add(1);
        overflow.Add(2);
        overflow.Add(4);
        overflow.Add(5);

        overflow.Insert(2, 3);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, overflow.AsSpan().ToArray());
    }

    [Fact]
    public void SmallList_EnsureCapacity_PreservesItemsWhenMovingToOverflow()
    {
        SmallList<int> list = default;
        list.Add(1);
        list.Add(2);

        list.EnsureCapacity(8);
        list.Add(3);
        list.Add(4);
        list[1] = 20;

        Assert.Equal(4, list.Count);
        Assert.Equal(new[] { 1, 20, 3, 4 }, list.AsSpan().ToArray());
    }

    [Fact]
    public void SmallList_EnsureCapacity_BeforeAdd_UsesOverflowPath()
    {
        SmallList<int> list = default;

        list.EnsureCapacity(8);
        list.Add(1);
        list.Add(2);

        Assert.Equal(new[] { 1, 2 }, list.AsSpan().ToArray());
    }

    [Fact]
    public void SmallList_Clear_AllowsReuse()
    {
        SmallList<string> list = default;
        list.Add("before");
        list.Add("clear");

        list.Clear();
        Assert.Equal(0, list.Count);

        list.Add("after");
        Assert.Equal(new[] { "after" }, list.AsSpan().ToArray());
    }

    [Fact]
    public void SmallList_RemoveAt_ClearsRemovedReferences_ForInlineAndOverflow()
    {
        var inline = CreateInlineListAfterRemoveAt();
        var overflow = CreateOverflowListAfterRemoveAt();

        CollectGarbage();

        GC.KeepAlive(inline.List);
        GC.KeepAlive(overflow.List);
        Assert.False(inline.Removed.IsAlive);
        Assert.False(overflow.Removed.IsAlive);
    }

    [Fact]
    public void SmallList_Clear_ClearsReferences_ForInlineAndOverflow()
    {
        var inline = CreateInlineListAfterClear();
        var overflow = CreateOverflowListAfterClear();

        CollectGarbage();

        GC.KeepAlive(inline.List);
        GC.KeepAlive(overflow.List);
        Assert.False(inline.Removed.IsAlive);
        Assert.False(overflow.Removed.IsAlive);
    }

    [Fact]
    public void SmallList_Indexer_AllowsInPlaceMutationForValueTypes()
    {
        SmallList<int> list = default;
        list.Add(1);
        list.Add(2);
        list.Add(3);

        ref var item = ref list[1];
        item = 99;

        Assert.Equal(new[] { 1, 99, 3 }, list.AsSpan().ToArray());
    }

    [Fact]
    public void SmallList_RemoveStable_RemovesByValueAndPreservesOrder()
    {
        SmallList<int> list = default;
        list.Add(1);
        list.Add(2);
        list.Add(3);

        Assert.True(list.RemoveStable(2));
        Assert.False(list.RemoveStable(9));
        Assert.Equal(new[] { 1, 3 }, list.AsSpan().ToArray());
    }

    [Fact]
    public void SmallList_RemoveSwapBack_RemovesByValueWithoutPreservingOrder()
    {
        SmallList<int> list = default;
        list.Add(1);
        list.Add(2);
        list.Add(3);

        Assert.True(list.RemoveSwapBack(1));
        Assert.False(list.RemoveSwapBack(9));
        Assert.Equal(new[] { 3, 2 }, list.AsSpan().ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SmallList<object> List, WeakReference Removed) CreateInlineListAfterRemoveAt()
    {
        object removed = new();
        SmallList<object> list = default;
        list.Add(removed);
        list.RemoveAt(0);
        return (list, new WeakReference(removed));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SmallList<object> List, WeakReference Removed) CreateOverflowListAfterRemoveAt()
    {
        object removed = new();
        SmallList<object> list = default;
        list.Add(removed);
        list.Add(new object());
        list.Add(new object());
        list.Add(new object());
        list.RemoveAt(0);
        return (list, new WeakReference(removed));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SmallList<object> List, WeakReference Removed) CreateInlineListAfterClear()
    {
        object removed = new();
        SmallList<object> list = default;
        list.Add(removed);
        list.Clear();
        return (list, new WeakReference(removed));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SmallList<object> List, WeakReference Removed) CreateOverflowListAfterClear()
    {
        object removed = new();
        SmallList<object> list = default;
        list.Add(removed);
        list.Add(new object());
        list.Add(new object());
        list.Add(new object());
        list.Clear();
        return (list, new WeakReference(removed));
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

