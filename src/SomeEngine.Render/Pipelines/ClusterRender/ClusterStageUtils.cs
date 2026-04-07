using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Render.Graph;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Shared utility: upload a dynamic uniform buffer.
/// </summary>
public static class ClusterStageUtils
{
    public static RenderGraphHandle AddDynamicUniformPass<T>(RenderGraph graph, string name, T data) where T : unmanaged
    {
        var handle = graph.CreateBuffer(name, new BufferDesc
        {
            Size = (ulong)Marshal.SizeOf<T>(),
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        graph.AddPass(
            $"Upload{name}",
            builder => { builder.Write(handle, ResourceState.ConstantBuffer); },
            rgCtx =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(handle);
                if (ctx2 != null && buf != null)
                {
                    var span = ctx2.MapBuffer<T>(buf, MapType.Write, MapFlags.Discard);
                    span[0] = data;
                    ctx2.UnmapBuffer(buf, MapType.Write);
                }
            }
        );
        return handle;
    }
}
