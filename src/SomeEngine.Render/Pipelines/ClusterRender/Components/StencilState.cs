using Diligent;
using Friflo.Engine.ECS;

namespace SomeEngine.Render.Pipelines;

public struct StencilState : IComponent
{
    public byte Ref;
    public StencilOp PassOp;
    public ComparisonFunction Compare;
}
