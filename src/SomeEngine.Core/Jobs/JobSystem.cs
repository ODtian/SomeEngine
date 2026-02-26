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
        int counterId = JobPools.AllocCounter();
        ref var counter = ref JobPools.Counters[counterId];

        counter.Version++;
        counter.Value = 2;
        counter.FirstDependent = 0;

        // Use the dummy combine job type with index 0
        var jobId = new JobId(_combineJobTypeId, 0, counterId);

        ScheduleInternal(jobId, h1);
        ScheduleInternal(jobId, h2);

        return new JobHandle(counterId, counter.Version);
    }

    public static JobHandle CombineDependencies(ReadOnlySpan<JobHandle> handles)
    {
        if (handles.Length == 0)
            return default;

        int counterId = JobPools.AllocCounter();
        ref var counter = ref JobPools.Counters[counterId];

        counter.Version++;
        counter.Value = handles.Length;
        counter.FirstDependent = 0;

        var jobId = new JobId(_combineJobTypeId, 0, counterId);

        foreach (var h in handles)
        {
            ScheduleInternal(jobId, h);
        }

        return new JobHandle(counterId, counter.Version);
    }

    public static JobHandle Schedule<T>(T job, JobHandle dependency = default)
        where T : struct, IJob
    {
        // 1. Allocate and Init Counter
        int counterId = JobPools.AllocCounter();
        ref var counter = ref JobPools.Counters[counterId];

        counter.Version++;
        counter.Value = 1;
        counter.FirstDependent = 0; // null

        // 2. Store Job Data
        int jobIndex = JobDataStore<T>.Add(job);

        // 3. Create JobId
        var myJobId = new JobId(JobDataStore<T>.TypeId, jobIndex, counterId);

        // 4. Handle Dependency
        ScheduleInternal(myJobId, dependency);

        return new JobHandle(counterId, counter.Version);
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
        int counterId = JobPools.AllocCounter();
        ref var counter = ref JobPools.Counters[counterId];

        counter.Version++;
        counter.Value = batchCount; // Wait for ALL batches
        counter.FirstDependent = 0;

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

        return new JobHandle(counterId, counter.Version);
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
        if (counter.Version != handle.Version)
            return true;

        return counter.Value == 0;
    }

    private static bool TryAddDependency(int counterId, int expectedVersion, JobId dependentJob)
    {
        ref var counter = ref JobPools.Counters[counterId];

        SpinWait spin = new SpinWait();
        while (true)
        {
            if (counter.Version != expectedVersion)
                return false;
            if (counter.Value == 0)
                return false;

            int nodeId = JobPools.AllocNode();
            ref var node = ref JobPools.Nodes[nodeId];
            node.Job = dependentJob;

            int currentHead = counter.FirstDependent;
            if (currentHead == -1)
            {
                JobPools.FreeNode(nodeId);
                return false;
            }

            node.Next = currentHead;

            if (
                Interlocked.CompareExchange(ref counter.FirstDependent, nodeId, currentHead)
                == currentHead
            )
            {
                return true;
            }

            JobPools.FreeNode(nodeId);
            spin.SpinOnce();
        }
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

        if (Interlocked.Decrement(ref counter.Value) == 0)
        {
            int head = Interlocked.Exchange(ref counter.FirstDependent, -1);

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
        if (counter.Version == handle.Version)
        {
            JobPools.FreeCounter(handle.CounterId);
        }
    }
}
