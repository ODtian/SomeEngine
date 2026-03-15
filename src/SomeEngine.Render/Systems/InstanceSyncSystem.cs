using System;
using Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render.Data;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Systems;

public class InstanceSyncSystem(InstanceDataManager dataManager)
    : QuerySystem<TransformQvvs, MeshInstance>
{
    private readonly InstanceDataManager _dataManager = dataManager;

    protected override void OnUpdate()
    {
        int count = Query.Count;
        _dataManager.EnsureCapacity(count);
        _dataManager.ClearMetadata();

        int index = 0;
        foreach (var entity in Query.Entities)
        {
            var t = entity.GetComponent<TransformQvvs>();
            var m = entity.GetComponent<MeshInstance>();

            uint metaOffset = 0;
            uint metaCount = 0;

            if (entity.TryGetComponent<MaterialOverride>(out var overrideData))
            {
                metaOffset = _dataManager.AppendMetadata(ref overrideData);
                metaCount = (uint)(
                    System.Runtime.CompilerServices.Unsafe.SizeOf<MaterialOverride>() / 4
                );
            }

            _dataManager.SetTransform(index, GpuTransform.FromQvvs(t));
            _dataManager.SetHeader(
                index,
                new GpuInstanceHeader
                {
                    BVHRootIndex = m.BVHRootIndex,
                    MaterialID = m.MaterialID,
                    MetadataOffset = metaOffset,
                    MetadataCount = metaCount,
                    RasterBinKey = 0,
                    BoundsExpansion = 0f,
                    Pad2 = 0,
                    Pad3 = 0,
                }
            );
            index++;
        }

        _dataManager.UpdateCount(count);
    }
}
