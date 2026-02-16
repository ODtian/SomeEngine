using System.Threading;

namespace SomeEngine.Core.Jobs.Internal;

internal static class GlobalJobQueue
{
    private const int Capacity = 32768; // Power of 2
    private const int Mask = Capacity - 1;

    private static readonly JobId[] _array = new JobId[Capacity];
    private static int _head; // Write
    private static int _tail; // Read
    private static SpinLock _lock;
    private static readonly ManualResetEventSlim _signal = new(false);

    static GlobalJobQueue()
    {
        _lock = new SpinLock();
    }

    public static void Enqueue(JobId job)
    {
        bool lockTaken = false;
        try
        {
            _lock.Enter(ref lockTaken);
            
            // Check full
            int nextHead = (_head + 1) & Mask;
            if (nextHead == _tail)
            {
                // Full - Drop? Throw? Spin?
                // For now throw/ignore, but in real engine we need to grow or wait.
                // Assuming capacity is enough.
                return; 
            }

            _array[_head] = job;
            _head = nextHead;
            _signal.Set();
        }
        finally
        {
            if (lockTaken) _lock.Exit();
        }
    }

    public static bool TryDequeue(out JobId job)
    {
        bool lockTaken = false;
        try
        {
            _lock.Enter(ref lockTaken);
            
            if (_head == _tail)
            {
                job = default;
                _signal.Reset();
                return false;
            }

            job = _array[_tail];
            _tail = (_tail + 1) & Mask;
            return true;
        }
        finally
        {
            if (lockTaken) _lock.Exit();
        }
    }

    public static void WaitForJob()
    {
        _signal.Wait();
    }
}
