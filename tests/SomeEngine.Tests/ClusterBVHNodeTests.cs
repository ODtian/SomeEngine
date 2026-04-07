using SomeEngine.Assets.Data;

namespace SomeEngine.Tests;

public class ClusterBVHNodeTests
{
    [Fact]
    public void TestLeafDataEncodingAndDecoding()
    {
        var node = new ClusterBVHNode();

        // Arrange properties
        uint expectedClusterStart = 0xFFF; // 12 bits max is 4095
        uint expectedClusterCount = 0x8FFFF; // up to 20 bits

        // Act
        node.SetLeafData(expectedClusterStart, expectedClusterCount);
        node.GetLeafData(out uint actualStart, out uint actualCount);

        // Assert
        Assert.Equal(expectedClusterStart, actualStart);
        Assert.Equal(expectedClusterCount, actualCount);
    }

    [Fact]
    public void TestLeafDataBitBoundaries()
    {
        var node = new ClusterBVHNode();

        // Act: max values
        uint expectedClusterStart = 4095; // 2^12 - 1
        uint expectedClusterCount = 1048575; // 2^20 - 1

        node.SetLeafData(expectedClusterStart, expectedClusterCount);
        node.GetLeafData(out uint actualStart, out uint actualCount);

        // Assert
        Assert.Equal(expectedClusterStart, actualStart);
        Assert.Equal(expectedClusterCount, actualCount);
    }
}
