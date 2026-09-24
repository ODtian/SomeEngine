namespace SomeEngine.Rhi;

public static class ResourceStateCompatibility
{
    public static bool Satisfies(ResourceState actual, ResourceState required)
        => actual == required
            || actual == ResourceState.GenericRead
                && IsGenericReadState(required)
            || actual == ResourceState.IndirectArgument
                && required == ResourceState.ShaderResource;

    private static bool IsGenericReadState(ResourceState state)
        => state is ResourceState.VertexBuffer
            or ResourceState.IndexBuffer
            or ResourceState.ConstantBuffer
            or ResourceState.ShaderResource
            or ResourceState.CopySource
            or ResourceState.IndirectArgument;
}
