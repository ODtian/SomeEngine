using SomeEngine.Core.Collections;

namespace SomeEngine.Rhi.Tests;

public sealed class InlineFlatDictionaryTests
{
    [Fact]
    public void SupportsInlineStorageAndDictionaryInterfaces()
    {
        var map = new InlineFlatDictionary<int, int>();
        try
        {
            for (int index = 0; index < 8; index++)
                map.Add(index, index * 10);

            Assert.Equal(8, map.Count);
            Assert.True(map.ContainsKey(3));
            Assert.True(map.TryGetValue(7, out int value));
            Assert.Equal(70, value);

            IDictionary<int, int> dictionary = map;
            Assert.Equal(30, dictionary[3]);
            dictionary[3] = 123;
            Assert.Equal(123, map[3]);
            Assert.Contains(4, dictionary.Keys);
            Assert.Contains(50, dictionary.Values);
            Assert.Throws<ArgumentException>(() => dictionary.Add(3, 99));
        }
        finally
        {
            map.Dispose();
        }
    }

    [Fact]
    public void SpillsToFlatDictionaryCoreAndSupportsMutation()
    {
        var map = new InlineFlatDictionary<int, string>();
        try
        {
            for (int index = 0; index < 32; index++)
                map.Add(index, index.ToString());

            map.Set(12, "updated");
            Assert.Equal("updated", map[12]);
            Assert.True(map.Remove(12));
            Assert.False(map.ContainsKey(12));
            Assert.True(map.TryAdd(12, "reused"));
            Assert.Equal("reused", map[12]);

            IReadOnlyDictionary<int, string> readOnly = map;
            Assert.Equal(32, readOnly.Count);
            Assert.Equal(32, readOnly.Keys.Count());
            Assert.Equal(32, readOnly.Values.Count());
        }
        finally
        {
            map.Dispose();
        }
    }

    [Fact]
    public void EnumeratorThrowsAfterSpillMutation()
    {
        var map = new InlineFlatDictionary<int, int>();
        try
        {
            for (int index = 0; index < 12; index++)
                map.Add(index, index);

            var enumerator = map.GetEnumerator();
            map.Remove(3);

            Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
        }
        finally
        {
            map.Dispose();
        }
    }
}
