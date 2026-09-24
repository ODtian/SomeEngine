using SomeEngine.Render.RHI;

namespace SomeEngine.Tests.Systems;

public sealed class UploadPackTests
{
    [Fact]
    public void AddUsesMemory()
    {
        var pack = new UploadPack();
        byte[] data = [1, 2, 3];

        pack.Add(16, data);
        data[0] = 9;
        UploadItem item = Assert.Single(pack.Take());

        Assert.Equal(16ul, item.Offset);
        Assert.Equal(9, item.Data.Span[0]);
        Assert.Equal(0, pack.Count);
        Assert.Equal(0, pack.ByteCount);
        Assert.Equal(0, pack.CopyBytes);
    }

    [Fact]
    public void CopyOwnsData()
    {
        var pack = new UploadPack();
        byte[] data = [1, 2, 3];

        pack.Copy(4, data);
        data[0] = 9;
        UploadItem item = Assert.Single(pack.Take());

        Assert.Equal(4ul, item.Offset);
        Assert.Equal(1, item.Data.Span[0]);
        Assert.Equal(3, item.Data.Length);
    }

    [Fact]
    public void TracksBytes()
    {
        var pack = new UploadPack();

        pack.Add(0, new byte[] { 1, 2 });
        pack.Copy(8, new byte[] { 3, 4, 5 });

        Assert.Equal(2, pack.Count);
        Assert.Equal(5, pack.ByteCount);
        Assert.Equal(3, pack.CopyBytes);
    }

    [Fact]
    public void TakesPacked()
    {
        var pack = new UploadPack();
        byte[] data = [1, 2, 3];

        pack.Copy(0, data.AsSpan(0, 2));
        pack.Copy(2, data.AsSpan(2, 1));
        data[0] = 9;

        Assert.True(pack.TryPacked(out ReadOnlyMemory<byte> packed));
        Assert.Equal([1, 2, 3], packed.ToArray());
        Assert.Equal(0, pack.Count);
        Assert.Equal(0, pack.ByteCount);
        Assert.Equal(0, pack.CopyBytes);
    }

    [Fact]
    public void MixedNeedsTake()
    {
        var pack = new UploadPack();

        pack.Copy(0, new byte[] { 1 });
        pack.Add(4, new byte[] { 2 });

        Assert.False(pack.TryPacked(out _));
        UploadItem[] items = pack.Take();

        Assert.Equal(2, items.Length);
        Assert.Equal(0ul, items[0].Offset);
        Assert.Equal(1, items[0].Data.Span[0]);
        Assert.Equal(4ul, items[1].Offset);
        Assert.Equal(2, items[1].Data.Span[0]);
    }

    [Fact]
    public void KeepsLargeOffset()
    {
        var pack = new UploadPack();

        pack.Add(5_000_000_000ul, new byte[] { 1 });
        UploadItem item = Assert.Single(pack.Take());

        Assert.Equal(5_000_000_000ul, item.Offset);
    }
}
