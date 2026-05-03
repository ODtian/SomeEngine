using Diligent;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Graph;

namespace SomeEngine.Tests;

public class FrameTargetRegistryTests
{
    [Fact]
    public void StandardAndCustomTargetsResolveThroughSameRegistry()
    {
        using var graph = new RenderGraph();
        var registry = new FrameTargetRegistry();
        registry.BeginFrame(graph, new FrameTargetContext(640, 360));

        var sceneColor = registry.DeclareTexture(
            StandardFrameTargets.SceneColor,
            static ctx => ColorDesc(ctx.Width, ctx.Height),
            debugName: "SceneColor");
        var customMask = registry.DeclareTexture(
            new FrameTargetKey("Plugin.Mask"),
            static ctx => ColorDesc(ctx.Width, ctx.Height),
            debugName: "Plugin.Mask");

        RenderGraphHandle sceneColorHandle = registry.ResolveTexture(sceneColor);
        RenderGraphHandle customMaskHandle = registry.ResolveTexture(customMask);

        Assert.True(sceneColorHandle.IsValid);
        Assert.True(customMaskHandle.IsValid);
        Assert.NotEqual(sceneColorHandle.Index, customMaskHandle.Index);
        Assert.True(graph.GetResourceHandle("SceneColor").IsValid);
        Assert.True(graph.GetResourceHandle("Plugin.Mask").IsValid);
    }

    [Fact]
    public void CompatibleDeclarationsMergeWithoutBuiltinPreference()
    {
        using var graph = new RenderGraph();
        var registry = new FrameTargetRegistry();
        registry.BeginFrame(graph, new FrameTargetContext(128, 128));
        var key = new FrameTargetKey("Plugin.Shared");

        FrameTargetHandle first = registry.DeclareTexture(key, static ctx => ColorDesc(ctx.Width, ctx.Height));
        FrameTargetHandle second = registry.DeclareTexture(key, static ctx => ColorDesc(ctx.Width, ctx.Height));

        Assert.Equal(first, second);
    }

    [Fact]
    public void IncompatibleDeclarationsFailClearly()
    {
        using var graph = new RenderGraph();
        var registry = new FrameTargetRegistry();
        registry.BeginFrame(graph, new FrameTargetContext(128, 128));
        var key = new FrameTargetKey("Plugin.Shared");

        registry.DeclareTexture(key, static ctx => ColorDesc(ctx.Width, ctx.Height));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            registry.DeclareTexture(key, static ctx => ColorDesc(ctx.Width * 2, ctx.Height)));

        Assert.Contains("incompatible", ex.Message);
    }

    [Fact]
    public void OverrideBeforeFreezeReplacesDeclaration()
    {
        using var graph = new RenderGraph();
        var registry = new FrameTargetRegistry();
        registry.BeginFrame(graph, new FrameTargetContext(128, 128));
        var key = new FrameTargetKey("Plugin.Override");

        registry.DeclareTexture(key, static ctx => ColorDesc(ctx.Width, ctx.Height));
        registry.OverrideTexture(key, static ctx => ColorDesc(ctx.Width * 2, ctx.Height), debugName: "Overridden");

        RenderGraphHandle resolved = registry.ResolveTexture(key);

        Assert.True(resolved.IsValid);
        Assert.True(graph.GetResourceHandle("Overridden").IsValid);
    }

    [Fact]
    public void FreezePreventsLateDeclaration()
    {
        using var graph = new RenderGraph();
        var registry = new FrameTargetRegistry();
        registry.BeginFrame(graph, new FrameTargetContext(128, 128));
        registry.Freeze();

        Assert.Throws<InvalidOperationException>(() =>
            registry.DeclareTexture(new FrameTargetKey("Late"), static ctx => ColorDesc(ctx.Width, ctx.Height)));
    }

    [Fact]
    public void CustomHistoryTargetUsesSameHistoryPathAsStandardTargets()
    {
        using var graph = new RenderGraph();
        var registry = new FrameTargetRegistry();
        registry.BeginFrame(graph, new FrameTargetContext(256, 128));
        var customHistory = registry.DeclareTexture(
            new FrameTargetKey("Plugin.History"),
            static ctx => ColorDesc(ctx.Width, ctx.Height),
            FrameTargetLifetime.History,
            debugName: "PluginHistory");

        FrameTargetHistoryHandles handles = registry.ResolveHistoryTexture(customHistory);

        Assert.True(handles.Current.IsValid);
        Assert.False(handles.Previous.IsValid);
        Assert.False(handles.HasPrevious);
        Assert.True(graph.GetResourceHandle("PluginHistory_B").IsValid);
    }

    private static TextureDesc ColorDesc(uint width, uint height)
    {
        return new TextureDesc
        {
            Type = ResourceDimension.Tex2d,
            Width = width,
            Height = height,
            Format = TextureFormat.RGBA8_UNorm,
            Usage = Usage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | BindFlags.UnorderedAccess,
        };
    }
}
