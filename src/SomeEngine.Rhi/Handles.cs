namespace SomeEngine.Rhi;

public readonly record struct BufferHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly BufferHandle Invalid = default;
}

public readonly record struct TextureHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly TextureHandle Invalid = default;
}

public readonly record struct TextureViewHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly TextureViewHandle Invalid = default;
}

public readonly record struct BufferViewHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly BufferViewHandle Invalid = default;
}

public readonly record struct SamplerHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly SamplerHandle Invalid = default;
}

public readonly record struct ShaderModuleHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly ShaderModuleHandle Invalid = default;
}

public readonly record struct BindingLayoutHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly BindingLayoutHandle Invalid = default;
}

public readonly record struct PipelineLayoutHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly PipelineLayoutHandle Invalid = default;
}

public readonly record struct BindingSetHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly BindingSetHandle Invalid = default;
}

public readonly record struct PipelineHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly PipelineHandle Invalid = default;
}

public readonly record struct PipelineCacheHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly PipelineCacheHandle Invalid = default;
}

public readonly record struct AccelerationStructureHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly AccelerationStructureHandle Invalid = default;
}

public readonly record struct CommandBufferHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly CommandBufferHandle Invalid = default;
}

public readonly record struct FenceHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly FenceHandle Invalid = default;
}

public readonly record struct QueryPoolHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly QueryPoolHandle Invalid = default;
}

public readonly record struct SwapchainHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly SwapchainHandle Invalid = default;
}

public readonly record struct MemoryHeapHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly MemoryHeapHandle Invalid = default;
}
