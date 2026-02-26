using System;
using System.Collections.Generic;
using Diligent;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Graph;

public abstract class RenderPass(string name)
{
    public string Name { get; } = name;

    // Called when the graph is built, to declare resource usage
    public virtual void Setup(RenderGraphBuilder builder) { }

    // Called when the graph executes
    public abstract void Execute(RenderContext context, RenderGraphContext graphContext);
}
