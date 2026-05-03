using Friflo.Engine.ECS;

namespace SomeEngine.Core.ECS;

public sealed class BakeContext
{
    public BakeContext(EntityStore authoringStore, EntityStore runtimeStore)
    {
        ArgumentNullException.ThrowIfNull(authoringStore);
        ArgumentNullException.ThrowIfNull(runtimeStore);

        AuthoringStore = authoringStore;
        RuntimeStore = runtimeStore;
    }

    public EntityStore AuthoringStore { get; }

    public EntityStore RuntimeStore { get; }

    public Entity CreateRuntimeEntity()
    {
        return RuntimeStore.CreateEntity();
    }
}
