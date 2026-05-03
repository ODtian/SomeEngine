using Friflo.Engine.ECS;

namespace SomeEngine.Render.Pipelines;

public struct ClusterShadeComponent : IComponent
{
    public ShaderVariantRef Default;

    public static void CopyValue(in ClusterShadeComponent source, ref ClusterShadeComponent target, in CopyContext context)
    {
        target = source;
    }
}
