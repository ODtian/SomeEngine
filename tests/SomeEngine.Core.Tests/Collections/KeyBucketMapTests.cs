using SomeEngine.Core.Collections;

namespace SomeEngine.Core.Tests.Collections;

public class KeyBucketMapTests
{
    [Fact]
    public void KeyBucketMap_AddUnique_RemoveAndClear()
    {
        var buckets = new KeyBucketMap<string, int>();

        Assert.True(buckets.AddUnique("group", 1));
        Assert.False(buckets.AddUnique("group", 1));
        buckets.Add("group", 2);

        Assert.Equal(new[] { 1, 2 }, buckets.Get("group").ToArray());
        Assert.True(buckets.RemoveSwapBack("group", 1));
        Assert.Equal(new[] { 2 }, buckets.Get("group").ToArray());
        Assert.True(buckets.RemoveSwapBack("group", 2));
        Assert.Empty(buckets.Get("group").ToArray());

        buckets.Add("other", 1);
        buckets.Clear();

        Assert.Equal(0, buckets.Count);
        Assert.Empty(buckets.Get("other").ToArray());
    }
}

