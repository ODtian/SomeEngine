using System;
using System.Threading;
using SomeEngine.Core.Jobs.Internal;

namespace SomeEngine.Core.Jobs;

public static class JobDataStore<T> where T : struct, IJob
{
    public static readonly ushort TypeId;
    
    // Storage
    private static readonly T[] _jobs = new T[JobPools.MaxCounters]; // Reuse max constants
    private static readonly int[] _freeStack = new int[JobPools.MaxCounters];
    private static int _freeTop;
    private static SpinLock _lock;

    static JobDataStore()
    {
        _freeTop = JobPools.MaxCounters;
        for (int i = 0; i < JobPools.MaxCounters; i++) _freeStack[i] = i; // 0-based indices for data store
        
        _lock = new SpinLock();
        
        // Register execution delegate
        TypeId = JobRegistry.Register(Execute);
    }

    private static void Execute(int index)
    {
        _jobs[index].Execute();
        Free(index);
    }

    public static int Add(T job)
    {
        bool lockTaken = false;
        try
        {
            _lock.Enter(ref lockTaken);
            if (_freeTop > 0)
            {
                int index = _freeStack[--_freeTop];
                _jobs[index] = job;
                return index;
            }
        }
        finally
        {
            if (lockTaken) _lock.Exit();
        }
        throw new InvalidOperationException($"JobDataStore<{typeof(T).Name}> full!");
    }

    private static void Free(int index)
    {
        bool lockTaken = false;
        try
        {
            _lock.Enter(ref lockTaken);
            if (_freeTop < _freeStack.Length)
            {
                _freeStack[_freeTop++] = index;
            }
        }
        finally
        {
            if (lockTaken) _lock.Exit();
        }
    }
}
