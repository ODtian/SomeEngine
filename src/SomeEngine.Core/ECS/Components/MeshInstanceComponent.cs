using Friflo.Engine.ECS;

namespace SomeEngine.Core.ECS.Components;

public struct MeshInstance : IComponent
{
    public uint BVHRootIndex;
    public float BoundsExpansion; // Authored conservative expansion in world units
}
