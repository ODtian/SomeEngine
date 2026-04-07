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
                var slotBuffer = binSpace.SlotBuffer;
                
                if (ctx == null || buf == null || slotBuffer == null || slotBuffer.Capacity == 0)
                    return;

                var slotData = slotBuffer.GetData();

                if (slotBuffer.RequiresFullUpload)
                {
                    ctx.UpdateBuffer(buf, 0, (ReadOnlySpan<ushort>)slotData, ResourceStateTransitionMode.None);
                }
                else
                {
                    for (int i = 0; i < slotBuffer.Stride; i++)
                    {
                        if (slotBuffer.TryGetDirtyRange(i, out int min, out int max))
                        {
                            int count = max - min + 1;
                            int offsetInUshorts = i * slotBuffer.Capacity + min;
                            var dirtySpan = slotData.Slice(offsetInUshorts, count);
                            ctx.UpdateBuffer(buf, (ulong)(offsetInUshorts * sizeof(ushort)), (ReadOnlySpan<ushort>)dirtySpan, ResourceStateTransitionMode.None);
                        }
                    }
                }
                slotBuffer.ClearDirty();
            }
        );
        return handle;
    }
}
