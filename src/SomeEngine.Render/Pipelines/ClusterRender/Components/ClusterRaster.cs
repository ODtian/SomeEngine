using Friflo.Engine.ECS;

namespace SomeEngine.Render.Pipelines;

public struct ClusterRaster : IComponent
{
    public ShaderVariantRef SWInline;
    public ShaderVariantRef SWCached;
    public ShaderVariantRef HWVSInline;
    public ShaderVariantRef HWVSCached;
    public ShaderVariantRef HWPS;
}
