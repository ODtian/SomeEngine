using System;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Graph;

public interface IParameterSink
{
    void SetParameters(uint setIndex, PassParameters parameters);
    void SetParameters(uint setIndex, PassBindings bindings);
}

public interface IComputeCommands : IParameterSink
{
    void SetPipeline(PipelineHandle pipeline);
    void Barrier(ReadOnlySpan<UavBarrier> barriers);
    void BufferBarrier(RenderGraphHandle handle, ResourceState before, ResourceState after);
    void SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data);
    void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ);
    void DispatchIndirect(
        RenderGraphHandle arguments,
        ulong argumentOffset = 0,
        uint dispatchCount = 1,
        uint strideInBytes = IndirectArgumentSize.Dispatch,
        RenderGraphHandle countBuffer = default,
        ulong countBufferOffset = 0);
}

public readonly struct UavBarrier
{
    internal RenderGraphHandle Handle { get; }
    internal SubResourceRange Range { get; }

    public UavBarrier(RenderGraphHandle handle)
        : this(handle, SubResourceRange.All)
    {
    }

    public UavBarrier(RenderGraphHandle handle, SubResourceRange range)
    {
        Handle = handle;
        Range = range;
    }
}
