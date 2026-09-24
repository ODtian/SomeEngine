using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;

namespace SomeEngine.Tests;

public class ClusterLodLevelTests
{
    [Fact]
    public void VerifyClusterLodLevelSerialization()
    {
        // 1. Create a dummy high-res mesh to force LOD generation
        // Using IcoSphere(5) which has 20480 triangles, enough for multiple clusters and levels
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);

        // 2. Process
        var meshAsset = ClusterBuilder.ProcessRaw(vertices, attributes, indices, new System.Collections.Generic.List<string>(), "TestLOD");

        (int[] levelCounts, int maxLevel, int totalClusters) = ReadLevelCounts(meshAsset.Payload!.Value);

        Console.WriteLine($"Total Clusters: {totalClusters}");
        Console.WriteLine($"Max Level: {maxLevel}");
        for (int i = 0; i <= maxLevel; i++)
        {
            Console.WriteLine($"Level {i}: {levelCounts[i]} clusters");
        }

        // Assert that we have at least level 0 and level 1
        Assert.True(levelCounts[0] > 0, "Should have Level 0 clusters");
        // With 20k tris, we should definitely have LODs
        Assert.True(maxLevel > 0, "Should have generated LODs");
        Assert.True(levelCounts[1] > 0, "Should have Level 1 clusters");
    }

    [Fact]
    public void SampleIcoSphereAsset_ContainsLowerLodLevels()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "samples",
            "IcoSphere.mesh.0.IcoSphere.mesh.asset");
        MeshAsset asset = MeshAssetCodec.Load(path);
        Assert.True(asset.Payload.HasValue);

        (int[] levelCounts, int maxLevel, int totalClusters) = ReadLevelCounts(asset.Payload!.Value);

        Console.WriteLine($"Sample Total Clusters: {totalClusters}");
        Console.WriteLine($"Sample Max Level: {maxLevel}");
        for (int i = 0; i <= maxLevel; i++)
            Console.WriteLine($"Sample Level {i}: {levelCounts[i]} clusters");

        Assert.True(levelCounts[0] > 0, "Sample asset should contain Level 0 clusters.");
        Assert.True(maxLevel > 0, "Sample asset should contain coarse LOD clusters.");
    }

    private static (int[] Counts, int MaxLevel, int TotalClusters) ReadLevelCounts(ReadOnlyMemory<byte> payload)
    {
        int maxLevel = 0;
        int[] levelCounts = new int[16];
        int offset = 0;
        int payloadLength = payload.Length;
        int totalClusters = 0;

        while (offset < payloadLength)
        {
            ReadOnlySpan<byte> pageSpan = payload.Span[offset..];
            if (pageSpan.Length < MeshPageHeader.Size)
                break;

            uint clusterCount = MemoryMarshal.Read<uint>(pageSpan.Slice(0, 4));
            uint totalTriCount = MemoryMarshal.Read<uint>(pageSpan.Slice(8, 4));
            uint clustersOffset = MemoryMarshal.Read<uint>(pageSpan.Slice(16, 4));
            uint indicesOffset = MemoryMarshal.Read<uint>(pageSpan.Slice(28, 4));
            uint pageSize = indicesOffset + totalTriCount * 3;

            int clusterByteSize = Marshal.SizeOf<GPUCluster>();
            if (clustersOffset + clusterCount * clusterByteSize > pageSpan.Length)
                break;
            ReadOnlySpan<byte> clustersSpan = pageSpan.Slice((int)clustersOffset, (int)clusterCount * clusterByteSize);
            ReadOnlySpan<GPUCluster> clusters = MemoryMarshal.Cast<byte, GPUCluster>(clustersSpan);

            for (int i = 0; i < clusters.Length; i++)
            {
                int level = (int)((clusters[i].PackedCounts >> 16) & 0xFF);
                if ((uint)level < (uint)levelCounts.Length)
                    levelCounts[level]++;
                if (level > maxLevel)
                    maxLevel = level;
            }

            totalClusters += (int)clusterCount;
            offset += (int)pageSize;
        }

        return (levelCounts, maxLevel, totalClusters);
    }
}
