using Friflo.Engine.ECS;

namespace SomeEngine.Render.Pipelines;

public struct ClusterRaster : IComponent
{
    public ShaderVariantRef SWInline;
    public ShaderVariantRef SWCached;
    public ShaderVariantRef HWVSInline;
    public ShaderVariantRef HWVSCached;
    public ShaderVariantRef HWPS;

    public static void CopyValue(in ClusterRaster source, ref ClusterRaster target, in CopyContext context)
    {
        target = source;
    }
}
