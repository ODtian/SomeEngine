using System.Collections.Generic;
using Friflo.Engine.ECS;
using SomeEngine.Assets;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Render.Components;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Systems;

public sealed class RenderWorldExtractor(RenderWorld renderWorld)
{
    private readonly RenderWorld _renderWorld = renderWorld;
    private ulong _lastFrameHash;
    private int _lastSourceCount = -1;
    private bool _hasFrameHash;
    private uint _version;

    public uint Version => _version;

    public void Rebuild(EntityStore sourceStore, Func<AssetGuid, Material?> materialResolver)
    {
        ulong frameHash = ComputeFrameHash(sourceStore, materialResolver, out int sourceCount);
        if (_hasFrameHash && _lastSourceCount == sourceCount && _lastFrameHash == frameHash)
        {
            return;
        }

        _lastFrameHash = frameHash;
        _lastSourceCount = sourceCount;
        _hasFrameHash = true;

        WorldBaker baker = new(new MaterialPassBaker(materialResolver));
        baker.Rebuild(sourceStore, _renderWorld.Store);
        _version++;
    }

    private static ulong ComputeFrameHash(
        EntityStore sourceStore,
        Func<AssetGuid, Material?> materialResolver,
        out int sourceCount)
    {
        ulong hash = 14695981039346656037UL;
        sourceCount = 0;

        foreach (Entity sourceEntity in sourceStore.Entities)
        {
            if (!sourceEntity.TryGetComponent<MeshInstance>(out MeshInstance meshInstance))
            {
                continue;
            }

            sourceCount++;
            hash = HashValue(hash, sourceEntity.Id);
            hash = HashValue(hash, meshInstance.BVHRootIndex);
            hash = HashValue(hash, meshInstance.BoundsExpansion);

            if (!sourceEntity.TryGetComponent<MeshMaterialBindings>(out MeshMaterialBindings bindings)
                || bindings.MaterialAssetGuids == null)
            {
                hash = HashValue(hash, 0);
                continue;
            }

            AssetGuid[] materialGuids = bindings.MaterialAssetGuids;
            hash = HashValue(hash, materialGuids.Length);
            for (int localMaterialSlot = 0; localMaterialSlot < materialGuids.Length; localMaterialSlot++)
            {
                AssetGuid materialGuid = materialGuids[localMaterialSlot];
                hash = HashValue(hash, localMaterialSlot);
                hash = HashValue(hash, materialGuid);

                if (materialGuid.IsEmpty)
                {
                    hash = HashValue(hash, 0);
                    continue;
                }

                Material? material = materialResolver(materialGuid);
                int passCount = material?.PassEntities.Length ?? 0;
                hash = HashValue(hash, passCount);
                if (material == null)
                {
                    continue;
                }

                hash = HashValue(hash, material.Version);
                for (int passIndex = 0; passIndex < passCount; passIndex++)
                {
                    Entity passEntity = material.PassEntities[passIndex];
                    hash = HashValue(hash, passEntity.Id);
                }
            }
        }

        return hash;
    }

    private static ulong HashValue<T>(ulong hash, T value)
    {
        int valueHash = value is null ? 0 : EqualityComparer<T>.Default.GetHashCode(value);
        hash ^= unchecked((ulong)valueHash);
        hash *= 1099511628211UL;
        return hash;
    }
}
