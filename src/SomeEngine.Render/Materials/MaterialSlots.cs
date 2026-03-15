using Diligent;

namespace SomeEngine.Render.Materials;

/// <summary>
/// 纹理槽位：同时持有 Asset 引用（序列化）和 GPU 视图（运行时）。
/// </summary>
public struct TextureSlot
{
    /// <summary>TextureAsset 路径，序列化用。</summary>
    public string? AssetPath;

    /// <summary>运行时 GPU 视图，从 AssetPath 懒加载或手动设置。</summary>
    public ITextureView? View;

    public static implicit operator TextureSlot(ITextureView view)
        => new() { View = view };

    public static implicit operator TextureSlot(string assetPath)
        => new() { AssetPath = assetPath };
}

/// <summary>
/// Buffer 槽位：同时持有 Asset 引用和运行时视图。
/// 支持 StructuredBuffer (IBufferView) 和 ConstantBuffer (IBuffer) 两种绑定方式。
/// </summary>
public struct BufferSlot
{
    public string? AssetPath;
    /// <summary>StructuredBuffer / FormattedBuffer 视图。</summary>
    public IBufferView? View;
    /// <summary>ConstantBuffer 直接绑定（无需 View）。</summary>
    public IBuffer? Buffer;

    public static implicit operator BufferSlot(IBufferView view)
        => new() { View = view };

    public static implicit operator BufferSlot(IBuffer buffer)
        => new() { Buffer = buffer };

    public static implicit operator BufferSlot(string assetPath)
        => new() { AssetPath = assetPath };
}

/// <summary>
/// 采样器槽位。
/// </summary>
public struct SamplerSlot
{
    public string? AssetPath;
    public ISampler? Sampler;

    public static implicit operator SamplerSlot(ISampler sampler)
        => new() { Sampler = sampler };

    public static implicit operator SamplerSlot(string assetPath)
        => new() { AssetPath = assetPath };
}
