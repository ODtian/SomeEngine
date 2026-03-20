using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;

namespace SomeEngine.Tests;

[TestFixture]
public class ClusterLodLevelTests
{
    [Test]
    public void VerifyClusterLodLevelSerialization()
    {
        // 1. Create a dummy high-res mesh to force LOD generation
        // Using IcoSphere(5) which has 20480 triangles, enough for multiple clusters and levels
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);

        // 2. Process
        var meshAsset = ClusterBuilder.ProcessRaw(vertices, attributes, indices, new System.Collections.Generic.List<string>(), "TestLOD");

        // 3. Inspect Payload manually
        var payload = meshAsset.Payload!.Value;

        // 4. Verify Levels
        int maxLevel = 0;
        int[] levelCounts = new int[16];
        int offset = 0;
        int payloadLength = payload.Length;
        int totalClusters = 0;

        while (offset < payloadLength)
        {
            var pageSpan = payload.Span.Slice(offset);
            if (pageSpan.Length < MeshPageHeader.Size) break;

            uint pClusterCount = MemoryMarshal.Read<uint>(pageSpan.Slice(0, 4));
            uint pTotalTriCount = MemoryMarshal.Read<uint>(pageSpan.Slice(8, 4));
            uint pClustersOffset = MemoryMarshal.Read<uint>(pageSpan.Slice(16, 4));
            uint pIndicesOffset = MemoryMarshal.Read<uint>(pageSpan.Slice(28, 4));
            uint pPageSize = pIndicesOffset + pTotalTriCount * 3;

            int clusterByteSize = Marshal.SizeOf<GPUCluster>();
            if (pClustersOffset + pClusterCount * clusterByteSize > pageSpan.Length) break;
            var clustersSpan = pageSpan.Slice((int)pClustersOffset, (int)pClusterCount * clusterByteSize);
            var clusters = MemoryMarshal.Cast<byte, GPUCluster>(clustersSpan);

            for (int i = 0; i < clusters.Length; i++)
            {
                var c = clusters[i];
                int level = (int)((c.PackedCounts >> 16) & 0xFF);
                if (level < 16) levelCounts[level]++;
                if (level > maxLevel) maxLevel = level;
            }

            totalClusters += (int)pClusterCount;
            offset += (int)pPageSize;
        }

        Console.WriteLine($"Total Clusters: {totalClusters}");

        Console.WriteLine($"Max Level: {maxLevel}");
        for (int i = 0; i <= maxLevel; i++)
        {
            Console.WriteLine($"Level {i}: {levelCounts[i]} clusters");
        }

        // Assert that we have at least level 0 and level 1
        Assert.That(levelCounts[0], Is.GreaterThan(0), "Should have Level 0 clusters");
        // With 20k tris, we should definitely have LODs
        Assert.That(maxLevel, Is.GreaterThan(0), "Should have generated LODs");
        Assert.That(levelCounts[1], Is.GreaterThan(0), "Should have Level 1 clusters");
    }
}
