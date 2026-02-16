using System.Buffers;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using SomeEngine.Core.Jobs.Internal;

namespace SomeEngine.Core.Jobs;

public static class JobChunkExtensions
{
    public static JobHandle Schedule<TJob, T>(this TJob jobData, ArchetypeQuery<T> query, JobHandle dependency = default) 
        where TJob : struct, IJobChunk<Chunks<T>>
        where T : struct
    {
        var queryChunks = query.Chunks;
        
        // Optimization: Use a resizable approach to avoid double iteration
        // Start with a reasonable guess or capacity
        int capacity = 128; 
        var chunksArray = ArrayPool<Chunks<T>>.Shared.Rent(capacity);
        var indexArray = ArrayPool<int>.Shared.Rent(capacity);

        int count = 0;
        int current = 0;

        foreach (var chunks in queryChunks)
        {
            if (count >= capacity)
            {
                // Grow
                int newCapacity = capacity * 2;
                var newChunks = ArrayPool<Chunks<T>>.Shared.Rent(newCapacity);
                var newIndices = ArrayPool<int>.Shared.Rent(newCapacity);
                
                Array.Copy(chunksArray, newChunks, count);
                Array.Copy(indexArray, newIndices, count);
                
                ArrayPool<Chunks<T>>.Shared.Return(chunksArray);
                ArrayPool<int>.Shared.Return(indexArray);
                
                chunksArray = newChunks;
                indexArray = newIndices;
                capacity = newCapacity;
            }

            chunksArray[count] = chunks;
            indexArray[count] = current;
            current += chunks.Chunk1.Length;
            count++;
        }

        if (count == 0)
        {
            // Nothing found, return arrays immediately
            ArrayPool<Chunks<T>>.Shared.Return(chunksArray);
            ArrayPool<int>.Shared.Return(indexArray);
            return dependency;
        }

        // ... Dispatch using 'count' as length ...


        // 4. Create Worker Job
        var wrapper = new JobChunkWrapper<TJob, Chunks<T>>
        {
            JobData = jobData,
            Chunks = chunksArray, 
            StartIndices = indexArray
        };

        JobHandle workerHandle = JobSystem.Dispatch(wrapper, count, count, dependency);

        // 5. Create Dispose Job to return arrays to pool
        var disposeJob = new ChunkDisposeJob<Chunks<T>>
        {
            ChunksArray = chunksArray,
            IndicesArray = indexArray
        };

        // 6. Schedule Dispose Job after Worker
        return JobSystem.Schedule(disposeJob, workerHandle);
    }

    public static JobHandle ScheduleParallel<TJob, T>(this TJob jobData, ArchetypeQuery<T> query, int chunksPerBatch = 1, JobHandle dependency = default) 
        where TJob : struct, IJobChunk<Chunks<T>>
        where T : struct
    {
        var queryChunks = query.Chunks;
        
        int capacity = 128;
        var chunksArray = ArrayPool<Chunks<T>>.Shared.Rent(capacity);
        var indexArray = ArrayPool<int>.Shared.Rent(capacity);

        int count = 0;
        int current = 0;
        
        foreach (var chunks in queryChunks)
        {
             if (count >= capacity)
            {
                int newCapacity = capacity * 2;
                var newChunks = ArrayPool<Chunks<T>>.Shared.Rent(newCapacity);
                var newIndices = ArrayPool<int>.Shared.Rent(newCapacity);
                
                Array.Copy(chunksArray, newChunks, count);
                Array.Copy(indexArray, newIndices, count);
                
                ArrayPool<Chunks<T>>.Shared.Return(chunksArray);
                ArrayPool<int>.Shared.Return(indexArray);
                
                chunksArray = newChunks;
                indexArray = newIndices;
                capacity = newCapacity;
            }

            chunksArray[count] = chunks;
            indexArray[count] = current;
            current += chunks.Chunk1.Length;
            count++;
        }
        
        if (count == 0)
        {
            ArrayPool<Chunks<T>>.Shared.Return(chunksArray);
            ArrayPool<int>.Shared.Return(indexArray);
            return dependency;
        }

        var wrapper = new JobChunkWrapper<TJob, Chunks<T>>
        {
            JobData = jobData,
            Chunks = chunksArray,
            StartIndices = indexArray
        };

        JobHandle workerHandle = JobSystem.Dispatch(wrapper, count, chunksPerBatch, dependency);

        var disposeJob = new ChunkDisposeJob<Chunks<T>>
        {
            ChunksArray = chunksArray,
            IndicesArray = indexArray
        };

        return JobSystem.Schedule(disposeJob, workerHandle);
    }

