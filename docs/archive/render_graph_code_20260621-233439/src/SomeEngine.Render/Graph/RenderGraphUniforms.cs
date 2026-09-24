using System.Runtime.InteropServices;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Graph;

public static class RenderGraphUniforms
{
    private const ulong ConstantBufferAlignment = 256;

    public static RenderGraphHandle AddDynamicUniform<T>(
        RenderGraph graph,
        string name,
        T data) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
        ulong alignedSize = AlignUp((ulong)bytes.Length, ConstantBufferAlignment);
        return graph.CreateBuffer(
            name,
            new BufferDesc
            {
                Name = name,
                SizeInBytes = alignedSize,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.ConstantBuffer,
                InitialState = ResourceState.ConstantBuffer,
            },
            bytes);
    }

    private static ulong AlignUp(ulong value, ulong alignment)
        => (value + alignment - 1) / alignment * alignment;
}
