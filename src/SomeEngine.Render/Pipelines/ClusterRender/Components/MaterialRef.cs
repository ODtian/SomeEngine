using Friflo.Engine.ECS;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Pipelines;

public struct MaterialRef : IComponent
{
    public Material? Owner;
}
