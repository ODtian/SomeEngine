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

    public static int AllocCounter()
    {
        bool lockTaken = false;
        try
        {
            _counterLock.Enter(ref lockTaken);
            if (_counterTop > 0)
            {
                return _counterFreeStack[--_counterTop];
            }
        }
        finally
        {
            if (lockTaken) _counterLock.Exit();
        }
        throw new InvalidOperationException("JobCounter pool exhausted!");
    }

    public static void FreeCounter(int index)
    {
        bool lockTaken = false;
        try
        {
            _counterLock.Enter(ref lockTaken);
            if (_counterTop < MaxCounters)
            {
                _counterFreeStack[_counterTop++] = index;
            }
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
