using Vortice.Direct3D12;
using Vortice.DXGI;

namespace SomeEngine.Rhi.D3D12;

public interface ID3D12DeviceInterop
{
    ID3D12Device NativeDevice { get; }
    IDXGIFactory6 Factory { get; }
    IDXGIAdapter1 Adapter { get; }
    BufferHandle ImportBuffer(ExternalBufferDesc desc);
    TextureHandle ImportTexture(ExternalTextureDesc desc);
    ID3D12Resource GetNativeBuffer(BufferHandle buffer);
    ID3D12Resource GetNativeTexture(TextureHandle texture);
    ID3D12Resource GetNativeAcceleration(AccelerationStructureHandle accelerationStructure);
}

public interface ID3D12QueueInterop
{
    ID3D12CommandQueue NativeQueue { get; }
}

public interface ID3D12SwapchainInterop
{
    IDXGISwapChain3 NativeSwapchain { get; }
}

public sealed record ExternalBufferDesc
{
    public string Name { get; init; } = string.Empty;
    public ID3D12Resource? Resource { get; init; }
    public BufferDesc Desc { get; init; } = new();
}

public sealed record ExternalTextureDesc
{
    public string Name { get; init; } = string.Empty;
    public ID3D12Resource? Resource { get; init; }
    public TextureDesc Desc { get; init; } = new();
}
