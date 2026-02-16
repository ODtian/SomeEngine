using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Importers;
using SomeEngine.Render.Systems;

namespace SomeEngine.Tests;

[TestFixture]
public class ClusterSelectionTests
{
    [Test]
    public void TestSelection_LinearHierarchy()
    {
        // 1. Setup a simple hierarchy
        // Root (LOD 1) -> Child (LOD 0)
        
        // Root: Center (0,0,100), Radius 10, Error 10.
        // Child: Center (0,0,100), Radius 5, Error 1.
        
        var root = new GPUCluster
        {
            Center = new Vector3(0, 0, 100),
            Radius = 10.0f,
            LodCenter = new Vector3(0, 0, 100),
            LodRadius = 10.0f,
            LODError = 10.0f,
            ParentLODError = float.MaxValue, // Is Root
            LODLevel = 1
        };

        var child = new GPUCluster
        {
            Center = new Vector3(0, 0, 100),
            Radius = 5.0f,
            LodCenter = new Vector3(0, 0, 100),
            LodRadius = 5.0f,
            LODError = 1.0f,
            ParentLODError = 10.0f, // Matches Root.LODError
            LODLevel = 0
        };

        var clusters = new GPUCluster[] { child, root };

        // 2. Test Case A: Camera Far Away
        // Dist = 1000. 
        // d_root = 990. RootMetric = 10/990 ~= 0.01
        // d_child = 995. ChildMetric = 1/995 ~= 0.001
        
        // Threshold = 0.02.
        // RootMetric (0.01) < 0.02 -> Root is Good.
        // ChildMetric (0.001) < 0.02 -> Child is Good.
        // Parent of Root (Max) -> Bad.
        // Expected: Select Root.
        
        var selected = new List<int>();
        // Camera at -900, Cluster at 100 -> Dist 1000
        ClusterSelection.SelectClustersLinear(clusters, new Vector3(0, 0, -900), 0.02f, selected);
        
        Assert.That(selected, Contains.Item(1), "Should select Root when far away");
        Assert.That(selected, Does.Not.Contain(0), "Should NOT select Child when Root is good");

        // 3. Test Case B: Camera Closer
        // Dist = 200. (Camera at -100)
        // d_root = 190. RootMetric = 10/190 ~= 0.052
        // d_child = 195. ChildMetric = 1/195 ~= 0.005
        
        // Threshold = 0.02.
        // RootMetric (0.052) >= 0.02 -> Root is Bad.
        // ChildMetric (0.005) < 0.02 -> Child is Good.
        // Parent of Child (Root) is Bad.
        // Expected: Select Child.
        
        selected.Clear();
        ClusterSelection.SelectClustersLinear(clusters, new Vector3(0, 0, -100), 0.02f, selected);
        
        Assert.That(selected, Contains.Item(0), "Should select Child when closer");
        Assert.That(selected, Does.Not.Contain(1), "Should NOT select Root when it is bad");

        // 4. Test Case C: Camera Very Close
        // Dist = 20. (Camera at 80)
        // d_root = 10. RootMetric = 10/10 = 1.0
        // d_child = 15. ChildMetric = 1/15 = 0.066
        
        // Threshold = 0.02.
        // RootMetric >= 0.02 -> Bad.
        // ChildMetric >= 0.02 -> Bad.
        // Expected: Select Nothing (or fall back to finest if we had indices, but here we just check selection logic)
        // If Child is Bad, we don't select it.
        
        selected.Clear();
        ClusterSelection.SelectClustersLinear(clusters, new Vector3(0, 0, 80), 0.02f, selected);
        
        Assert.That(selected, Is.Empty, "Should select nothing if even finest is bad (requires subdivision or higher detail mesh)");
    }

    [Test]
    public void TestSelection_BoundaryCondition()
    {
        // Case: Error / Dist == Threshold
        // Error = 10, Dist = 100 -> Metric = 0.1
        // Threshold = 0.1
        
        var cluster = new GPUCluster
        {
            Center = new Vector3(0, 0, 100),
            Radius = 0.0f, // Making Radius 0 to simplify Dist calc
            LodCenter = new Vector3(0, 0, 100),
            LodRadius = 0.0f,
            LODError = 10.0f,
            ParentLODError = float.MaxValue, // Root
            LODLevel = 0
        };

        var clusters = new GPUCluster[] { cluster };
        var selected = new List<int>();
        
        // Camera at Origin. Dist = 100.
        // Metric = 10 / 100 = 0.1.
        // Threshold = 0.1.
        
        ClusterSelection.SelectClustersLinear(clusters, Vector3.Zero, 0.1f, selected);
        
        // Shader Logic: 0.1 <= 0.1 (True). Parent Max > 0.1 (True). -> Select.
        // Current C# Logic Matches GPU (<= logic).
        
        Assert.That(selected, Contains.Item(0), "Should select cluster when Error/Dist == Threshold (Matches GPU <= logic)");
    }

