using Friflo.Engine.ECS;

namespace SomeEngine.Core.Jobs;

/// <summary>
/// A job that processes a chunk of entities.
/// </summary>
public interface IJobChunk<TChunk>
{
    void Execute(TChunk chunk, int chunkIndex, int firstEntityIndex);
}
