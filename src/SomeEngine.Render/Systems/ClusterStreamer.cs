using System;
using System.Collections.Generic;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Systems;

public class ClusterStreamer(ClusterResourceManager clusterManager)
{
    private readonly HashSet<uint> _pendingFaultNodes = new();

    public uint LastFrameFaultCount { get; private set; }
    public uint LastFrameLoadedPages { get; private set; }

    public void EnqueueFaultNodes(uint[] nodeIndices)
    {
        if (nodeIndices.Length == 0)
            return;

        foreach (uint nodeIndex in nodeIndices)
        {
            _pendingFaultNodes.Add(nodeIndex);
        }
    }

    public void Update()
    {
        if (_pendingFaultNodes.Count == 0)
        {
            LastFrameFaultCount = 0;
            LastFrameLoadedPages = 0;
            return;
        }

        LastFrameFaultCount = (uint)_pendingFaultNodes.Count;

        var requestedPages = new HashSet<uint>();
        foreach (uint nodeIndex in _pendingFaultNodes)
        {
            if (clusterManager.TryGetPageForLeafNode(nodeIndex, out uint pageID))
            {
                requestedPages.Add(pageID);
            }
        }

        uint loadedPages = 0;
        foreach (uint pageID in requestedPages)
        {
            if (clusterManager.IsPageResident(pageID))
            {
                clusterManager.TouchPage(pageID);
                continue;
            }

            if (clusterManager.TryLoadPage(pageID, out uint byteOffset))
            {
                clusterManager.PatchBVHLeafNodes(pageID, byteOffset, true);
                loadedPages++;
            }
        }

        LastFrameLoadedPages = loadedPages;
        _pendingFaultNodes.Clear();
    }
}
