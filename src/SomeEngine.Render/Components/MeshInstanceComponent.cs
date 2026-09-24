using SomeEngine.Assets;
using SomeEngine.Render.Assets;
using SomeECS.Core.Components;

namespace SomeEngine.Render.Components;

public struct MeshInstance : IComponent
{
    public Handle<Mesh> Mesh;
    public float BoundsExpansion;
}
