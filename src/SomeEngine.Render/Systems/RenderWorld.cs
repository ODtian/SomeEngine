using Friflo.Engine.ECS;

namespace SomeEngine.Render.Systems;

public sealed class RenderWorld
{
    public EntityStore Store { get; private set; } = new();

    internal void Reset()
    {
        Store = new EntityStore();
    }
}