    // --- 2 Components ---
    public static JobHandle Schedule<TJob, T1, T2>(this TJob jobData, ArchetypeQuery<T1, T2> query, JobHandle dependency = default) 
        where TJob : struct, IJobChunk<Chunks<T1, T2>>
        where T1 : struct where T2 : struct
    {
        return ScheduleParallel<TJob, T1, T2>(jobData, query, 1, dependency);
    }

    public static JobHandle ScheduleParallel<TJob, T1, T2>(this TJob jobData, ArchetypeQuery<T1, T2> query, int chunksPerBatch = 1, JobHandle dependency = default) 
        where TJob : struct, IJobChunk<Chunks<T1, T2>>
        where T1 : struct where T2 : struct
    {
        var queryChunks = query.Chunks;
        int capacity = 128;
        var chunksArray = ArrayPool<Chunks<T1, T2>>.Shared.Rent(capacity);
        var indexArray = ArrayPool<int>.Shared.Rent(capacity);
        int count = 0;
        int current = 0;

        foreach (var chunks in queryChunks) {
            if (count >= capacity) {
                int newCapacity = capacity * 2;
                var newChunks = ArrayPool<Chunks<T1, T2>>.Shared.Rent(newCapacity);
                var newIndices = ArrayPool<int>.Shared.Rent(newCapacity);
                Array.Copy(chunksArray, newChunks, count);
                Array.Copy(indexArray, newIndices, count);
                ArrayPool<Chunks<T1, T2>>.Shared.Return(chunksArray);
                ArrayPool<int>.Shared.Return(indexArray);
                chunksArray = newChunks;
                indexArray = newIndices;
                capacity = newCapacity;
            }
            chunksArray[count] = chunks;
            indexArray[count] = current;
            current += chunks.Chunk1.Length;
            count++;
        }

        if (count == 0) {
            ArrayPool<Chunks<T1, T2>>.Shared.Return(chunksArray);
            ArrayPool<int>.Shared.Return(indexArray);
            return dependency;
        }

        var wrapper = new JobChunkWrapper<TJob, Chunks<T1, T2>> { JobData = jobData, Chunks = chunksArray, StartIndices = indexArray };
        JobHandle workerHandle = JobSystem.Dispatch(wrapper, count, chunksPerBatch, dependency);
        var disposeJob = new ChunkDisposeJob<Chunks<T1, T2>> { ChunksArray = chunksArray, IndicesArray = indexArray };
        return JobSystem.Schedule(disposeJob, workerHandle);
    }

    // --- 3 Components ---
    public static JobHandle Schedule<TJob, T1, T2, T3>(this TJob jobData, ArchetypeQuery<T1, T2, T3> query, JobHandle dependency = default) 
        where TJob : struct, IJobChunk<Chunks<T1, T2, T3>>
        where T1 : struct where T2 : struct where T3 : struct
    {
        return ScheduleParallel<TJob, T1, T2, T3>(jobData, query, 1, dependency);
    }

