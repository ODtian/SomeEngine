using System.Numerics;
using SomeECS.Core.Components;

namespace SomeEngine.Core.ECS.Components;

public struct MaterialOverride : IComponent
{
    public Vector4 BaseColorTint;
}
