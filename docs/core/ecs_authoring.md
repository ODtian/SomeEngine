# ECS Authoring

> **Status:** design aligned with current codebase on 2026-04-22
>
> **Goal:** define authoring as a generic ECS capability, not as a material-only or render-only subsystem

---

## 1. Core Statement

Authoring is part of the ECS model of the whole engine.

It is not:

- a material-specific system
- a render-specific system
- a special layer owned by `ClusterRender`

It is:

- the editable, persistable, stable form of ECS data
- the source from which runtime ECS data is derived

Materials are only one example.
Physics, animation, audio, gameplay, AI, and scene/prefab systems should all be able to use the same authoring model.

## 2. Keep It Simple

The model only needs three roles.

### 2.1 Authoring Entity

The authoring entity lives in the main ECS world and carries stable data:

- asset references
- user-authored values
- scene/prefab state
- stable local tables or bindings

This is the data we want to edit, serialize, diff, and reload.

### 2.2 Runtime Entity

The runtime entity carries executable data:

- pipeline components
- physics runtime state
- animation runtime state
- caches
- transient bindings
- frame-derived state

This is the data systems actually consume.

### 2.3 Extractor

The extractor converts authoring entities into runtime entities.

Its job is simple:

1. read authoring components
2. resolve referenced assets/resources
3. generate or update runtime entities/components

That is the whole pattern.

No extra abstract layering is needed at the top level.

## 3. Generic Rules

These rules should apply engine-wide.

1. Authoring data stays in ECS.
2. Runtime data stays in ECS.
3. Authoring data is stable and persistable.
4. Runtime data is derived and replaceable.
5. Systems should consume runtime data, not authoring data, unless they are authoring/editor systems.
6. Assets are inputs to authoring/runtime conversion, not the definition of authoring itself.
7. Every subsystem may define its own extractor, but all extractors follow the same ECS pattern.

## 4. What Makes A Component "Authoring"

A component is an authoring component if it primarily exists to express user intent and stable references.

Typical signs:

- it references an asset GUID or prefab GUID
- it stores editable parameters
- it should round-trip through scene/prefab serialization
- it should survive runtime rebuilds

A component is a runtime component if it primarily exists to drive execution.

Typical signs:

- it contains resolved handles, runtime templates, caches, or derived metadata
- it can be rebuilt from authoring data
- it is specific to a system's execution path

This is a role distinction, not necessarily a type hierarchy requirement.

We do not need a deep inheritance tree to model this.

## 5. Minimal Generic Contract

The engine only needs a small generic contract around extraction.

For example:

```csharp
public interface IEntityExtractor
{
    bool Matches(Entity sourceEntity);
    void Extract(Entity sourceEntity, ExtractContext context);
}
```

Where `ExtractContext` provides:

- target runtime world/store
- asset/resource handles or loader callbacks
- entity mapping helpers
- scratch buffers or command APIs if needed

The important point is not the exact interface shape.
The important point is that extraction is generic and ECS-wide.

## 6. Material Example

The current material path should be understood as one specialization of ECS authoring.

In the current codebase:

- authoring entity data:
  - `MeshInstance`
  - `MeshMaterialBindings`
- authoring resource:
  - `MaterialAsset`
- runtime template:
  - `Material`
  - `MaterialPass[]`
- runtime entity:
  - RenderWorld instance state
- extractor:
  - `RenderWorldExtractor`

So the pattern is:

```text
Authoring Entity + Authoring Resource
    -> Extractor
    -> Runtime Entity
```

not:

```text
Material system
    -> owns authoring for the whole engine
```

## 7. Current Mapping In SomeEngine

Today this maps to:

- `GameWorld` as the main ECS world containing authoring-facing data
- `RenderWorld` as one runtime execution world
- material extraction as one concrete extractor path

This is already the right direction.

What needs to stay true is:

- render authoring is not the definition of authoring
- material authoring is not the definition of authoring
- cluster-specific components are not the definition of authoring

They are only concrete consumers of the generic ECS authoring model.

## 8. Design Consequences

If we accept authoring as a whole-ECS concept, then:

1. New subsystems should add authoring components, not invent parallel data models.
2. Extraction should be subsystem-specific, but the pattern should stay the same.
3. Scene/prefab persistence should store authoring components, not runtime execution state.
4. Runtime worlds may be multiple.
   Examples: render world, physics world, animation runtime world.
5. Assets should feed authoring/runtime conversion, but authoring remains ECS-native.

## 9. Material-Specific Note

Materials still need their own detailed document because they have extra concerns:

- pass templates
- shader metadata
- RenderWorld pass expansion

But that document should be treated as an example of ECS authoring, not the root definition.

See:

- [ecs_design.md](ecs_design.md)
- [../materials/authoring_system.md](../materials/authoring_system.md)
- [../materials/architecture.md](../materials/architecture.md)

## 10. Decision Summary

The engine should use one generic authoring idea:

```text
Authoring is stable ECS data.
Runtime is derived ECS data.
Extractors convert one into the other.
```

Materials are one instance of that rule, not a separate top-level authoring model.
