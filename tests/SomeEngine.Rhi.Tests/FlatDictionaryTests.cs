using SomeEngine.Core.Collections;

namespace SomeEngine.Rhi.Tests;

public sealed class FlatDictionaryTests
{
    [Fact]
    public void PublicDictionariesAreReferenceTypes()
    {
        Assert.True(typeof(FlatDictionary<int, int>).IsClass);
        Assert.True(typeof(InlineFlatDictionary<int, int>).IsClass);
    }

    [Fact]
    public void SupportsOpenAddressedStorageAndDictionaryInterfaces()
    {
        var map = new FlatDictionary<int, string>(1);
        try
        {
            for (int index = 0; index < 64; index++)
                map.Add(index, index.ToString());

            Assert.Equal(64, map.Count);
            Assert.True(map.TryGetValue(42, out string? value));
            Assert.Equal("42", value);
            Assert.False(map.TryAdd(42, "duplicate"));

            map.Set(42, "updated");
            Assert.Equal("updated", map[42]);
            Assert.True(map.Remove(42));
            Assert.False(map.ContainsKey(42));
            Assert.True(map.TryAdd(42, "reused"));
            Assert.Equal("reused", map[42]);

            IDictionary<int, string> dictionary = map;
            Assert.Equal("reused", dictionary[42]);
            dictionary[42] = "interface-updated";
            Assert.Equal("interface-updated", map[42]);
            Assert.Contains(7, dictionary.Keys);
            Assert.Contains("9", dictionary.Values);
            Assert.Throws<ArgumentException>(() => dictionary.Add(42, "duplicate"));
        }
        finally
        {
            map.Dispose();
        }
    }

    [Fact]
    public void ReusesDeletedSlotsWithCollidingKeys()
    {
        var map = new FlatDictionary<int, int>(0, new ConstantHashComparer());
        try
        {
            for (int index = 0; index < 40; index++)
                map.Add(index, index);

            for (int index = 0; index < 40; index += 2)
                Assert.True(map.Remove(index));

            for (int index = 100; index < 120; index++)
                Assert.True(map.TryAdd(index, index));

            Assert.Equal(40, map.Count);
            for (int index = 1; index < 40; index += 2)
                Assert.Equal(index, map[index]);
            for (int index = 100; index < 120; index++)
                Assert.Equal(index, map[index]);
        }
        finally
        {
            map.Dispose();
        }
    }

    [Fact]
    public void EnumeratorThrowsAfterMutation()
    {
        var map = new FlatDictionary<int, int>();
        try
        {
            map.Add(1, 10);
            var enumerator = map.GetEnumerator();
            map.Add(2, 20);

            Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
        }
        finally
        {
            map.Dispose();
        }
    }

    [Fact]
    public void ClearNoResizeKeepsStorageReusable()
    {
        var map = new FlatDictionary<int, string>(1);
        try
        {
            for (int index = 0; index < 32; index++)
                map.Add(index, index.ToString());

            int slotsBefore = SlotCount(map);
            map.ClearNoResize();

            Assert.Empty(map);
            Assert.Equal(slotsBefore, SlotCount(map));
            Assert.False(map.TryGetValue(7, out _));

            for (int index = 0; index < 32; index++)
                map.Add(index, $"next-{index}");

            Assert.Equal(32, map.Count);
            Assert.Equal("next-7", map[7]);
            Assert.Equal(slotsBefore, SlotCount(map));
        }
        finally
        {
            map.Dispose();
        }
    }

    [Fact]
    public void ActiveHandlesCompactsDeletedSlotsWhenEmpty()
    {
        var active = new FlatDictionary<BufferHandle, int>(1);
        try
        {
            BufferHandle[] handles = Enumerable.Range(1, 12)
                .Select(id => new BufferHandle((uint)id, 1))
                .ToArray();

            foreach (var handle in handles)
                ActiveHandles.Retain(active, handle);

            int slotsBefore = SlotCount(active);
            Assert.True(UsedSlotCount(active) > 0);

            ActiveHandles.Release(active, handles, "Test", "buffer");

            Assert.Empty(active);
            Assert.Equal(slotsBefore, SlotCount(active));
            Assert.Equal(0, UsedSlotCount(active));
        }
        finally
        {
            active.Dispose();
        }
    }

    private static int SlotCount<TKey, TValue>(FlatDictionary<TKey, TValue> map)
        where TKey : notnull
    {
        var core = typeof(FlatDictionary<TKey, TValue>)
            .GetField("_core", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(map)!;
        return (int)core.GetType()
            .GetProperty("SlotCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(core)!;
    }

    private static int UsedSlotCount<TKey, TValue>(FlatDictionary<TKey, TValue> map)
        where TKey : notnull
    {
        var core = typeof(FlatDictionary<TKey, TValue>)
            .GetField("_core", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(map)!;
        return (int)core.GetType()
            .GetField("_usedSlots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(core)!;
    }

    private sealed class ConstantHashComparer : IEqualityComparer<int>
    {
        public bool Equals(int x, int y)
            => x == y;

        public int GetHashCode(int obj)
            => 1;
    }
}
