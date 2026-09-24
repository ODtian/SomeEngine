using SomeEngine.Render.Graph;
using SomeEngine.Rhi;

namespace SomeEngine.Render.RHI;

internal sealed class BufferUploadBatch(RenderGraph graph, string name, int copyCapacity = 0)
{
    private readonly UploadPack _pack = new(copyCapacity);
    private readonly List<UploadRequest> _requests = [];
    private bool _emitted;

    public int Count => _requests.Count;
    public long ByteCount => _pack.ByteCount;
    public long CopyBytes => _pack.CopyBytes;

    public void AddUpload(
        RenderGraphHandle destination,
        ulong destinationOffset,
        ReadOnlySpan<byte> data)
    {
        int sourceOffset = PrepareAdd(destination, data.Length, nameof(data));
        _pack.Copy(destinationOffset, data);
        AddRequest(destination, destinationOffset, sourceOffset, data.Length);
    }

    public void AddMemory(
        RenderGraphHandle destination,
        ulong destinationOffset,
        ReadOnlyMemory<byte> data)
    {
        int sourceOffset = PrepareAdd(destination, data.Length, nameof(data));
        _pack.Add(destinationOffset, data);
        AddRequest(destination, destinationOffset, sourceOffset, data.Length);
    }

    private int PrepareAdd(RenderGraphHandle destination, int length, string parameterName)
    {
        if (_emitted)
            throw new InvalidOperationException("buffer upload batch has already emitted its pass.");
        if (!destination.IsValid)
            throw new ArgumentException("buffer upload destination must be valid.", nameof(destination));
        if (length == 0)
            throw new ArgumentException("buffer upload data must not be empty.", parameterName);
        if (length > int.MaxValue - _pack.ByteCount)
            throw new ArgumentOutOfRangeException(parameterName, "buffer upload batch is too large.");

        return checked((int)_pack.ByteCount);
    }

    private void AddRequest(
        RenderGraphHandle destination,
        ulong destinationOffset,
        int sourceOffset,
        int byteCount)
    {
        _requests.Add(new UploadRequest(
            destination,
            destinationOffset,
            checked((ulong)sourceOffset),
            checked((ulong)byteCount)));
    }

    public void AddPass()
    {
        var copyRequests = new List<BufferCopyRequest>(_requests.Count);
        AddCopiesTo(copyRequests);
        BufferCopyPasses.AddCopyBatch(graph, name, [.. copyRequests]);
    }

    internal void AddCopiesTo(List<BufferCopyRequest> copyRequests)
    {
        if (_emitted)
            throw new InvalidOperationException("buffer upload batch has already emitted its pass.");
        _emitted = true;

        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(copyRequests);
        if (_requests.Count == 0)
            throw new InvalidOperationException("buffer upload batch must contain at least one request.");

        int totalBytes = checked((int)_pack.ByteCount);
        if (_pack.Count != _requests.Count)
            throw new InvalidOperationException("buffer upload batch payload count does not match request count.");

        ReadOnlyMemory<byte> packedData = default;
        if (!_pack.TryPacked(out packedData))
        {
            UploadItem[] payloads = _pack.Take();
            byte[] ownedData = new byte[totalBytes];
            int offset = 0;
            foreach (var payload in payloads)
            {
                payload.Data.Span.CopyTo(ownedData.AsSpan(offset));
                offset += payload.Data.Length;
            }

            packedData = ownedData;
        }

        string sourceName = $"{name} Source";
        ulong sourceCapacity = checked((ulong)packedData.Length);
        RenderGraphHandle source;
        ulong baseSourceOffset;
        if (!graph.TryImportFrameUploadBuffer(sourceName, packedData.Span, sourceCapacity, out source, out baseSourceOffset))
        {
            source = graph.CreateBuffer(
                sourceName,
                new BufferDesc
                {
                    Name = sourceName,
                    SizeInBytes = sourceCapacity,
                    Memory = MemoryClass.CpuUpload,
                    BindFlags = BindFlags.CopySource,
                    InitialState = ResourceState.CopySource,
                },
                packedData.Span);
            baseSourceOffset = 0;
        }

        for (int i = 0; i < _requests.Count; i++)
        {
            var request = _requests[i];
            copyRequests.Add(new BufferCopyRequest(
                source,
                request.Destination,
                checked(baseSourceOffset + request.SourceOffset),
                request.DestinationOffset,
                request.ByteCount));
        }
    }

    private readonly record struct UploadRequest(
        RenderGraphHandle Destination,
        ulong DestinationOffset,
        ulong SourceOffset,
        ulong ByteCount);
}

internal static class BufferUploadPasses
{
    public static ulong SourceCapacity(RenderGraph graph, RenderGraphHandle destination)
    {
        ArgumentNullException.ThrowIfNull(graph);
        BufferDesc desc = graph.GetBufferDesc(destination);
        return Math.Max(desc.SizeInBytes, 1);
    }

    public static void AddUploadPass(
        RenderGraph graph,
        string name,
        RenderGraphHandle destination,
        ulong destinationOffset,
        ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!destination.IsValid)
            throw new ArgumentException("buffer upload destination must be valid.", nameof(destination));
        if (data.IsEmpty)
            throw new ArgumentException("buffer upload data must not be empty.", nameof(data));

        string sourceName = $"{name} Source";
        ulong sourceCapacity = checked((ulong)data.Length);
        RenderGraphHandle source;
        ulong sourceOffset;
        if (!graph.TryImportFrameUploadBuffer(sourceName, data, sourceCapacity, out source, out sourceOffset))
        {
            source = graph.CreateBuffer(
                sourceName,
                new BufferDesc
                {
                    Name = sourceName,
                    SizeInBytes = sourceCapacity,
                    Memory = MemoryClass.CpuUpload,
                    BindFlags = BindFlags.CopySource,
                    InitialState = ResourceState.CopySource,
                },
                data);
            sourceOffset = 0;
        }

        BufferCopyPasses.AddCopyPass(
            graph,
            name,
            source,
            destination,
            sourceOffset,
            destinationOffset,
            checked((ulong)data.Length));
    }
}

