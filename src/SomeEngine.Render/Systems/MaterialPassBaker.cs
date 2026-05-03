using Friflo.Engine.ECS;
using SomeEngine.Assets;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Render.Components;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Systems;

/// <summary>
/// Expands authored mesh material binding into runtime pass entities.
/// </summary>
public sealed class MaterialPassBaker : IWorldBaker
{
    private readonly Func<AssetGuid, Material?> _materialResolver;

    public MaterialPassBaker(Func<AssetGuid, Material?> materialResolver)
    {
        ArgumentNullException.ThrowIfNull(materialResolver);
        _materialResolver = materialResolver;
    }

    public void Bake(BakeContext context)
    {
        int instanceIndex = 0;
        foreach (Entity authoringEntity in context.AuthoringStore.Entities)
        {
            if (!authoringEntity.TryGetComponent<MeshInstance>(out MeshInstance meshInstance))
            {
                continue;
            }

            int currentInstanceIndex = instanceIndex++;
            if (!authoringEntity.TryGetComponent<MeshMaterialBindings>(out MeshMaterialBindings bindings)
                || bindings.MaterialAssetGuids is not { Length: > 0 } materialGuids)
            {
                continue;
            }

            for (int localMaterialSlot = 0; localMaterialSlot < materialGuids.Length; localMaterialSlot++)
            {
                AssetGuid materialGuid = materialGuids[localMaterialSlot];
                if (materialGuid.IsEmpty)
                {
                    continue;
                }

                Material? material = _materialResolver(materialGuid);
                if (material == null || material.PassEntities.Length == 0)
                {
                    continue;
                }

                int passCount = material.PassEntities.Length;
                for (int passIndex = 0; passIndex < passCount; passIndex++)
                {
                    Entity passEntity = material.PassEntities[passIndex];
                    Entity runtimeEntity = context.CreateRuntimeEntity();
                    if (!passEntity.IsNull)
                    {
                        EntityStore.CopyEntity(passEntity, runtimeEntity);
                    }

                    runtimeEntity.AddComponent(new Pipelines.MaterialRef { Owner = material });
                    runtimeEntity.AddComponent(new RenderSourceEntity { SourceEntityId = authoringEntity.Id });
                    runtimeEntity.AddComponent(new RenderMaterialSlotBinding
                    {
                        InstanceIndex = currentInstanceIndex,
                        LocalMaterialSlot = localMaterialSlot,
                        BVHRootIndex = meshInstance.BVHRootIndex,
                        BoundsExpansion = meshInstance.BoundsExpansion,
                        MaterialAssetGuid = materialGuid,
                        PassIndex = passIndex,
                    });
                }
            }
        }
    }
}
