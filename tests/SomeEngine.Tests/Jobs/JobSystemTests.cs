using SomeJob;
using System.Threading;

namespace SomeEngine.Tests.Jobs;

public class JobSystemTests
{
    struct SimpleJob : IJob
    {
        public static int ExecutedCount;

        public void Execute()
        {
            Interlocked.Increment(ref ExecutedCount);
        }
    }

    private const int CounterPoolPressureCount = 17000;

    [Fact]
    public void TestSimpleSchedule()
    {
        SimpleJob.ExecutedCount = 0;
        var job = new SimpleJob();
        var handle = JobSystem.Schedule(job);

        handle.Complete();

        Assert.Equal(1, SimpleJob.ExecutedCount);
    }

    struct DependencyJob : IJob
    {
        public static int Step;
        public bool IsSecond;

        public void Execute()
        {
            // If IsSecond is true, Step must be 1.
            if (IsSecond)
            {
                if (Step == 1) Step = 2;
            }
            else
            {
                Thread.Sleep(10); // Ensure delay to test dependency
                Step = 1;
            }
        }
    }

    [Fact]
    public void TestDependency()
    {
        DependencyJob.Step = 0;

        var job1 = new DependencyJob { IsSecond = false };
        var job2 = new DependencyJob { IsSecond = true };

        var handle1 = JobSystem.Schedule(job1);
        var handle2 = JobSystem.Schedule(job2, handle1);

        handle2.Complete();
        handle1.Complete();

        Assert.Equal(2, DependencyJob.Step);
    }

    struct ParallelJob : IJobParallelFor
    {
        public static int[] Data = Array.Empty<int>();

        public void Execute(int index)
        {
            Data[index] = index * 2;
        }
    }

    [Fact]
    public void TestDispatch()
    {
        int count = 100;
        ParallelJob.Data = new int[count];

        var job = new ParallelJob();
        var handle = JobSystem.ScheduleParallel(job, count, 10);

        handle.Complete();

        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i * 2, ParallelJob.Data[i]);
        }
    }

    struct RecursiveJob : IJob
    {
        public int Depth;
        public static int TotalExecuted;

        public void Execute()
        {
            Interlocked.Increment(ref TotalExecuted);
            if (Depth > 0)
            {
                var child = new RecursiveJob { Depth = Depth - 1 };
                var handle = JobSystem.Schedule(child);
                handle.Complete();
            }
        }
    }

    [Fact]
    public void TestRecursiveWait()
    {
        RecursiveJob.TotalExecuted = 0;
        int depth = 10;

        var rootJob = new RecursiveJob { Depth = depth };
        var handle = JobSystem.Schedule(rootJob);
        handle.Complete();

        // Depth 10 -> 10, 9, 8... 0 = 11 jobs
        Assert.Equal(depth + 1, RecursiveJob.TotalExecuted);
    }

    [Fact]
    public void CompleteDoesNotExhaustCounterPool()
    {
        SimpleJob.ExecutedCount = 0;

        for (int i = 0; i < CounterPoolPressureCount; i++)
        {
            JobSystem.Schedule(new SimpleJob()).Complete();
        }

        Assert.Equal(CounterPoolPressureCount, SimpleJob.ExecutedCount);
    }

    [Fact]
    public void CompletingFinalDependencyReleasesParentCounters()
    {
        SimpleJob.ExecutedCount = 0;

        for (int i = 0; i < CounterPoolPressureCount; i++)
        {
            var first = JobSystem.Schedule(new SimpleJob());
            var second = JobSystem.Schedule(new SimpleJob(), first);

            second.Complete();
        }

        Assert.Equal(CounterPoolPressureCount * 2, SimpleJob.ExecutedCount);
    }
}
