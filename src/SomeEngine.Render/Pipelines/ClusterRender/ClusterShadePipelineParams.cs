using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Cluster Shade 管线公共资源参数。
/// 替代 ClusterMaterialShadePass.BindPipelineResources() 的手写字符串绑定。
/// 管线资源全部是 Dynamic（每帧重绑）。
/// </summary>
public partial class ClusterShadePipelineParams : IShaderParams
{
    [ShaderParam(Dynamic = true)] public TextureSlot VisBuffer;
    [ShaderParam(Dynamic = true)] public BufferSlot VisibleClusters;
    [ShaderParam(Dynamic = true)] public BufferSlot PageHeap;
    [ShaderParam(Dynamic = true)] public BufferSlot Instances;
    [ShaderParam(Dynamic = true)] public BufferSlot PixelCoordBuffer;
    [ShaderParam(Dynamic = true)] public BufferSlot BinOffsets;
    [ShaderParam(Dynamic = true)] public TextureSlot OutputColor;
    [ShaderParam(Dynamic = true)] public BufferSlot Uniforms;
    [ShaderParam(Dynamic = true)] public BufferSlot InstanceHeaders;
    [ShaderParam(Dynamic = true)] public BufferSlot InstanceDataHeap;

}
