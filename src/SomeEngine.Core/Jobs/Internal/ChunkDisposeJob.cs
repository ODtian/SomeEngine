using System.Buffers;
using SomeEngine.Core.Jobs;

namespace SomeEngine.Core.Jobs.Internal;

internal struct ChunkDisposeJob<T> : IJob
{
    public T[] ChunksArray;
    public int[] IndicesArray;

    public void Execute()
    {
        if (ChunksArray != null)
        {
            ArrayPool<T>.Shared.Return(ChunksArray);
            ChunksArray = null!;
        }
        
        if (IndicesArray != null)
        {
            ArrayPool<int>.Shared.Return(IndicesArray);
            IndicesArray = null!;
        }
    }
}
