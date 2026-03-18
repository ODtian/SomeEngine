using Friflo.Engine.ECS;

namespace SomeEngine.Core.ECS.Components;

public struct MeshInstance : IComponent
{
    public uint BVHRootIndex;
    public uint MaterialSlotOffset; // Index into MaterialSlotBuffer
}
