using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace SomeEngine.Core.Jobs.Internal;

internal struct JobChunkWrapper<TJob, TChunk> : IJobParallelFor 
    where TJob : struct, IJobChunk<TChunk>
{
    public TJob JobData;
    public TChunk[] Chunks; // Changed from IReadOnlyList to Array for ArrayPool usage
    public int[] StartIndices;

    public void Execute(int index)
    {
        var chunk = Chunks[index];
        int firstEntityIndex = StartIndices != null ? StartIndices[index] : 0;
        JobData.Execute(chunk, index, firstEntityIndex);
    }
}
