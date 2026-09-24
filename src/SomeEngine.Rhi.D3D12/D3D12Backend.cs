namespace SomeEngine.Rhi.D3D12;

public static class D3D12Backend
{
    public static IBackendFactory Factory { get; } = new D3D12Factory();
}
