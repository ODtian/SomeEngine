using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// BinSpace 的 RenderGraph 扩展方法。保持 Materials 层不依赖 Graph。
/// </summary>
public static class BinSpaceExtensions
{
    /// <summary>
    /// 创建 MaterialSlotBuffer 并添加 upload pass 到 RenderGraph。
    /// </summary>
    public static RenderGraphHandle AddUploadPass(this BinSpace binSpace, RenderGraph graph)
    {
        int totalUshorts = binSpace.SlotCapacity * binSpace.Stride;
        var handle = graph.CreateBuffer("MaterialSlotBuffer", new BufferDesc
        {
            Size = (ulong)((totalUshorts == 0 ? 1 : totalUshorts) * sizeof(ushort)),
            BindFlags = BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 4,
        });

        graph.AddPass<object>(
            "UploadMaterialSlotBuffer",
            (builder, _) => { builder.Write(handle, ResourceState.CopyDest); },
            (rgCtx, _) =>
            {
                var ctx = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(handle);
                var slotData = binSpace.GetData();
                if (ctx != null && buf != null && slotData.Length > 0)
                {
                    ctx.UpdateBuffer(buf, 0, (ReadOnlySpan<ushort>)slotData,
                        ResourceStateTransitionMode.Verify);
                }
            }
        );
        return handle;
    }
}
