using System;
using System.Threading;

namespace SomeEngine.Core.Jobs.Internal;

internal static class JobPools
{
    // Configuration
    public const int MaxCounters = 16384;
    public const int MaxNodes = 16384;

    // Storage
    public static readonly JobCounter[] Counters = new JobCounter[MaxCounters];
    public static readonly JobDependencyNode[] Nodes = new JobDependencyNode[MaxNodes + 1]; // +1 for 1-based indexing

    // Free Lists
    private static readonly int[] _counterFreeStack = new int[MaxCounters];
    private static int _counterTop;
    private static SpinLock _counterLock;

    private static readonly int[] _nodeFreeStack = new int[MaxNodes];
    private static int _nodeTop;
    private static SpinLock _nodeLock;

    static JobPools()
    {
        _counterTop = MaxCounters;
        for (int i = 0; i < MaxCounters; i++) _counterFreeStack[i] = i; // 0-based for Counters

        _nodeTop = MaxNodes;
        for (int i = 0; i < MaxNodes; i++) _nodeFreeStack[i] = i + 1; // 1-based for Nodes
        
        _counterLock = new SpinLock();
        _nodeLock = new SpinLock();
    }

    public static int AllocCounter(int initialValue, out int version)
    {
        bool lockTaken = false;
        try
        {
            _counterLock.Enter(ref lockTaken);
            if (_counterTop > 0)
            {
                int index = _counterFreeStack[--_counterTop];
                ref var counter = ref Counters[index];

                version = unchecked(counter.Version + 1);
                if (version == 0)
                    version = 1;

                counter.Version = version;
                counter.Value = initialValue;
                counter.FirstDependent = 0;
                counter.Allocated = 1;

                return index;
            }
        }
        finally
        {
            if (lockTaken) _counterLock.Exit();
        }
        throw new InvalidOperationException("JobCounter pool exhausted!");
    }

    public static bool TryFreeCounter(int index, int expectedVersion)
    {
        bool lockTaken = false;
        try
        {
            _counterLock.Enter(ref lockTaken);
            ref var counter = ref Counters[index];

            if (counter.Version != expectedVersion || counter.Allocated == 0)
                return false;

            if (counter.Value != 0 || counter.FirstDependent != -1)
                return false;

            if (_counterTop >= MaxCounters)
                return false;

            counter.Value = 0;
            counter.FirstDependent = -1;
            counter.Allocated = 0;
            _counterFreeStack[_counterTop++] = index;
            return true;
        }
        finally
        {
            if (lockTaken) _counterLock.Exit();
        }
    }

    public static bool TryAddDependent(int counterId, int expectedVersion, JobId dependentJob)
    {
        bool lockTaken = false;
        try
        {
            _counterLock.Enter(ref lockTaken);
            ref var counter = ref Counters[counterId];

            if (counter.Version != expectedVersion || counter.Allocated == 0)
                return false;

            if (counter.Value == 0 || counter.FirstDependent == -1)
                return false;

            int nodeId = AllocNode();
            ref var node = ref Nodes[nodeId];
            node.Job = dependentJob;
            node.Next = counter.FirstDependent;
            counter.FirstDependent = nodeId;
            return true;
        }
        finally
        {
            if (lockTaken) _counterLock.Exit();
        }
    }

    public static int CompleteCounter(int index, int expectedVersion)
    {
        bool lockTaken = false;
        try
        {
            _counterLock.Enter(ref lockTaken);
            ref var counter = ref Counters[index];

            if (counter.Version != expectedVersion || counter.Allocated == 0)
                return 0;

            int head = counter.FirstDependent;
            counter.Value = 0;
            counter.FirstDependent = -1;
            counter.Allocated = 0;

            if (_counterTop < MaxCounters)
                _counterFreeStack[_counterTop++] = index;

            return head;
        }
        finally
        {
            if (lockTaken) _counterLock.Exit();
        }
    }

    public static int AllocNode()
    {
        bool lockTaken = false;
        try
        {
            _nodeLock.Enter(ref lockTaken);
            if (_nodeTop > 0)
            {
                return _nodeFreeStack[--_nodeTop];
            }
        }
        finally
        {
            if (lockTaken) _nodeLock.Exit();
        }
        throw new InvalidOperationException("JobDependencyNode pool exhausted!");
    }

    public static void FreeNode(int index)
    {
        bool lockTaken = false;
        try
        {
            _nodeLock.Enter(ref lockTaken);
            if (_nodeTop < MaxNodes)
            {
                _nodeFreeStack[_nodeTop++] = index;
            }
        }
        finally
        {
            if (lockTaken) _nodeLock.Exit();
        }
    }
}
