# Material In ECS Authoring

> **Status:** material-specific note aligned with current codebase on 2026-04-22
>
> **Position:** material authoring is one concrete use of the generic ECS authoring model

---

## 1. Read This In Order

The generic definition of authoring lives here:

- [../core/ecs_authoring.md](../core/ecs_authoring.md)

This file is only the material-specific example.

## 2. What "Material Authoring" Means

In SomeEngine, material authoring is not a separate top-level subsystem.

It is the material-shaped use of the engine-wide ECS authoring rule:

```text
Authoring entity data + authored asset/resource
    -> extractor
    -> runtime ECS data
```

For materials, that becomes:

```text
MeshMaterialBindings + MaterialAsset
    -> RenderWorldExtractor
    -> RenderWorld pass entities
```

## 3. Current Material Mapping

### Authoring Side

The source entity keeps stable authoring data:

- `MeshInstance`
- `MeshMaterialBindings`

`MeshMaterialBindings` is the important piece here. It is not runtime submission state. It is the stable mesh-local material table authored on the ECS entity.

Current type:

- [src/SomeEngine.Render/Components/MeshMaterialBindingsComponent.cs](../../src/SomeEngine.Render/Components/MeshMaterialBindingsComponent.cs)

### Authored Resource

`MaterialAsset` is the authored resource referenced by authoring ECS data.

It stores:

- root params
- pass snapshots
- shader references
- explicit serialized pass payload

Current schema:

- [assets/Schema/material_asset.fbs](../../assets/Schema/material_asset.fbs)

### Runtime Material Template

When loaded, `MaterialAsset` becomes a runtime `Material`:

- shared params live on `Material.Params`
- pass templates live on `Material.PassEntities[]`

Current files:

- [src/SomeEngine.Render/Assets/MaterialAssetLoader.cs](../../src/SomeEngine.Render/Assets/MaterialAssetLoader.cs)
- [src/SomeEngine.Render/Materials/Material.cs](../../src/SomeEngine.Render/Materials/Material.cs)

### Runtime Execution Entity

At render extract time, the source authoring entity is expanded into RenderWorld runtime entities.

Current runtime metadata:

- `RenderSourceEntity`
- `RenderMaterials`

Current files:

- [src/SomeEngine.Render/Components/RenderWorldComponents.cs](../../src/SomeEngine.Render/Components/RenderWorldComponents.cs)
- [src/SomeEngine.Render/Systems/RenderWorldExtractor.cs](../../src/SomeEngine.Render/Systems/RenderWorldExtractor.cs)

## 4. The Only Material-Specific Rule That Matters

One local material slot may expand to multiple runtime render entities, because one `MaterialAsset` may contain multiple pass templates.

That is why:

- authoring data stays on the source ECS entity
- pass execution data is mounted on extracted runtime entities

and not directly on the source entity.

## 5. ClusterRender Is Only The Current Consumer

Today the concrete consumer is `ClusterRender`.

That means current runtime pass entities happen to contain cluster-facing state such as:

- `ClusterRaster`
- `ClusterShadeComponent`
- `ClusterDeform`
- `StencilState`

But this is only the current render specialization.

It must not be mistaken for the definition of authoring itself.

## 6. Decision Summary

For materials, the ECS authoring pattern is:

```text
Source ECS entity stores stable material bindings.
MaterialAsset stores authored material data.
Loaded Material stores runtime pass templates.
Extractor expands templates into runtime render entities.
```

That is a material example of generic ECS authoring, not a separate authoring architecture.
