namespace SomeEngine.Rhi.D3D12;

internal sealed record D3D12Policy
{
    private const int DefaultCpuDescriptorCapacity = 8192;
    private const int DefaultShaderResourceDescriptorCapacity = 65_536;
    private const int DefaultShaderSamplerDescriptorCapacity = 2048;
    private const int DefaultMaxCachedCommandListsPerQueue = int.MaxValue;

    public int CpuDescriptorCapacity { get; init; } = DefaultCpuDescriptorCapacity;
    public int ShaderResourceDescriptorCapacity { get; init; } = DefaultShaderResourceDescriptorCapacity;
    public int ShaderSamplerDescriptorCapacity { get; init; } = DefaultShaderSamplerDescriptorCapacity;
    public bool PrecreateIndirectCommandSignatures { get; init; } = true;
    public int MaxCachedCommandListsPerQueue { get; init; } = DefaultMaxCachedCommandListsPerQueue;
}
