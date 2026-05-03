using System;
using System.Threading;
using SomeEngine.Core.Jobs.Internal;

namespace SomeEngine.Core.Jobs;

public static class JobSystem
{
    private static readonly Thread[] _workers;
    private static readonly CancellationTokenSource _cts = new();
    private static readonly ushort _combineJobTypeId;

    static JobSystem()
    {
        // Register a dummy executor for CombineDependencies (no-op, no-free)
        _combineJobTypeId = JobRegistry.Register(_ => { });

        // One worker per core minus 1 (for main thread/OS)
        int threadCount = System.Math.Max(1, Environment.ProcessorCount - 1);
        _workers = new Thread[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            _workers[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"JobWorker_{i}",
                Priority = ThreadPriority.AboveNormal,
            };
            _workers[i].Start();
        }
    }

    public static JobHandle CombineDependencies(JobHandle h1, JobHandle h2)
    {
        // Allocate Counter
        int counterId = JobPools.AllocCounter(2, out int version);

        // Use the dummy combine job type with index 0
        var jobId = new JobId(_combineJobTypeId, 0, counterId);

        ScheduleInternal(jobId, h1);
        ScheduleInternal(jobId, h2);

        return new JobHandle(counterId, version);
    }

    public static JobHandle CombineDependencies(ReadOnlySpan<JobHandle> handles)
    {
        if (handles.Length == 0)
            return default;

        int counterId = JobPools.AllocCounter(handles.Length, out int version);

        var jobId = new JobId(_combineJobTypeId, 0, counterId);

        foreach (var h in handles)
        {
            ScheduleInternal(jobId, h);
        }

        return new JobHandle(counterId, version);
    }

    public static JobHandle Schedule<T>(T job, JobHandle dependency = default)
        where T : struct, IJob
    {
        // 1. Allocate and Init Counter
        int counterId = JobPools.AllocCounter(1, out int version);

        // 2. Store Job Data
        int jobIndex = JobDataStore<T>.Add(job);

        // 3. Create JobId
        var myJobId = new JobId(JobDataStore<T>.TypeId, jobIndex, counterId);

        // 4. Handle Dependency
        ScheduleInternal(myJobId, dependency);

        return new JobHandle(counterId, version);
    }

    struct ParallelJobWrapper<T> : IJob
        where T : struct, IJobParallelFor
    {
        public T JobData;
        public int Start;
        public int End;

        public void Execute()
        {
            for (int i = Start; i < End; i++)
            {
                JobData.Execute(i);
            }
        }
    }

    public static JobHandle Dispatch<T>(
        T jobData,
        int length,
        int batchSize,
        JobHandle dependency = default
    )
        where T : struct, IJobParallelFor
    {
        if (length <= 0)
            return dependency;
        if (batchSize <= 0)
            batchSize = 1;

        int batchCount = (length + batchSize - 1) / batchSize;

        // 1. Allocate Counter
        int counterId = JobPools.AllocCounter(batchCount, out int version); // Wait for ALL batches

        // 2. Schedule Batches
        for (int i = 0; i < batchCount; i++)
        {
            int start = i * batchSize;
            int end = System.Math.Min(start + batchSize, length);

            var wrapper = new ParallelJobWrapper<T>
            {
                JobData = jobData,
                Start = start,
                End = end,
            };

            int jobIndex = JobDataStore<ParallelJobWrapper<T>>.Add(wrapper);
            var jobId = new JobId(JobDataStore<ParallelJobWrapper<T>>.TypeId, jobIndex, counterId);

            ScheduleInternal(jobId, dependency);
        }

        return new JobHandle(counterId, version);
    }

    private static void ScheduleInternal(JobId jobId, JobHandle dependency)
    {
        if (dependency.Version == 0)
        {
            GlobalJobQueue.Enqueue(jobId);
        }
        else
        {
            if (IsJobCompleted(dependency))
            {
                GlobalJobQueue.Enqueue(jobId);
            }
            else
            {
                if (!TryAddDependency(dependency.CounterId, dependency.Version, jobId))
                {
                    GlobalJobQueue.Enqueue(jobId);
                }
            }
        }
    }

    internal static bool IsJobCompleted(JobHandle handle)
    {
        if (handle.Version == 0)
            return true;

        ref var counter = ref JobPools.Counters[handle.CounterId];
        if (Volatile.Read(ref counter.Version) != handle.Version)
            return true;

        if (Volatile.Read(ref counter.Allocated) == 0)
            return true;

        return Volatile.Read(ref counter.FirstDependent) == -1;
    }

    private static bool TryAddDependency(int counterId, int expectedVersion, JobId dependentJob)
    {
        return JobPools.TryAddDependent(counterId, expectedVersion, dependentJob);
    }

    private static void WorkerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            GlobalJobQueue.WaitForJob();

            while (GlobalJobQueue.TryDequeue(out var job))
            {
                ExecuteJob(job);
            }
        }
    }

    private static void ExecuteJob(JobId job)
    {
        var executor = JobRegistry.GetExecutor(job.TypeId);
        if (executor != null)
        {
            try
            {
                executor(job.Index);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Job Execution Error: {ex}");
            }
        }

        CompleteJob(job.CounterId);
    }

    private static void CompleteJob(int counterId)
    {
        ref var counter = ref JobPools.Counters[counterId];
        int version = Volatile.Read(ref counter.Version);

        if (Interlocked.Decrement(ref counter.Value) == 0)
        {
            int head = JobPools.CompleteCounter(counterId, version);

            while (head > 0)
            {
                ref var node = ref JobPools.Nodes[head];
                int next = node.Next;

                GlobalJobQueue.Enqueue(node.Job);

                JobPools.FreeNode(head);
                head = next;
            }
        }
    }

    public static void Wait(JobHandle handle)
    {
        if (handle.IsCompleted)
            return;

        SpinWait spin = new SpinWait();
        while (!handle.IsCompleted)
        {
            // Help Execute while waiting to prevent deadlocks and improve throughput
            // If we can dequeue a job, run it.
            if (GlobalJobQueue.TryDequeue(out var job))
            {
                ExecuteJob(job);
            }
            else
            {
                spin.SpinOnce();
            }
        }
    }

    public static void Return(JobHandle handle)
    {
        Wait(handle);
        // Only free if version matches (still valid handle)
        ref var counter = ref JobPools.Counters[handle.CounterId];
        if (Volatile.Read(ref counter.Version) == handle.Version)
            JobPools.TryFreeCounter(handle.CounterId, handle.Version);
    }
}
