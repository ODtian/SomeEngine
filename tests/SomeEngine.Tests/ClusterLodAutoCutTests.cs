using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

[TestFixture]
public class ClusterLodAutoCutTests
{
    [Test]
    public void TestIcoSphereSelectionStability()
    {
        // 1. Generate IcoSphere Clusters
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);
        var meshAsset = ClusterBuilder.ProcessRaw(vertices, attributes, indices, "StabilityTest");
        var payload = meshAsset.Payload!.Value;

        var allClusters = new List<GPUCluster>();
        int offset = 0;
        while (offset < payload.Length)
        {
            var page = payload.Span.Slice(offset);
            uint clusterCount = MemoryMarshal.Read<uint>(page.Slice(0, 4));
            uint pageSize = MemoryMarshal.Read<uint>(page.Slice(12, 4));
            uint clustersOffset = MemoryMarshal.Read<uint>(page.Slice(16, 4));
            int clusterByteSize = Marshal.SizeOf<GPUCluster>();

            var clusters = MemoryMarshal.Cast<byte, GPUCluster>(page.Slice((int)clustersOffset, (int)clusterCount * clusterByteSize));
            foreach (var c in clusters) allClusters.Add(c);
            offset += (int)pageSize;
        }

        // 2. Build DAG for traversal
        // Map GroupId -> List of Cluster indices
        var groupToClusters = allClusters.Select((c, idx) => new { c, idx })
                                         .GroupBy(x => x.c.GroupId)
                                         .ToDictionary(g => g.Key, g => g.Select(x => x.idx).ToList());

        // Map ParentGroupId -> List of child Cluster indices
        var parentGroupToChildren = allClusters.Select((c, idx) => new { c, idx })
                                               .Where(x => x.c.ParentGroupId != -1)
                                               .GroupBy(x => x.c.ParentGroupId)
                                               .ToDictionary(g => g.Key, g => g.Select(x => x.idx).ToList());

        float lodScale = 500.0f;
        float lodThreshold = 1.0f;

        // 3. Simulate camera distances from close to far
        for (float camDist = 5.0f; camDist < 500.0f; camDist *= 1.5f)
        {
            var selectedIndices = new HashSet<int>();
            
            for (int i = 0; i < allClusters.Count; i++)
            {
                var c = allClusters[i];
                float dist = Math.Max(Vector3.Distance(c.Center, new Vector3(0, 0, -camDist)) - c.Radius, 0.001f);
                float e = (c.LODError * lodScale) / dist;
                float pe = c.ParentLODError >= 1e30f ? float.PositiveInfinity : (c.ParentLODError * lodScale) / dist;

                if (e <= lodThreshold && pe > lodThreshold)
                {
                    selectedIndices.Add(i);
                }
            }

            // 4. Check DAG Cut validity:
            var leafIndices = allClusters.Select((c, idx) => new { c, idx })
                                         .Where(x => x.c.LODLevel == 0)
                                         .Select(x => x.idx)
                                         .ToList();

            // For a valid cut:
            // - No selected cluster can be an ancestor of another selected cluster (Overlap)
            // - Every path from a leaf to a root must contain exactly one selected cluster (Hole/Overlap)

            // 1. For every selected cluster, none of its ancestors can be selected.
            foreach (int selIdx in selectedIndices)
            {
                var queue = new Queue<int>();
                var visited = new HashSet<int>();
                
                var c = allClusters[selIdx];
                if (c.ParentGroupId != -1 && groupToClusters.TryGetValue(c.ParentGroupId, out var parents))
                {
                    foreach (var p in parents) { queue.Enqueue(p); visited.Add(p); }
                }

                while (queue.Count > 0)
                {
                    int curr = queue.Dequeue();
                    Assert.That(selectedIndices.Contains(curr), Is.False, 
                        $"Overlap: Cluster {selIdx} and its ancestor {curr} are both selected at distance {camDist}.");
                    
                    var cluster = allClusters[curr];
                    if (cluster.ParentGroupId != -1 && groupToClusters.TryGetValue(cluster.ParentGroupId, out var nextParents))
                    {
                        foreach (var p in nextParents)
                        {
                            if (!visited.Contains(p)) { visited.Add(p); queue.Enqueue(p); }
                        }
                    }
                }
            }

            // 2. For every leaf, every path to root must hit exactly one selected cluster.
            foreach (int leafIdx in leafIndices)
            {
                var queue = new Queue<int>();
                queue.Enqueue(leafIdx);
                var visited = new HashSet<int>();
                visited.Add(leafIdx);

                while (queue.Count > 0)
                {
                    int curr = queue.Dequeue();
                    if (selectedIndices.Contains(curr)) continue; // Path satisfied

                    var cluster = allClusters[curr];
                    if (cluster.ParentGroupId == -1)
                    {
                        // Reached a root without finding a selection
                        Assert.Fail($"Hole: Path from leaf {leafIdx} reached root {curr} without any selection at distance {camDist}.");
                    }

                    if (groupToClusters.TryGetValue(cluster.ParentGroupId, out var parents))
                    {
                        foreach (var p in parents)
                        {
                            if (!visited.Contains(p)) { visited.Add(p); queue.Enqueue(p); }
                        }
                    }
                }
            }
            
            TestContext.WriteLine($"Distance {camDist:F2}: Selected {selectedIndices.Count} clusters. Cut is valid.");
        }
    }

    [Test]
    public void ErrorIntervals_ShouldDefineNonEmptyCutForTypicalThresholds()
    {
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);
        var meshAsset = ClusterBuilder.ProcessRaw(vertices, attributes, indices, "AutoCut");
        var payload = meshAsset.Payload!.Value;

        float[] thresholds = [0.5f, 1.0f, 2.0f, 4.0f];
        int[] selectedCounts = new int[thresholds.Length];

        int offset = 0;
        while (offset < payload.Length)
        {
            var page = payload.Span.Slice(offset);
            uint clusterCount = MemoryMarshal.Read<uint>(page.Slice(0, 4));
            uint pageSize = MemoryMarshal.Read<uint>(page.Slice(12, 4));
            uint clustersOffset = MemoryMarshal.Read<uint>(page.Slice(16, 4));

            int clusterByteSize = Marshal.SizeOf<GPUCluster>();
            var clustersSpan = page.Slice((int)clustersOffset, (int)clusterCount * clusterByteSize);
            var clusters = MemoryMarshal.Cast<byte, GPUCluster>(clustersSpan);

            for (int i = 0; i < clusters.Length; i++)
            {
                var c = clusters[i];

                // Use constant dist/scale for interval sanity check only
                float dist = 5.0f;
                float scale = 500.0f;
                float e = c.LODError * scale / dist;
                float pe = c.ParentLODError >= float.MaxValue * 0.5f ? float.PositiveInfinity : c.ParentLODError * scale / dist;

                for (int t = 0; t < thresholds.Length; t++)
                {
                    float th = thresholds[t];
                    if (e <= th && pe > th)
                        selectedCounts[t]++;
                }
            }

            offset += (int)pageSize;
        }

        for (int i = 0; i < thresholds.Length; i++)
            Assert.That(selectedCounts[i], Is.GreaterThan(0), $"Threshold {thresholds[i]} produced empty cut.");
    }
}
