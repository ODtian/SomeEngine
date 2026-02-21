RWByteAddressBuffer IndirectArgs; // The main draw args
RWByteAddressBuffer DebugArgs;    // The debug sphere draw args

cbuffer CopyUniforms
{
    uint SphereVertexCount;
};

[shader("compute")]
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
