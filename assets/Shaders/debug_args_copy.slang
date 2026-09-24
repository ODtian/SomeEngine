RWByteAddressBuffer IndirectArgs;
RWByteAddressBuffer DebugArgs;

cbuffer CopyUniforms
{
    uint SphereVertexCount;
};

[shader("compute")]
[numthreads(1, 1, 1)]
void CSMain()
{
    uint instanceCount = IndirectArgs.Load(4);

    DebugArgs.Store(0, SphereVertexCount);
    DebugArgs.Store(4, instanceCount);
    DebugArgs.Store(8, 0);
    DebugArgs.Store(12, 0);
}
