using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Jobs;
using SomeEngine.Core.Math;

namespace SomeEngine.Core.ECS.Systems;

public class TransformSystem : QuerySystem<LocalTransform, WorldTransform, TransformDepth>
{
    private readonly SystemContext _context;
    private const int MaxDepthLimit = 50;

    public TransformSystem(SystemContext context)
    {
        _context = context;
    }

    protected override void OnUpdate()
    {
        var dependency = _context.GlobalDependency;
        
        for (int d = 0; d < MaxDepthLimit; d++)
        {
            var job = new UpdateJob { TargetDepth = d };
            dependency = job.ScheduleParallel(Query, 1, dependency);
        }

        _context.GlobalDependency = dependency;
    }

    struct UpdateJob : IJobChunk<Chunks<LocalTransform, WorldTransform, TransformDepth>>
    {
        public int TargetDepth;

        public void Execute(Chunks<LocalTransform, WorldTransform, TransformDepth> chunks, int chunkIndex, int firstEntityIndex)
        {
            var locals = chunks.Chunk1;
            var worlds = chunks.Chunk2;
            var depths = chunks.Chunk3;
            int count = locals.Length;

            for (int i = 0; i < count; i++)
            {
                if (depths[i].Value != TargetDepth) continue;

                var local = locals[i].Value;

                if (TargetDepth == 0)
                {
                    worlds[i].Qvvs = local;
                    worlds[i].Matrix = local.ToMatrix();
                    continue;
                }

                // For Depth > 0, we need Parent WorldTransform
                var entityId = chunks.Entities[i];
                var store = (EntityStore)chunks.Entities.Archetype.Store;
                var entity = store.GetEntityById(entityId);
                var parentEntity = entity.Parent;

                if (parentEntity.TryGetComponent<WorldTransform>(out var parentWorld))
                {
                    var world = TransformQvvs.Combine(parentWorld.Qvvs, local);
                    worlds[i].Qvvs = world;
                    worlds[i].Matrix = world.ToMatrix();
                }
                else
                {
                    // Fallback to local if parent not found or missing WorldTransform
                    worlds[i].Qvvs = local;
                    worlds[i].Matrix = local.ToMatrix();
                }
            }
        }
    }
}
