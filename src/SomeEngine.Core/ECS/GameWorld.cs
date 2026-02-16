using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using SomeEngine.Core.ECS.Systems;
using SomeEngine.Core.Jobs;

namespace SomeEngine.Core.ECS;

public class GameWorld
{
    public EntityStore EntityStore { get; }
    public SystemRoot SystemRoot { get; }
    public SystemContext SystemContext { get; }

    public GameWorld()
    {
        EntityStore = new EntityStore();
        SystemContext = new SystemContext();

        SystemRoot = new SystemRoot(EntityStore) {
            new HierarchySystem(EntityStore), new TransformSystem(SystemContext)
        };
    }

    public void Update(double deltaTime)
    {
        SystemContext.GlobalDependency = default;

        SystemRoot.Update(default);

        SystemContext.GlobalDependency.Complete();
    }
}
