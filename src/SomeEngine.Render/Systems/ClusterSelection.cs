using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Data;

namespace SomeEngine.Render.Systems;

public static class ClusterSelection
{
    /// <summary>
    /// Selects clusters visible to the camera based on LOD error and frustum culling.
    /// Simulates the GPU selection logic.
    /// </summary>
    /// <param name="clusters">Span of all GPU clusters (linear buffer)</param>
    /// <param name="cameraPos">Camera world position</param>
    /// <param name="lodThreshold">LOD Error Threshold (e.g. TargetPixelSize * 2 * tan(fov/2) / ScreenHeight)</param>
    /// <param name="selectedIndices">List to populate with selected cluster indices</param>
    public static void SelectClustersLinear(
        ReadOnlySpan<GPUCluster> clusters,
        Vector3 cameraPos,
        float lodThreshold,
        List<int> selectedIndices)
    {
        for (int i = 0; i < clusters.Length; i++)
        {
            if (IsClusterSelected(in clusters[i], cameraPos, lodThreshold))
            {
                selectedIndices.Add(i);
            }
        }
    }

    /// <summary>
    /// Determines if a single cluster should be selected.
    /// Based on dual-sphere logic to ensure LOD continuity:
    /// SelfError evaluated with cluster.Center/Radius (Group sphere of children)
    /// ParentError evaluated with cluster.LodCenter/Radius (Group sphere of parent)
    /// </summary>
    public static bool IsClusterSelected(ref readonly GPUCluster cluster, Vector3 cameraPos, float lodThreshold)
    {
        // 1. Check Self Error
        float d_self = Math.Max(0.001f, Vector3.Distance(cameraPos, cluster.Center) - cluster.Radius);
        bool selfGood = (cluster.LODError / d_self) <= lodThreshold;
        
        if (!selfGood) 
            return false; // This cluster is too coarse. (Should draw children)

        // 2. Check Parent Error
        if (cluster.ParentLODError >= float.MaxValue)
            return true;

        float d_parent = Math.Max(0.001f, Vector3.Distance(cameraPos, cluster.LodCenter) - cluster.LodRadius);
        bool parentBad = (cluster.ParentLODError / d_parent) > lodThreshold;
        
        return parentBad;
    }
}