    public static JobHandle ScheduleParallel<TJob, T1, T2, T3>(this TJob jobData, ArchetypeQuery<T1, T2, T3> query, int chunksPerBatch = 1, JobHandle dependency = default) 
        where TJob : struct, IJobChunk<Chunks<T1, T2, T3>>
        where T1 : struct where T2 : struct where T3 : struct
    {
        var queryChunks = query.Chunks;
        int capacity = 128;
        var chunksArray = ArrayPool<Chunks<T1, T2, T3>>.Shared.Rent(capacity);
        var indexArray = ArrayPool<int>.Shared.Rent(capacity);
        int count = 0;
        int current = 0;

        foreach (var chunks in queryChunks) {
            if (count >= capacity) {
                int newCapacity = capacity * 2;
                var newChunks = ArrayPool<Chunks<T1, T2, T3>>.Shared.Rent(newCapacity);
                var newIndices = ArrayPool<int>.Shared.Rent(newCapacity);
                Array.Copy(chunksArray, newChunks, count);
                Array.Copy(indexArray, newIndices, count);
                ArrayPool<Chunks<T1, T2, T3>>.Shared.Return(chunksArray);
                ArrayPool<int>.Shared.Return(indexArray);
                chunksArray = newChunks;
                indexArray = newIndices;
                capacity = newCapacity;
            }
            chunksArray[count] = chunks;
            indexArray[count] = current;
            current += chunks.Chunk1.Length;
            count++;
        }

        if (count == 0) {
            ArrayPool<Chunks<T1, T2, T3>>.Shared.Return(chunksArray);
            ArrayPool<int>.Shared.Return(indexArray);
            return dependency;
        }

        var wrapper = new JobChunkWrapper<TJob, Chunks<T1, T2, T3>> { JobData = jobData, Chunks = chunksArray, StartIndices = indexArray };
        JobHandle workerHandle = JobSystem.Dispatch(wrapper, count, chunksPerBatch, dependency);
        var disposeJob = new ChunkDisposeJob<Chunks<T1, T2, T3>> { ChunksArray = chunksArray, IndicesArray = indexArray };
        return JobSystem.Schedule(disposeJob, workerHandle);
    }
    
    // --- 4 Components ---
    public static JobHandle Schedule<TJob, T1, T2, T3, T4>(this TJob jobData, ArchetypeQuery<T1, T2, T3, T4> query, JobHandle dependency = default) 
        where TJob : struct, IJobChunk<Chunks<T1, T2, T3, T4>>
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct
    {
        return ScheduleParallel<TJob, T1, T2, T3, T4>(jobData, query, 1, dependency);
    }

    public static JobHandle ScheduleParallel<TJob, T1, T2, T3, T4>(this TJob jobData, ArchetypeQuery<T1, T2, T3, T4> query, int chunksPerBatch = 1, JobHandle dependency = default) 
        where TJob : struct, IJobChunk<Chunks<T1, T2, T3, T4>>
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct
    {
        var queryChunks = query.Chunks;
        int capacity = 128;
        var chunksArray = ArrayPool<Chunks<T1, T2, T3, T4>>.Shared.Rent(capacity);
        var indexArray = ArrayPool<int>.Shared.Rent(capacity);
        int count = 0;
        int current = 0;

        foreach (var chunks in queryChunks) {
            if (count >= capacity) {
                int newCapacity = capacity * 2;
                var newChunks = ArrayPool<Chunks<T1, T2, T3, T4>>.Shared.Rent(newCapacity);
                var newIndices = ArrayPool<int>.Shared.Rent(newCapacity);
                Array.Copy(chunksArray, newChunks, count);
                Array.Copy(indexArray, newIndices, count);
                ArrayPool<Chunks<T1, T2, T3, T4>>.Shared.Return(chunksArray);
                ArrayPool<int>.Shared.Return(indexArray);
                chunksArray = newChunks;
                indexArray = newIndices;
                capacity = newCapacity;
            }
            chunksArray[count] = chunks;
            indexArray[count] = current;
            current += chunks.Chunk1.Length;
            count++;
        }

        if (count == 0) {
            ArrayPool<Chunks<T1, T2, T3, T4>>.Shared.Return(chunksArray);
            ArrayPool<int>.Shared.Return(indexArray);
            return dependency;
        }

        var wrapper = new JobChunkWrapper<TJob, Chunks<T1, T2, T3, T4>> { JobData = jobData, Chunks = chunksArray, StartIndices = indexArray };
        JobHandle workerHandle = JobSystem.Dispatch(wrapper, count, chunksPerBatch, dependency);
        var disposeJob = new ChunkDisposeJob<Chunks<T1, T2, T3, T4>> { ChunksArray = chunksArray, IndicesArray = indexArray };
        return JobSystem.Schedule(disposeJob, workerHandle);
    }
}



