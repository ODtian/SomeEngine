using SomeECS.Core;
using SomeECS.Systems;

namespace SomeEngine.Core.ECS;

public readonly struct EngineSystemContext
{
    public EngineSystemContext(
        World world,
        SystemContext systemContext,
        uint lastSystemVersion,
        uint currentSystemVersion)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        SystemContext = systemContext ?? throw new ArgumentNullException(nameof(systemContext));
        LastSystemVersion = lastSystemVersion;
        CurrentSystemVersion = currentSystemVersion;
    }

    public World World { get; }

    public SystemContext SystemContext { get; }

    public uint LastSystemVersion { get; }

    public uint CurrentSystemVersion { get; }
}

internal sealed class EngineDriver : ISystemDriver<EngineSystemContext>
{
    private readonly World _world;
    private readonly SystemContext _systemContext;

    public EngineDriver(World world, SystemContext systemContext)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _systemContext = systemContext ?? throw new ArgumentNullException(nameof(systemContext));
    }

    public uint AcquireSystemVersion(ref SystemSlot slot)
    {
        return _world.AcquireSystemTick();
    }

    public EngineSystemContext CreateContext(ref SystemSlot slot)
    {
        return new EngineSystemContext(
            _world,
            _systemContext,
            slot.LastSystemVersion,
            slot.CurrentSystemVersion);
    }
}
