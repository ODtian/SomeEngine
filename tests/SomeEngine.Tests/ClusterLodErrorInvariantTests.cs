using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

[TestFixture]
public class ClusterLodErrorInvariantTests
{
    [Test]
    public void ParentError_ShouldBeGreaterOrEqual_ThanClusterError_ForNonRoot()
    {
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);
        var meshAsset = ClusterBuilder.ProcessRaw(vertices, attributes, indices, "ErrorInvariant");

        var payload = meshAsset.Payload!.Value;

        int offset = 0;
        int payloadLength = payload.Length;

        int checkedCount = 0;
        int violationCount = 0;

        while (offset < payloadLength)
        {
            var pageSpan = payload.Span.Slice(offset);
            if (pageSpan.Length < 32) break;

            uint clusterCount = MemoryMarshal.Read<uint>(pageSpan.Slice(0, 4));
            uint pageSize = MemoryMarshal.Read<uint>(pageSpan.Slice(12, 4));
            uint clustersOffset = MemoryMarshal.Read<uint>(pageSpan.Slice(16, 4));

            int clusterByteSize = Marshal.SizeOf<GPUCluster>();
            var clustersSpan = pageSpan.Slice((int)clustersOffset, (int)clusterCount * clusterByteSize);
            var clusters = MemoryMarshal.Cast<byte, GPUCluster>(clustersSpan);

            for (int i = 0; i < clusters.Length; i++)
            {
                ref readonly var c = ref clusters[i];
                bool isRoot = c.ParentLODError >= float.MaxValue * 0.5f;
                if (isRoot) continue;

                checkedCount++;
                if (c.ParentLODError + 1e-8f < c.LODError)
                    violationCount++;
            }

            offset += (int)pageSize;
        }

        Assert.That(checkedCount, Is.GreaterThan(0));
        Assert.That(violationCount, Is.EqualTo(0), "Found clusters where ParentLODError < LODError; this breaks cut monotonicity and causes overlap.");
    }

    [Test]
    public void ParentError_ShouldMapToExistingCoarserSelfError()
    {
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);
        var meshAsset = ClusterBuilder.ProcessRaw(vertices, attributes, indices, "ParentErrorMap");

        var payload = meshAsset.Payload!.Value;
        var allClusters = new List<GPUCluster>();

        int offset = 0;
        while (offset < payload.Length)
        {
            var pageSpan = payload.Span.Slice(offset);
            if (pageSpan.Length < 32) break;

            uint clusterCount = MemoryMarshal.Read<uint>(pageSpan.Slice(0, 4));
            uint pageSize = MemoryMarshal.Read<uint>(pageSpan.Slice(12, 4));
            uint clustersOffset = MemoryMarshal.Read<uint>(pageSpan.Slice(16, 4));

            int clusterByteSize = Marshal.SizeOf<GPUCluster>();
            var clustersSpan = pageSpan.Slice((int)clustersOffset, (int)clusterCount * clusterByteSize);
            var clusters = MemoryMarshal.Cast<byte, GPUCluster>(clustersSpan);

            for (int i = 0; i < clusters.Length; i++)
                allClusters.Add(clusters[i]);

            offset += (int)pageSize;
        }

        int checkedCount = 0;
        int missingMappedCount = 0;

        for (int i = 0; i < allClusters.Count; i++)
        {
            var child = allClusters[i];
            if (child.ParentLODError >= float.MaxValue * 0.5f)
                continue;

            checkedCount++;

            bool mapped = false;
            for (int j = 0; j < allClusters.Count; j++)
            {
                var parentCandidate = allClusters[j];
                if (parentCandidate.LODLevel <= child.LODLevel)
                    continue;

                if (Math.Abs(parentCandidate.LODError - child.ParentLODError) <= 1e-5f)
                {
                    mapped = true;
                    break;
                }
            }

            if (!mapped)
                missingMappedCount++;
        }

        Assert.That(checkedCount, Is.GreaterThan(0));
        Assert.That(missingMappedCount, Is.EqualTo(0), "Found child clusters whose ParentLODError does not map to any coarser cluster LODError.");
    }
}
