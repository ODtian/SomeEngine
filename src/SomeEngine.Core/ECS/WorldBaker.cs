using SomeECS.Core;
using SomeECS.Core.Entities;
using SomeECS.Core.Queries;

namespace SomeEngine.Core.ECS;

public sealed class WorldBaker
{
    private readonly IWorldBaker[] _bakers;
    private readonly List<EntityId> _clearScratch = [];

    public WorldBaker(params IWorldBaker[] bakers)
    {
        ArgumentNullException.ThrowIfNull(bakers);
        _bakers = bakers;
    }

    public void Rebuild(World authoringWorld, World runtimeWorld)
    {
        ArgumentNullException.ThrowIfNull(authoringWorld);
        ArgumentNullException.ThrowIfNull(runtimeWorld);

        Clear(runtimeWorld, _clearScratch);

        var context = new BakeContext(authoringWorld, runtimeWorld);
        foreach (IWorldBaker baker in _bakers)
        {
            baker.Bake(context);
        }
    }

    private static void Clear(World runtimeWorld, List<EntityId> scratch)
    {
        QueryHandle query = runtimeWorld.AllEntities();
        runtimeWorld.CollectEntities(query, scratch);

        foreach (EntityId entity in scratch)
        {
            if (runtimeWorld.IsAlive(entity))
                runtimeWorld.DestroyEntity(entity);
        }
    }
}
