using System.Collections.Concurrent;

namespace SomeEngine.Render.Pipelines;

internal sealed class PageStream
{
    private readonly HashSet<uint> _pendingNodes = new();
    private readonly HashSet<uint> _requestedPages = new();
    private readonly Queue<uint> _queuedPages = new();
    private readonly HashSet<uint> _queuedPageSet = new();
    private readonly Dictionary<uint, Task> _loadingPages = new();
    private readonly ConcurrentQueue<(uint PageID, ReadOnlyMemory<byte> Data, Exception? Error)> _completedPages = new();
    private readonly ClusterMeshes _resources;
    private readonly Func<uint, ValueTask<ReadOnlyMemory<byte>>> _loadPageAsync;

    public uint FaultCount { get; private set; }
    public uint LoadedPages { get; private set; }
    public uint ErrorCount { get; private set; }
    public Exception? LastError { get; private set; }
    public int InFlightCount => _loadingPages.Count;
    public int QueuedPageCount => _queuedPageSet.Count;
    public uint RequestedPageCount { get; private set; }

    public PageStream(ClusterMeshes resources)
        : this(resources, pageID => resources.LoadPageAsync(pageID))
    {
    }

    internal PageStream(
        ClusterMeshes resources,
        Func<uint, ValueTask<ReadOnlyMemory<byte>>> loadPageAsync)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _loadPageAsync = loadPageAsync ?? throw new ArgumentNullException(nameof(loadPageAsync));
    }

    public void Push(ReadOnlySpan<uint> nodes)
    {
        if (nodes.IsEmpty)
            return;

        foreach (uint node in nodes)
            _pendingNodes.Add(node);
    }

    public void Update()
    {
        DrainCompleted(out uint loadedPages, out uint failedPages);
        LoadedPages = loadedPages;
        ErrorCount += failedPages;
        FaultCount = 0;
        RequestedPageCount = 0;

        if (_pendingNodes.Count != 0)
        {
            FaultCount = (uint)_pendingNodes.Count;
            _requestedPages.Clear();
            foreach (uint nodeIndex in _pendingNodes)
            {
                if (_resources.TryPage(nodeIndex, out uint pageID))
                {
                    _requestedPages.Add(pageID);
                }
            }

            RequestedPageCount = (uint)_requestedPages.Count;
            foreach (uint pageID in _requestedPages)
            {
                if (_resources.IsPageResident(pageID))
                {
                    _resources.Touch(pageID);
                    continue;
                }

                if (_loadingPages.ContainsKey(pageID) || _queuedPageSet.Contains(pageID))
                    continue;

                _queuedPages.Enqueue(pageID);
                _queuedPageSet.Add(pageID);
            }

            _requestedPages.Clear();
            _pendingNodes.Clear();
        }

        int queuedCount = _queuedPages.Count;
        for (int i = 0; i < queuedCount; i++)
        {
            uint pageID = _queuedPages.Dequeue();
            _queuedPageSet.Remove(pageID);

            if (_resources.IsPageResident(pageID))
            {
                _resources.Touch(pageID);
                continue;
            }

            if (_loadingPages.ContainsKey(pageID))
                continue;

            try
            {
                _loadingPages[pageID] = CompleteLoadAsync(pageID, _loadPageAsync(pageID));
            }
            catch (Exception ex)
            {
                _completedPages.Enqueue((pageID, default, ex));
            }
        }
    }

    private async Task CompleteLoadAsync(uint pageID, ValueTask<ReadOnlyMemory<byte>> load)
    {
        try
        {
            _completedPages.Enqueue((pageID, await load.ConfigureAwait(false), null));
        }
        catch (Exception ex)
        {
            _completedPages.Enqueue((pageID, default, ex));
        }
    }

    private void DrainCompleted(out uint loadedPages, out uint failedPages)
    {
        loadedPages = 0;
        failedPages = 0;
        while (_completedPages.TryDequeue(out var completed))
        {
            _loadingPages.Remove(completed.PageID);
            if (completed.Error != null)
            {
                LastError = completed.Error;
                failedPages++;
                continue;
            }

            if (completed.Data.IsEmpty)
            {
                LastError = new InvalidOperationException($"Cluster page {completed.PageID} load returned no data.");
                failedPages++;
                continue;
            }

            try
            {
                if (_resources.IsPageResident(completed.PageID))
                {
                    _resources.Touch(completed.PageID);
                    continue;
                }

                if (_resources.TryLoad(completed.PageID, completed.Data, out uint byteOffset))
                {
                    _resources.PatchLeaves(completed.PageID, byteOffset, true);
                    loadedPages++;
                }
                else
                {
                    LastError = new InvalidOperationException($"Cluster page {completed.PageID} could not be staged for upload.");
                    failedPages++;
                }
            }
            catch (Exception ex)
            {
                LastError = ex;
                failedPages++;
            }
        }
    }
}
