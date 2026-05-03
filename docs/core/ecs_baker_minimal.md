# ECS Authoring / Baker Minimal Design

> Goal: let material assets declare render features as ECS pass entities, then bake those declarations onto runtime entities without adding a deep new framework.

## Core Model

Keep only four concepts:

1. `Authoring Entity`
2. `Material`
3. `WorldBaker`
4. `RenderWorld Query`

That means:

```text
GameWorld authoring entity
    + MeshInstance
    + MeshMaterialBindings
    + asset references / user data
        |
        v
Material asset
    -> load once into Material
    -> Material owns PassEntities[]
    -> each pass entity carries tags + components that declare pipeline features
        |
        v
MaterialPassBaker
    -> for each source entity
    -> for each local material slot
    -> for each material pass
    -> create one runtime entity
    -> copy pass feature declaration onto that runtime entity
        |
        v
RenderWorld / runtime store
    -> pipeline queries entities by tags/components
```

This matches the Bevy-style direction: the pipeline should not ask a material object what to do every frame. It should query the runtime ECS world for the entities that currently have a feature.

## Why This Shape

The important split is:

- authoring entity stores stable scene intent
- material stores stable pass templates
- baker expands templates into runtime execution entities
- pipeline only consumes runtime execution entities

This keeps material authoring ECS-native while also keeping runtime query-friendly.

## Minimal API

The first landing only needs:

```csharp
public sealed class BakeContext
{
    public EntityStore AuthoringStore { get; }
    public EntityStore RuntimeStore { get; }
    public Entity CreateRuntimeEntity();
}

public interface IWorldBaker
{
    void Bake(BakeContext context);
}

public sealed class WorldBaker
{
    public void Rebuild(EntityStore authoringStore, EntityStore runtimeStore);
}
```

And one concrete baker:

```csharp
public sealed class MaterialPassBaker : IWorldBaker
{
    public void Bake(BakeContext context);
}
```

No registry graph, no bake asset graph, no authoring base class tree.

## Material Rule

`Material` is the pass template owner.

Each `Material.PassEntities[i]` is a feature declaration entity:

- tags express coarse pipeline routing such as `Opaque` / `Masked`
- components express feature payload such as shade, raster, stencil, overlay

The baker does not reinterpret those passes into another intermediate representation.
It mounts them directly onto runtime entities.

## Runtime Rule

The runtime entity is where source-specific state and material-pass feature state meet:

- source-specific metadata
  - source entity id
  - mesh root / slot binding
- material-pass feature metadata
  - tags
  - feature components
  - material ref

So the runtime entity becomes the only thing the render pipeline needs to query.

## Scope Boundary

The first version should do a full rebuild of the runtime store.

That is intentional:

- simpler mental model
- fewer hidden caches
- easier to debug
- enough to validate authoring/data shape first

If profiling later proves rebuild cost matters, incremental reuse can be added behind the same `WorldBaker` entry point.

## One Important Limitation

The current pass copy path still clones a known set of render feature components.

That is acceptable for the first landing because the design question here is not "how many generic reflection layers do we build", but "where does authored feature data live, and what does runtime query consume".

If future material passes need arbitrary ECS component cloning, extend the pass copier itself.
Do not solve that by inserting more authoring/baker layers.

## Decision Summary

The minimal system is:

```text
Authoring ECS data picks materials.
Material owns pass ECS entities.
Baker expands pass ECS entities onto runtime entities.
Pipeline queries runtime entities by feature.
```

That is enough structure to support render/material authoring while keeping the hierarchy flat.
