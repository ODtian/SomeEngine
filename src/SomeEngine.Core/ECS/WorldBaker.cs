using Friflo.Engine.ECS;

namespace SomeEngine.Core.ECS;

public sealed class WorldBaker
{
    private readonly IWorldBaker[] _bakers;

    public WorldBaker(params IWorldBaker[] bakers)
    {
        ArgumentNullException.ThrowIfNull(bakers);
        _bakers = bakers;
    }

    public void Rebuild(EntityStore authoringStore, EntityStore runtimeStore)
    {
        ArgumentNullException.ThrowIfNull(authoringStore);
        ArgumentNullException.ThrowIfNull(runtimeStore);

        Clear(runtimeStore);

        var context = new BakeContext(authoringStore, runtimeStore);
        foreach (IWorldBaker baker in _bakers)
        {
            baker.Bake(context);
        }
    }

    private static void Clear(EntityStore runtimeStore)
    {
        List<Entity> entities = [];
        foreach (Entity entity in runtimeStore.Entities)
        {
            entities.Add(entity);
        }

        foreach (Entity entity in entities)
        {
            entity.DeleteEntity();
        }
    }
}
