using SomeEngine.Core.Collections;

namespace SomeEngine.Rhi;

internal static class ActiveHandles
{
    public static void Retain<THandle>(FlatDictionary<THandle, int> active, THandle handle)
        where THandle : notnull
    {
        lock (active)
        {
            active.TryGetValue(handle, out int count);
            active[handle] = checked(count + 1);
        }
    }

    public static void Retain<THandle>(System.Threading.Lock gate, FlatDictionary<THandle, int> active, THandle handle)
        where THandle : notnull
    {
        lock (gate)
        {
            active.TryGetValue(handle, out int count);
            active[handle] = checked(count + 1);
        }
    }

    public static void Retain<THandle>(System.Threading.Lock gate, FlatDictionary<THandle, int> active, ReadOnlySpan<THandle> handles)
        where THandle : notnull
    {
        if (handles.IsEmpty)
            return;

        lock (gate)
        {
            foreach (var handle in handles)
            {
                active.TryGetValue(handle, out int count);
                active[handle] = checked(count + 1);
            }
        }
    }

    public static void Release<THandle>(
        FlatDictionary<THandle, int> active,
        IEnumerable<THandle> handles,
        string backend,
        string label)
        where THandle : notnull
    {
        lock (active)
        {
            foreach (var handle in handles)
            {
                if (!active.TryGetValue(handle, out int count))
                    throw new RhiException(
                        ErrorCode.ValidationFailure,
                        $"Active {backend} {label} tracking is corrupt.");

                if (count == 1)
                    active.Remove(handle);
                else
                    active[handle] = count - 1;
            }

            if (active.Count == 0)
                active.ClearNoResize();
        }
    }

    public static void Release<THandle>(
        System.Threading.Lock gate,
        FlatDictionary<THandle, int> active,
        IEnumerable<THandle> handles,
        string backend,
        string label)
        where THandle : notnull
    {
        lock (gate)
        {
            foreach (var handle in handles)
            {
                if (!active.TryGetValue(handle, out int count))
                    throw new RhiException(
                        ErrorCode.ValidationFailure,
                        $"Active {backend} {label} tracking is corrupt.");

                if (count == 1)
                    active.Remove(handle);
                else
                    active[handle] = count - 1;
            }

            if (active.Count == 0)
                active.ClearNoResize();
        }
    }

    public static void Release<THandle>(
        System.Threading.Lock gate,
        FlatDictionary<THandle, int> active,
        ReadOnlySpan<THandle> handles,
        string backend,
        string label)
        where THandle : notnull
    {
        if (handles.IsEmpty)
            return;

        lock (gate)
        {
            for (int i = 0; i < handles.Length; i++)
            {
                var handle = handles[i];
                if (!active.TryGetValue(handle, out int count))
                    throw new RhiException(
                        ErrorCode.ValidationFailure,
                        $"Active {backend} {label} tracking is corrupt.");

                if (count == 1)
                    active.Remove(handle);
                else
                    active[handle] = count - 1;
            }

            if (active.Count == 0)
                active.ClearNoResize();
        }
    }

    public static bool Contains<THandle>(FlatDictionary<THandle, int> active, THandle handle)
        where THandle : notnull
    {
        lock (active)
        {
            return active.ContainsKey(handle);
        }
    }

    public static bool Contains<THandle>(System.Threading.Lock gate, FlatDictionary<THandle, int> active, THandle handle)
        where THandle : notnull
    {
        lock (gate)
        {
            return active.ContainsKey(handle);
        }
    }
}
