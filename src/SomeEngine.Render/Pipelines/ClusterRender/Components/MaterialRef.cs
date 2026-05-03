using Friflo.Engine.ECS;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Pipelines;

public struct MaterialRef : IComponent
{
    public Material? Owner;

    public static void CopyValue(in MaterialRef source, ref MaterialRef target, in CopyContext context)
    {
        target = source;
    }
}
