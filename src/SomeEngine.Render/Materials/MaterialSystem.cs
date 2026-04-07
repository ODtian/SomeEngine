using Friflo.Engine.ECS;

namespace SomeEngine.Render.Materials;

public sealed class MaterialSystem
{
    public EntityStore Store { get; } = new();
}
