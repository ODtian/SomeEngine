using System;

namespace SomeEngine.Core.Jobs.Internal;

internal static class JobRegistry
{
    private static Action<int>[] _executors = new Action<int>[1024];
    private static int _count = 0;

    public static ushort Register(Action<int> executor)
    {
        // Simple sequential registration
        // Not thread safe registration (assume static init)
        // Or lock it
        lock (_executors)
        {
            ushort id = (ushort)_count++;
            if (id >= _executors.Length)
            {
                Array.Resize(ref _executors, _executors.Length * 2);
            }
            _executors[id] = executor;
            return id;
        }
    }

    public static Action<int> GetExecutor(ushort typeId)
    {
        return _executors[typeId];
    }
}
