using Friflo.Engine.ECS;

namespace SomeEngine.Render.Materials;

public readonly record struct MaterialSlotBinding(Entity RepresentativeEntity, Entity[] FieldEntities);
