using System;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Jobs;
using SomeEngine.Render.Data;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Systems;

public class InstanceSyncSystem(InstanceDataManager dataManager, SystemContext? systemContext = null)
    : QuerySystem<WorldTransform, MeshInstance>
{
    private readonly InstanceDataManager _dataManager = dataManager;
    private readonly SystemContext? _systemContext = systemContext;

    protected override void OnUpdate()
    {
        if (_systemContext != null)
        {
            _systemContext.GlobalDependency.Complete();
            _systemContext.GlobalDependency = default;
        }

        int count = Query.Count;
        _dataManager.EnsureCapacity(count);
        _dataManager.ClearMetadata();

        int index = 0;
        foreach (var entity in Query.Entities)
        {
            var t = entity.GetComponent<WorldTransform>();
            var m = entity.GetComponent<MeshInstance>();

            uint instanceDataOffset = 0;
            var instanceDataFlags = GpuInstanceDataFlags.None;

            if (entity.TryGetComponent<MaterialOverride>(out var overrideData))
            {
                instanceDataOffset = _dataManager.AppendMetadata(ref overrideData);
                instanceDataFlags |= GpuInstanceDataFlags.MaterialOverride;
            }

            _dataManager.SetTransform(index, GpuTransform.FromQvvs(t.Qvvs));
            float boundsExpansion = MathF.Max(0f, m.BoundsExpansion);

            var writer = _dataManager.GetHeaderWriter(index);
            writer.Clear();
            writer.SetBvhRootIndex(m.BVHRootIndex);
            writer.SetMaterialSlotOffset(0);
            writer.SetInstanceDataOffset(instanceDataOffset);
            writer.SetInstanceDataFlags(instanceDataFlags);
            writer.SetBoundsExpansionWorld(boundsExpansion);
            index++;
        }

        _dataManager.UpdateCount(count);
    }
}
