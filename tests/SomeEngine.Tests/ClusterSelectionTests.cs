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
            LODError = 10.0f,
            LODLevel = 1
        };

        var child = new GPUCluster
        {
            Center = new Vector3(0, 0, 100),
            Radius = 5.0f,
            LODError = 1.0f,
            LODLevel = 0
        };

        // Parent info for Child (which is Root's group info)
        Vector4 childParentSphere = new Vector4(0, 0, 100, 10);
        float childParentError = 10.0f;

        // Parent info for Root (Max)
        Vector4 rootParentSphere = new Vector4(0, 0, 100, 100);
        float rootParentError = 1000.0f;

        float threshold = 0.02f;

        // 2. Test Case A: Camera Far Away (Dist 1000)
        Vector3 camPosFar = new Vector3(0, 0, -900);
        
        // Root Selection
        bool selectRoot = ClusterSelection.IsClusterSelected(root, camPosFar, threshold, rootParentSphere, rootParentError);
        // Child Selection
        bool selectChild = ClusterSelection.IsClusterSelected(child, camPosFar, threshold, childParentSphere, childParentError);
        
        Assert.That(selectRoot, Is.True, "Should select Root when far away");
        Assert.That(selectChild, Is.False, "Should NOT select Child when Parent (Root) is good");

        // 3. Test Case B: Camera Closer (Dist 200)
        Vector3 camPosNear = new Vector3(0, 0, -100);
        
        selectRoot = ClusterSelection.IsClusterSelected(root, camPosNear, threshold, rootParentSphere, rootParentError);
        selectChild = ClusterSelection.IsClusterSelected(child, camPosNear, threshold, childParentSphere, childParentError);
        
        Assert.That(selectRoot, Is.False, "Should NOT select Root when it is bad (too coarse)");
        Assert.That(selectChild, Is.True, "Should select Child when closer");
    }

    /*
    [Test]
    public void TestSelection_BoundaryCondition()
    ...
    */

    
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