using SomeEngine.Render.Graph;
using SomeEngine.Rhi;

namespace SomeEngine.Render.RHI;

internal readonly record struct BufferCopyRequest(
    RenderGraphHandle Source,
    RenderGraphHandle Destination,
    ulong SourceOffset,
    ulong DestinationOffset,
    ulong ByteCount);

internal static class BufferCopyPasses
{
    public static void AddCopyPass(
        RenderGraph graph,
        string name,
        RenderGraphHandle source,
        RenderGraphHandle destination,
        ulong sourceOffset,
        ulong destinationOffset,
        ulong byteCount)
        => AddCopyBatch(
            graph,
            name,
            new[] { new BufferCopyRequest(source, destination, sourceOffset, destinationOffset, byteCount) });

    internal static void AddCopyBatch(
        RenderGraph graph,
        string name,
        BufferCopyRequest[] requests)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Length == 0)
            throw new ArgumentException("buffer copy batch must contain at least one request.", nameof(requests));

        for (int i = 0; i < requests.Length; i++)
            ValidateRequest(requests[i], nameof(requests));

        graph.AddCopyPass(
            name,
            builder =>
            {
                var sources = new HashSet<RenderGraphHandle>();
                var destinations = new HashSet<RenderGraphHandle>();
                foreach (var request in requests)
                {
                    if (sources.Add(request.Source))
                        builder.Read(request.Source, ResourceState.CopySource);
                    if (destinations.Add(request.Destination))
                        builder.Write(request.Destination, ResourceState.CopyDestination);
                }
            },
            context =>
            {
                foreach (var request in requests)
                {
                    context.CopyBuffer(
                        request.Source,
                        request.SourceOffset,
                        request.Destination,
                        request.DestinationOffset,
                        request.ByteCount);
                }
            });
    }

    private static void ValidateRequest(BufferCopyRequest request, string paramName)
    {
        if (!request.Source.IsValid)
            throw new ArgumentException("buffer copy source must be valid.", paramName);
        if (!request.Destination.IsValid)
            throw new ArgumentException("buffer copy destination must be valid.", paramName);
        if (request.ByteCount == 0)
            throw new ArgumentOutOfRangeException(paramName, "buffer copy byte count must be greater than zero.");
    }
}

