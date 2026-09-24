namespace SomeEngine.Rhi;

internal enum BindStateKind
{
    Buffer,
    Texture,
}

internal readonly record struct BindState(
    BindStateKind Kind,
    BufferHandle Buffer,
    TextureHandle Texture,
    SubresourceRange TextureRange,
    ResourceState State,
    string Label)
{
    public static BindState ForBuffer(BufferHandle buffer, ResourceState state, string label)
        => new(BindStateKind.Buffer, buffer, default, default, state, label);

    public static BindState ForTexture(
        TextureHandle texture,
        SubresourceRange range,
        ResourceState state,
        string label)
        => new(BindStateKind.Texture, default, texture, range, state, label);
}

public enum BindTarget
{
    None,
    BufferView,
    TextureView,
    Sampler,
    AccelerationStructure,
}

public readonly record struct BindStateInfo(
    BindTarget Target,
    ResourceState State,
    string Label);

public static class BindRules
{
    public static BindStateInfo Resolve(BindingType type)
        => type switch
        {
            BindingType.ConstantBuffer => new(
                BindTarget.BufferView,
                ResourceState.ConstantBuffer,
                "Constant buffer binding"),
            BindingType.StorageBufferRead or BindingType.RawBufferRead => new(
                BindTarget.BufferView,
                ResourceState.ShaderResource,
                "Read-only buffer binding"),
            BindingType.StorageBufferReadWrite or BindingType.RawBufferReadWrite => new(
                BindTarget.BufferView,
                ResourceState.UnorderedAccess,
                "Read-write buffer binding"),
            BindingType.TextureRead => new(
                BindTarget.TextureView,
                ResourceState.ShaderResource,
                "Texture SRV binding"),
            BindingType.TextureReadWrite => new(
                BindTarget.TextureView,
                ResourceState.UnorderedAccess,
                "Texture UAV binding"),
            BindingType.Sampler => new(BindTarget.Sampler, default, string.Empty),
            BindingType.AccelerationStructure => new(
                BindTarget.AccelerationStructure,
                default,
                string.Empty),
            BindingType.None => new(BindTarget.None, default, string.Empty),
            _ => throw new RhiException(
                ErrorCode.InvalidDescriptor,
                $"Binding type {type} is not defined."),
        };

    public static bool HasBufferView(BindingType type)
        => Resolve(type).Target == BindTarget.BufferView;
}
