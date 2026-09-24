using SomeEngine.Assets;
using SomeEngine.Core.ECS;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Data;
using SomeEngine.Render.Materials;
using SomeECS.Core;
using SomeECS.Core.Components;
using SomeECS.Core.Entities;

namespace SomeEngine.Render.Components;

public struct RenderSourceEntity : IComponent
{
    public EntityId SourceEntity;
}

public struct RenderSourceLink : ICleanupComponent
{
    public EntityId RenderEntity;
    public int InstanceIndex;
}

public struct RenderInstance : IComponent
{
    public EntityId SourceEntity;
    public int InstanceIndex;
    public GpuTransform Transform;
    public GpuTransform PrevTransform;
    public Handle<Mesh> Mesh;
    public uint DataOffset;
    public InstanceFlags DataFlags;
    public float BoundsExpansion;
}

public struct RenderMaterials : IComponent
{
    public ReadOnlyMemory<Handle<Material>> Materials;
}

[Flags]
public enum InstanceDirtyFlags
{
    None = 0,
    Transform = 1 << 0,
    Header = 1 << 1,
    Data = 1 << 2,
    MaterialHeader = 1 << 3,
    All = Transform | Header | Data | MaterialHeader,
}

public struct InstanceDirty : IEnableableComponent
{
    public InstanceDirtyFlags Flags;
}

internal static class InstanceMarks
{
    public static void Write(
        World world,
        EntityId entity,
        RenderInstance instance,
        InstanceDirtyFlags flags)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (entity == EntityId.Null || !world.IsAlive(entity))
            return;

        Store(world, entity, instance);
        Mark(world, entity, flags);
    }

    public static void Store(World world, EntityId entity, RenderInstance instance)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (entity == EntityId.Null || !world.IsAlive(entity))
            return;

        world.AddOrSet(entity, instance);
    }

    public static void Mark(World world, EntityId entity, InstanceDirtyFlags flags)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (flags == InstanceDirtyFlags.None
            || entity == EntityId.Null
            || !world.IsAlive(entity))
        {
            return;
        }

        if (world.Has<Removed<InstanceDirty>>(entity))
            world.Remove<Removed<InstanceDirty>>(entity);

        if (world.Has<InstanceDirty>(entity))
        {
            if (!world.IsEnabled<InstanceDirty>(entity))
                world.Enable<InstanceDirty>(entity);

            ref InstanceDirty dirty = ref world.Get<InstanceDirty>(entity);
            dirty.Flags |= flags;
        }
        else
        {
            world.Add(entity, new InstanceDirty { Flags = flags });
        }
    }
}
