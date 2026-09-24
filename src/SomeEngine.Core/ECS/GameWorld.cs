using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.ECS.Systems;
using SomeJob;
using SomeECS.Core;
using SomeECS.Systems;

namespace SomeEngine.Core.ECS;

public sealed class GameWorld : IDisposable
{
    public World World { get; }
    public SystemGroup<EngineSystemContext> Systems { get; }
    public SystemContext SystemContext { get; }

    public GameWorld()
    {
        World = new World();
        SystemContext = new SystemContext();
        Systems = new SystemGroup<EngineSystemContext>(new EngineDriver(World, SystemContext));
        Systems.Add(new TransformSystem());
    }

    public void Update(double deltaTime)
    {
        using var scope = Profiler.BeginScope("GameWorld.Update");
        SystemContext.GlobalDependency = default;

        using (Profiler.BeginScope("GameWorld.Systems.Update"))
        {
            Systems.Update();
        }

        using (Profiler.BeginScope("GameWorld.DependencyComplete"))
        {
            SystemContext.GlobalDependency.Complete();
        }
    }

    public void Dispose()
    {
        Systems.Dispose();
    }
}
