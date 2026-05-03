using Friflo.Engine.ECS;

namespace SomeEngine.Render.Pipelines;

public struct ClusterDeform : IComponent
{
    public ShaderVariantRef Default;
    public float BoundsExpansion;

    public static void CopyValue(in ClusterDeform source, ref ClusterDeform target, in CopyContext context)
    {
        target = source;
    }
}
