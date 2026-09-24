using SomeEngine.Render.Graph;
using SomeEngine.Rhi;

namespace SomeEngine.Render.RHI;

internal readonly record struct TextureCopyRequest(
    RenderGraphHandle Source,
    TextureCopyRegion SourceRegion,
    RenderGraphHandle Destination,
    TextureCopyRegion DestinationRegion);

internal static class TextureCopyPasses
{
    public static void AddCopyPass(
        RenderGraph graph,
        string name,
        RenderGraphHandle source,
        RenderGraphHandle destination,
        bool keepDestinationLive = false)
    {
        ValidateArgs(graph, name, source, destination);

        graph.AddCopyPass(
            name,
            builder =>
            {
                builder.Read(source, ResourceState.CopySource);
                builder.Write(destination, ResourceState.CopyDestination);
            },
            context =>
            {
                var sourceDesc = context.GetTextureDesc(source);
                var destinationDesc = context.GetTextureDesc(destination);
                EnsureFullCopy(sourceDesc, destinationDesc, name);

                for (uint slice = 0; slice < sourceDesc.ArraySize; slice++)
                {
                    for (uint mip = 0; mip < sourceDesc.MipLevels; mip++)
                    {
                        var region = FullSubresource(sourceDesc, mip, slice);
                        context.CopyTexture(source, region, destination, region);
                    }
                }
            });

        if (keepDestinationLive)
            graph.SetFinalState(destination, ResourceState.CopyDestination);
    }

    internal static void AddCopyBatch(
        RenderGraph graph,
        string name,
        TextureCopyRequest[] requests)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Length == 0)
            throw new ArgumentException("texture copy batch must contain at least one request.", nameof(requests));

        for (int i = 0; i < requests.Length; i++)
            ValidateRequest(graph, requests[i], nameof(requests));

        graph.AddCopyPass(
            name,
            builder =>
            {
                foreach (var request in requests)
                {
                    builder.Read(
                        request.Source,
                        ResourceState.CopySource,
                        new SubResourceRange(request.SourceRegion.MipLevel, 1, request.SourceRegion.ArraySlice, 1));
                    builder.Write(
                        request.Destination,
                        ResourceState.CopyDestination,
                        new SubResourceRange(request.DestinationRegion.MipLevel, 1, request.DestinationRegion.ArraySlice, 1));
                }
            },
            context =>
            {
                foreach (var request in requests)
                {
                    context.CopyTexture(
                        request.Source,
                        request.SourceRegion,
                        request.Destination,
                        request.DestinationRegion);
                }
            });
    }

    public static void AddCopyPass(
        RenderGraph graph,
        string name,
        RenderGraphHandle source,
        TextureCopyRegion sourceRegion,
        RenderGraphHandle destination,
        TextureCopyRegion destinationRegion,
        bool keepDestinationLive = false)
    {
        ValidateArgs(graph, name, source, destination);
        ValidateRegion(sourceRegion, nameof(sourceRegion));
        ValidateRegion(destinationRegion, nameof(destinationRegion));

        graph.AddCopyPass(
            name,
            builder =>
            {
                builder.Read(
                    source,
                    ResourceState.CopySource,
                    new SubResourceRange(sourceRegion.MipLevel, 1, sourceRegion.ArraySlice, 1));
                builder.Write(
                    destination,
                    ResourceState.CopyDestination,
                    new SubResourceRange(destinationRegion.MipLevel, 1, destinationRegion.ArraySlice, 1));
            },
            context =>
            {
                context.CopyTexture(
                    source,
                    sourceRegion,
                    destination,
                    destinationRegion);
            });

        if (keepDestinationLive)
            graph.SetFinalState(destination, ResourceState.CopyDestination);
    }

    private static void ValidateArgs(
        RenderGraph graph,
        string name,
        RenderGraphHandle source,
        RenderGraphHandle destination)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!source.IsValid)
            throw new ArgumentException("texture copy source must be valid.", nameof(source));
        if (!destination.IsValid)
            throw new ArgumentException("texture copy destination must be valid.", nameof(destination));
    }

    private static void ValidateRequest(
        RenderGraph graph,
        TextureCopyRequest request,
        string paramName)
    {
        ValidateArgs(graph, "TextureCopyBatch", request.Source, request.Destination);
        ValidateRegion(request.SourceRegion, paramName);
        ValidateRegion(request.DestinationRegion, paramName);
    }

    private static void EnsureFullCopy(TextureDesc source, TextureDesc destination, string name)
    {
        if (source.Dimension != destination.Dimension
            || source.Width != destination.Width
            || source.Height != destination.Height
            || source.Depth != destination.Depth
            || source.MipLevels != destination.MipLevels
            || source.ArraySize != destination.ArraySize
            || source.SampleCount != destination.SampleCount
            || source.SampleCount != 1
            || source.Format != destination.Format)
        {
            throw new InvalidOperationException(
                $"Texture copy pass '{name}' requires matching single-sampled source and destination texture descriptors.");
        }
    }

    private static void ValidateRegion(TextureCopyRegion region, string parameterName)
    {
        if (region.Width == 0 || region.Height == 0 || region.Depth == 0)
            throw new ArgumentOutOfRangeException(parameterName, "texture copy region extent must be non-zero.");
    }

    private static TextureCopyRegion FullSubresource(TextureDesc desc, uint mipLevel, uint arraySlice)
        => new(
            mipLevel,
            arraySlice,
            0,
            0,
            0,
            MipExtent(desc.Width, mipLevel),
            MipExtent(desc.Height, mipLevel),
            MipExtent(desc.Depth, mipLevel));

    private static uint MipExtent(uint value, uint mipLevel)
        => Math.Max(1u, value >> checked((int)mipLevel));
}
