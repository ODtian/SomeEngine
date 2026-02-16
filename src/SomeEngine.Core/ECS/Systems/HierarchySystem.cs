using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using SomeEngine.Core.ECS.Components;

namespace SomeEngine.Core.ECS.Systems;

public class HierarchySystem : QuerySystem<LocalTransform>
{
    private readonly EntityStore _store;
    private readonly ArchetypeQuery<LocalTransform> _missingDepthQuery;
    private readonly ArchetypeQuery<TreeNode, LocalTransform> _treeQuery;
    private readonly ArchetypeQuery<LocalTransform> _isolatedQuery;

    public HierarchySystem(EntityStore store)
    {
        _store = store;
        _missingDepthQuery = store.Query<LocalTransform>().WithoutAllComponents(
            ComponentTypes.Get<TransformDepth>()
        );
        _treeQuery = store.Query<TreeNode, LocalTransform>();
        _isolatedQuery = store.Query<LocalTransform>().WithoutAllComponents(
            ComponentTypes.Get<TreeNode>()
        );
    }

    protected override void OnUpdate()
    {
        // 1. Ensure all LocalTransform entities have TransformDepth
        foreach (var entity in _missingDepthQuery.ToEntityList())
        {
            entity.AddComponent(new TransformDepth { Value = 0 });
        }

        // 2. Update Depths

        // A. Isolated entities (No TreeNode) -> Depth 0
        foreach (var chunk in _isolatedQuery.Chunks)
        {
            var entities = chunk.Entities;
            for (int i = 0; i < chunk.Length; i++)
            {
                var id = entities[i];
                var entity = _store.GetEntityById(id);
                if (entity.TryGetComponent<TransformDepth>(out var d) &&
                    d.Value != 0)
                {
                    ref var depthRef = ref entity.GetComponent<TransformDepth>();
                    depthRef.Value = 0;
                }
            }
        }

        // Note: Tree traversal logic for depth calculation should go here.
        // Assuming partial implementation for now based on original file.

        // B. Tree Roots (TreeNode.Parent == null)
        foreach (var chunk in _treeQuery.Chunks)
        {
            var entities = chunk.Entities;
            for (int i = 0; i < chunk.Length; i++)
            {
                var id = entities[i];
                var entity = _store.GetEntityById(id);
                if (entity.Parent.IsNull)
                {
                    SetDepthRecursive(entity, 0);
                }
            }
        }
    }

    private void SetDepthRecursive(Entity entity, int depth)
    {
        ref var d = ref entity.GetComponent<TransformDepth>();
        if (d.Value != depth)
        {
            d.Value = depth;
        }

        foreach (var child in entity.ChildEntities)
        {
            SetDepthRecursive(child, depth + 1);
        }
    }
}
