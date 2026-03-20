using System;

namespace SomeEngine.Render.Graph;

public interface IRenderGraphPass
{
    string Name { get; }
    void Setup(RenderGraphBuilder builder);
    void Execute(RenderGraphContext context);
}

internal class LambdaRenderGraphPass<TData>(
    string name,
    TData data,
    Action<RenderGraphBuilder, TData> setup,
    Action<RenderGraphContext, TData> execute
) : IRenderGraphPass
    where TData : class, new()
{
    public string Name => name;

    public void Setup(RenderGraphBuilder builder) => setup(builder, data);

    public void Execute(RenderGraphContext context) => execute(context, data);
}

internal class LambdaRenderGraphPass(
    string name,
    Action<RenderGraphBuilder> setup,
    Action<RenderGraphContext> execute
) : IRenderGraphPass
{
    public string Name => name;

    public void Setup(RenderGraphBuilder builder) => setup(builder);

    public void Execute(RenderGraphContext context) => execute(context);
}
