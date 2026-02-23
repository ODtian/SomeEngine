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
        // Linear selection is no longer fully supported because Parent Error 
        // is now stored in the BVH nodes, not in the cluster itself.
        // To do a full selection, use the BVH traversal.
        
        for (int i = 0; i < clusters.Length; i++)
        {
            selectedIndices.Add(i);
        }
    }

    /// <summary>
    /// Determines if a single cluster should be selected.
    /// Note: This is now only half the check (Self Error).
    /// The other half (Parent Error) must be checked via the BVH node.
    /// </summary>
    public static bool IsClusterSelected(ref readonly GPUCluster cluster, Vector3 cameraPos, float lodThreshold, Vector4 parentSphere, float parentError)
    {
        // 1. Check Parent Error (Skip if parent is already good enough)
        float d_parent = Math.Max(0.001f, Vector3.Distance(cameraPos, new Vector3(parentSphere.X, parentSphere.Y, parentSphere.Z)) - parentSphere.W);
        if ((parentError / d_parent) <= lodThreshold)
            return false;

        // 2. Check Self Error (Skip if I am not good enough, need children)
        // Note: For Self Error evaluation, we use the SAME sphere as the parent 
        // check to maintain hierarchy consistency.
        bool selfGood = (cluster.LODError / d_parent) <= lodThreshold;
        
        return selfGood;
    }
}
