RWByteAddressBuffer IndirectArgs : register(u0); // The main draw args
RWByteAddressBuffer DebugArgs : register(u1);    // The debug sphere draw args

cbuffer CopyUniforms : register(b0)
{
    uint SphereVertexCount;
};

[numthreads(1, 1, 1)]
void main()
{
    // IndirectArgs: [VertexCount, InstanceCount, StartVertex, StartInstance]
    // We only want InstanceCount (offset 4)
    uint instanceCount = IndirectArgs.Load(4);
    
    // DebugArgs: [SphereVertexCount, InstanceCount, 0, 0]
    DebugArgs.Store(0, SphereVertexCount);
    DebugArgs.Store(4, instanceCount);
    DebugArgs.Store(8, 0);
    DebugArgs.Store(12, 0);
}