    [Test]
    public void TestSelection_HoleDetection()
    {
        // Setup according to the dual-sphere fix:
        // Parent cluster P:
        // - P.Center: Sphere of its child group (Group L0)
        // - P.LodCenter: Sphere of its own group (Group L1)
        
        // Group L0 sphere: Center (5,0,100), Radius 2.
        // Group L1 sphere: Center (0,0,100), Radius 10.
        
        var parent = new GPUCluster
        {
            Center = new Vector3(5, 0, 100), // Sphere of children
            Radius = 2.0f,
            LodCenter = new Vector3(0, 0, 100), // Sphere of parent group
            LodRadius = 10.0f,
            LODError = 5.0f,
            ParentLODError = 100.0f,
            LODLevel = 1
        };

        var child = new GPUCluster
        {
            Center = new Vector3(5, 0, 100), // Own tight center (or child group if had one)
            Radius = 2.0f,
            LodCenter = new Vector3(5, 0, 100), // Parent group sphere (which is L0 sphere)
            LodRadius = 2.0f,
            LODError = 1.0f,
            ParentLODError = 5.0f,
            LODLevel = 0
        };

        var clusters = new GPUCluster[] { child, parent };
        float threshold = 0.052f;
        
        // Evaluate at (0,0,0)
        // Parent: 
        // d_self (Center) = dist((5,0,100)) - 2 = 98.12. MetricSelf = 5 / 98.12 = 0.0509... (Good)
        // d_parent (LodCenter) = dist((0,0,100)) - 10 = 90. MetricParent = 100 / 90 = 1.11... (Bad)
        // Result: Parent Selected.
        
        // Child:
        // d_self (Center) = 98.12. MetricSelf = 1 / 98.12 = 0.01 (Good)
        // d_parent (LodCenter) = 98.12. MetricParent = 5 / 98.12 = 0.0509... (Good)
        // Result: Child NOT Selected (Parent is Good).
        
        var selected = new List<int>();
        ClusterSelection.SelectClustersLinear(clusters, Vector3.Zero, threshold, selected);
        
        Assert.That(selected, Contains.Item(1), "Should select Parent. If empty, it's a hole!");
    }

    [Test]
    public void TestSelection_ContinuousMovement()
    {
        // Setup hierarchy: LOD 0 (Fine) -> LOD 1 -> LOD 2 (Root)
        var lod0 = new GPUCluster
        {
            LodCenter = Vector3.Zero,
            LodRadius = 0,
            LODError = 1.0f,
            ParentLODError = 5.0f,
            LODLevel = 0
        };
        var lod1 = new GPUCluster
        {
            LodCenter = Vector3.Zero,
            LodRadius = 0,
            LODError = 5.0f,
            ParentLODError = 20.0f,
            LODLevel = 1
        };
        var lod2 = new GPUCluster
        {
            LodCenter = Vector3.Zero,
            LodRadius = 0,
            LODError = 20.0f,
            ParentLODError = float.MaxValue,
            LODLevel = 2
        };

        var clusters = new GPUCluster[] { lod0, lod1, lod2 };
        float threshold = 0.1f;
        var selected = new List<int>();

        // Sweep distance from 1 to 300 with 0.01 steps
        for (float dist = 1.0f; dist <= 300.0f; dist += 0.01f)
        {
            selected.Clear();
            ClusterSelection.SelectClustersLinear(clusters, new Vector3(0, 0, dist), threshold, selected);

            if (dist < 10.0f)
            {
                // All LODs are "too coarse" (Error/Dist > 0.1)
                Assert.That(selected, Is.Empty, $"Dist {dist}: Should select nothing (finer detail needed)");
            }
            else if (dist < 50.0f)
            {
                // 1/dist <= 0.1 AND 5/dist > 0.1 -> LOD 0
                Assert.That(selected.Count, Is.EqualTo(1), $"Dist {dist}: Should select exactly one LOD");
                Assert.That(selected[0], Is.EqualTo(0), $"Dist {dist}: Should select LOD 0");
            }
            else if (dist < 200.0f)
            {
                // 5/dist <= 0.1 AND 20/dist > 0.1 -> LOD 1
                Assert.That(selected.Count, Is.EqualTo(1), $"Dist {dist}: Should select exactly one LOD");
                Assert.That(selected[0], Is.EqualTo(1), $"Dist {dist}: Should select LOD 1");
            }
            else
            {
                // 20/dist <= 0.1 AND Max/dist > 0.1 -> LOD 2
                Assert.That(selected.Count, Is.EqualTo(1), $"Dist {dist}: Should select exactly one LOD");
                Assert.That(selected[0], Is.EqualTo(2), $"Dist {dist}: Should select LOD 2");
            }
        }
    }
    
    /*
    [Test]
    public void TestSelection_WithClusterBuilder()
    {
        // 1. Generate IcoSphere Clusters
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(4); // Smaller sphere
        var meshAsset = ClusterBuilder.ProcessRaw(vertices, attributes, indices, "SelectionTest");
        
        var payload = meshAsset.Payload!.Value;
        
        // Extract all clusters
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
             var cSpan = MemoryMarshal.Cast<byte, GPUCluster>(pageSpan.Slice((int)clustersOffset, (int)clusterCount * clusterByteSize));
             for(int i=0; i<cSpan.Length; i++) allClusters.Add(cSpan[i]);
             
             offset += (int)pageSize;
        }
        
        var clustersArr = allClusters.ToArray();
        
        // 2. Select with varying thresholds
        var selected = new List<int>();
        Vector3 camPos = new Vector3(0, 0, 50); // Radius is 1. Dist is 50.
        
        // Threshold high (relaxed) -> Should pick Coarse
        ClusterSelection.SelectClustersLinear(clustersArr, camPos, 1.0f, selected);
        // Assert we picked some clusters
        Assert.That(selected.Count, Is.GreaterThan(0));
        
        // Verify we didn't pick overlapping ancestry
        // (This is hard to verify without graph, but we can check if we picked both a node and its parent if we knew the indices)
        // But we can check that we picked roughly the expected count for a sphere.
        int coarseCount = selected.Count;
        
        // Threshold low (strict) -> Should pick Fine
        selected.Clear();
        ClusterSelection.SelectClustersLinear(clustersArr, camPos, 0.0001f, selected);
        int fineCount = selected.Count;
        
        Assert.That(fineCount, Is.GreaterThan(coarseCount), "Stricter threshold should select more (finer) clusters");
    }
    */
}
