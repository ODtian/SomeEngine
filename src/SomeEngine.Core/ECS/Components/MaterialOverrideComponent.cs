using System.Numerics;
using Friflo.Engine.ECS;

namespace SomeEngine.Core.ECS.Components;

public struct MaterialOverride : IComponent
{
    public Vector4 BaseColorTint;
}
