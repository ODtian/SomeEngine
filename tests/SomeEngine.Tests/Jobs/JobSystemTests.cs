using NUnit.Framework;
using SomeEngine.Core.Jobs;
using System.Threading;

namespace SomeEngine.Tests.Jobs;

[TestFixture]
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

    [Test]
    public void TestSimpleSchedule()
    {
        SimpleJob.ExecutedCount = 0;
        var job = new SimpleJob();
        var handle = JobSystem.Schedule(job);
        
        JobSystem.Return(handle); 
        
        Assert.That(SimpleJob.ExecutedCount, Is.EqualTo(1));
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

    [Test]
    public void TestDependency()
    {
        DependencyJob.Step = 0;
        
        var job1 = new DependencyJob { IsSecond = false };
        var job2 = new DependencyJob { IsSecond = true };
        
        var handle1 = JobSystem.Schedule(job1);
        var handle2 = JobSystem.Schedule(job2, handle1);
        
        JobSystem.Return(handle2);
        JobSystem.Return(handle1);
        
        Assert.That(DependencyJob.Step, Is.EqualTo(2));
    }

    struct ParallelJob : IJobParallelFor
    {
        public static int[] Data = Array.Empty<int>();

        public void Execute(int index)
        {
            Data[index] = index * 2;
        }
    }

    [Test]
    public void TestDispatch()
    {
        int count = 100;
        ParallelJob.Data = new int[count];

        var job = new ParallelJob();
        var handle = JobSystem.Dispatch(job, count, 10);
        
        JobSystem.Return(handle);

        for (int i = 0; i < count; i++)
        {
            Assert.That(ParallelJob.Data[i], Is.EqualTo(i * 2));
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
                JobSystem.Return(handle); // This will block/wait recursively
            }
        }
    }

    [Test]
    public void TestRecursiveWait()
    {
        RecursiveJob.TotalExecuted = 0;
        int depth = 10;
        
        var rootJob = new RecursiveJob { Depth = depth };
        var handle = JobSystem.Schedule(rootJob);
        JobSystem.Return(handle);
        
        // Depth 10 -> 10, 9, 8... 0 = 11 jobs
        Assert.That(RecursiveJob.TotalExecuted, Is.EqualTo(depth + 1));
    }
}
