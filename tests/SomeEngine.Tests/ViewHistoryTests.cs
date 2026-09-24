using SomeEngine.Render.Frame;
using SomeEngine.Render.Graph;
using SomeEngine.Rhi;

namespace SomeEngine.Tests;

public sealed class ViewHistoryTests
{
    [Fact]
    public void ImportTemporalSceneColor_CreatesCurrentAndKeepsProducerLive()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var history = new ViewHistory();
        using var graph = new RenderGraph();

        graph.BeginFrame();
        RenderHistoryTexture temporal = history.ImportTemporalSceneColor(
            graph,
            device,
            ColorDesc(256, 128));
        RenderGraphTestHelpers.WriteTexture(
            graph,
            temporal.Current,
            "ProduceHistory",
            ResourceState.RenderTarget);

        graph.Compile();

        Assert.True(temporal.Current.IsValid);
        Assert.False(temporal.Previous.IsValid);
        Assert.False(temporal.HasPrevious);
        Assert.Equal(["ProduceHistory"], RenderGraphTestHelpers.ExecutedPassNames(graph));
    }

    [Fact]
    public void ImportTemporalSceneColor_ReusesPersistentTextureAcrossFrames()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var history = new ViewHistory();
        using var graph = new RenderGraph();

        ExecuteFrame(graph, history, device, queue, "FirstHistorySource");
        ExecuteFrame(graph, history, device, queue, "SecondHistorySource");
        int texturesAfterWarmup = RenderGraphTestHelpers.LiveHandleCount(device, "Textures");
        int viewsAfterWarmup = RenderGraphTestHelpers.LiveHandleCount(device, "TextureViews");

        ExecuteFrame(graph, history, device, queue, "ThirdHistorySource");

        Assert.Equal(texturesAfterWarmup, RenderGraphTestHelpers.LiveHandleCount(device, "Textures"));
        Assert.Equal(viewsAfterWarmup, RenderGraphTestHelpers.LiveHandleCount(device, "TextureViews"));
    }

    [Fact]
    public void ImportTemporalSceneColor_ExtractsCurrentForNextFrameHistory()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var history = new ViewHistory();
        using var graph = new RenderGraph();

        ExecuteFrame(graph, history, device, queue, "FirstHistorySource");

        graph.BeginFrame();
        RenderHistoryTexture temporal = history.ImportTemporalSceneColor(
            graph,
            device,
            new TextureDesc
            {
                Name = "PersistentHistory",
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                InitialState = ResourceState.Common,
            },
            ResourceState.ShaderResource);

        Assert.True(temporal.Current.IsValid);
        Assert.True(temporal.Previous.IsValid);
        Assert.True(temporal.HasPrevious);
        Assert.NotEqual(temporal.Current, temporal.Previous);
    }

    private static void ExecuteFrame(
        RenderGraph graph,
        ViewHistory history,
        IDevice device,
        IQueue queue,
        string sourceName)
    {
        graph.BeginFrame();
        RenderHistoryTexture temporal = history.ImportTemporalSceneColor(
            graph,
            device,
            new TextureDesc
            {
                Name = "PersistentHistory",
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                InitialState = ResourceState.Common,
            },
            ResourceState.ShaderResource);
        RenderGraphHandle source = graph.CreateTexture(
            sourceName,
            new TextureDesc
            {
                Name = sourceName,
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | BindFlags.CopySource,
                InitialState = ResourceState.RenderTarget,
            });
        RenderGraphTestHelpers.WriteTexture(graph, source, $"Initialize {sourceName}", ResourceState.RenderTarget);
        graph.AddRasterPass(
            $"Use {sourceName}",
            builder =>
            {
                builder.Read(source, ResourceState.ShaderResource);
                builder.Write(temporal.Current, ResourceState.RenderTarget);
            },
            context =>
            {
                Assert.True(context.GetTextureView(source, ViewKind.ShaderResource).IsValid);
                Assert.True(context.GetTextureView(temporal.Current, ViewKind.RenderTarget).IsValid);
            });
        graph.Execute(device, queue);
    }

    private static TextureDesc ColorDesc(uint width, uint height)
        => new()
        {
            Dimension = ResourceDimension.Texture2D,
            Width = width,
            Height = height,
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
}
