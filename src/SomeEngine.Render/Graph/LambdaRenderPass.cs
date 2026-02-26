using System;
using Diligent;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Graph;

internal class LambdaRenderPass<TData>(
    string name,
    TData data,
    Action<RenderGraphBuilder, TData> setup,
    Action<RenderGraphContext, TData> execute
) : RenderPass(name)
    where TData : class, new()
{
    public override void Setup(RenderGraphBuilder builder)
    {
        setup(builder, data);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        execute(graphContext, data);
    }
}
