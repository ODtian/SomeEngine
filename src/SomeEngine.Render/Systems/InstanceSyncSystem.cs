using System;
using Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render.Data;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Systems;

public class InstanceSyncSystem(InstanceDataManager dataManager) : QuerySystem<TransformQvvs, MeshInstance>
{
    private readonly InstanceDataManager _dataManager = dataManager;

    protected override void OnUpdate()
    {
        int count = Query.Count;
        _dataManager.EnsureCapacity(count);

        int index = 0;
        foreach (var (transforms, meshInstances, _) in Query.Chunks)
        {
            var tSpan = transforms.Span;
            var mSpan = meshInstances.Span;
            for (int i = 0; i < tSpan.Length; i++)
            {
                _dataManager.SetTransform(index, GpuTransform.FromQvvs(tSpan[i]));
                _dataManager.SetHeader(index, new GpuInstanceHeader
                {
                    BVHRootIndex = mSpan[i].BVHRootIndex,
                    MaterialID = mSpan[i].MaterialID,
                    MetadataOffset = 0,
                    MetadataCount = 0,
                    DeformFlags = 0,
                    BoundsExpansion = 0f,
                    BoneMatrixOffset = 0,
                    BoneCount = 0,
                });
                index++;
            }
        }

        _dataManager.UpdateCount(count);
    }
}
