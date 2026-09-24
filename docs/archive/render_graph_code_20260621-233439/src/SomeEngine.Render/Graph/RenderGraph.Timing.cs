using System.Runtime.InteropServices;
using SomeEngine.Rhi;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Render.Graph;

public sealed partial class RenderGraph
{
    private DeviceTimestamps? RentTimestamps(IDevice device, int passCount, bool finalWork)
    {
        if (!DeviceTimestamps.CanUse(device, Profiler.NeedsDeviceTime))
            return null;

        uint capacity = DeviceTimestamps.Required(passCount, finalWork);
        if (capacity == 0)
            return null;

        for (int i = _executor.TimestampPool.Count - 1; i >= 0; i--)
        {
            DeviceTimestamps timestamps = _executor.TimestampPool[i];
            if (!timestamps.Matches(device, capacity))
                continue;

            _executor.TimestampPool.RemoveAt(i);
            timestamps.Reset();
            return timestamps;
        }

        return DeviceTimestamps.Create(device, capacity);
    }

    private void ReturnTimestamps(DeviceTimestamps? timestamps)
    {
        if (timestamps == null)
            return;

        if (_lastDevice == null || !timestamps.Belongs(_lastDevice))
        {
            timestamps.Destroy();
            return;
        }

        timestamps.Reset();
        _executor.TimestampPool.Add(timestamps);
    }

    private void DestroyTimestamps()
    {
        for (int i = _executor.TimestampPool.Count - 1; i >= 0; i--)
            _executor.TimestampPool[i].Destroy();
        _executor.TimestampPool.Clear();
    }

    internal sealed class DeviceTimestamps
    {
        private readonly IDevice _device;
        private readonly float _period;
        private readonly uint _capacity;
        private readonly List<TimestampSample> _samples = [];
        private uint _cursor;

        private DeviceTimestamps(IDevice device, QueryPoolHandle queries, BufferHandle readback, uint capacity)
        {
            _device = device;
            Queries = queries;
            Readback = readback;
            _capacity = capacity;
            _period = device.Limits.TimestampPeriodNanoseconds;
        }

        public QueryPoolHandle Queries { get; }
        public BufferHandle Readback { get; }
        public uint Cursor => _cursor;
        public bool HasSamples => _samples.Count != 0;

        public static bool CanUse(IDevice device, bool needsDeviceTime)
        {
            if (!needsDeviceTime || !device.Features.TimestampQueries)
                return false;

            IQueryDevice? queries = device.Get<IQueryDevice>();
            return queries != null;
        }

        public static uint Required(int passCount, bool finalWork)
        {
            int sampleCount = checked((passCount + (finalWork ? 1 : 0)) * 2);
            if (sampleCount <= 0)
                return 0;

            return checked((uint)sampleCount);
        }

        public static DeviceTimestamps Create(IDevice device, uint capacity)
        {
            IQueryDevice queries = device.Get<IQueryDevice>()
                ?? throw new InvalidOperationException("RenderGraph timestamp profiling requires a query device.");
            QueryPoolHandle pool = queries.CreateQueryPool(new QueryPoolDesc
            {
                Name = "RenderGraph Device Timestamps",
                Type = QueryType.Timestamp,
                Count = capacity,
            });
            BufferHandle readback = device.CreateBuffer(new BufferDesc
            {
                Name = "RenderGraph Device Timestamp Readback",
                SizeInBytes = checked((ulong)capacity * sizeof(ulong)),
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.QueryResolve,
            });
            return new DeviceTimestamps(device, pool, readback, capacity);
        }

        public bool Matches(IDevice device, uint capacity)
            => ReferenceEquals(_device, device) && _capacity >= capacity;

        public bool Belongs(IDevice device)
            => ReferenceEquals(_device, device);

        public void Reset()
        {
            _samples.Clear();
            _cursor = 0;
        }

        public bool Supports(QueueType queue)
        {
            for (int i = 0; i < _device.QueueFamilies.Count; i++)
            {
                QueueFamilyInfo family = _device.QueueFamilies[i];
                if (family.Type == queue)
                    return family.SupportsTimestamps;
            }

            return false;
        }

        public TimestampSpan Start(
            ICommandList list,
            QueueType queue,
            string name,
            int passIndex,
            string phase)
        {
            if (!Supports(queue) || _cursor + 1 >= _capacity)
                return default;

            uint start = _cursor++;
            uint end = _cursor++;
            list.WriteTimestamp(Queries, start);
            _samples.Add(new TimestampSample(name, passIndex, phase, start, end));
            return new TimestampSpan(end);
        }

        public void Stop(ICommandList list, TimestampSpan span)
        {
            if (span.IsValid)
                list.WriteTimestamp(Queries, span.End);
        }

        public void Resolve(ICommandList list, QueueType queue, uint first)
        {
            if (!Supports(queue) || first >= _cursor)
                return;

            uint count = _cursor - first;
            list.ResolveQueryData(Queries, first, count, Readback, checked((ulong)first * sizeof(ulong)));
        }

        public void Flush()
        {
            if (!HasSamples)
                return;

            int byteCount = checked((int)((ulong)_cursor * sizeof(ulong)));
            Memory<byte> mapped = _device.MapBuffer(Readback, MapMode.Read, 0, byteCount);
            try
            {
                Span<ulong> values = MemoryMarshal.Cast<byte, ulong>(mapped.Span);
                for (int i = 0; i < _samples.Count; i++)
                {
                    TimestampSample sample = _samples[i];
                    if (sample.Start >= values.Length || sample.End >= values.Length)
                        continue;

                    ulong start = values[(int)sample.Start];
                    ulong end = values[(int)sample.End];
                    if (end <= start)
                        continue;

                    ulong ticks = end - start;
                    long nanoseconds = checked((long)Math.Round(ticks * _period));
                    Profiler.DeviceTime(sample.Name, sample.PassIndex, sample.Phase, nanoseconds);
                }
            }
            finally
            {
                _device.UnmapBuffer(Readback);
            }
        }

        public void Destroy()
        {
            if (Readback.IsValid)
                _device.Destroy(Readback);
            if (Queries.IsValid)
                _device.Destroy(Queries);
        }
    }

    internal readonly record struct TimestampSpan(uint End)
    {
        public bool IsValid => End != 0;
    }

    private readonly record struct TimestampSample(
        string Name,
        int PassIndex,
        string Phase,
        uint Start,
        uint End);
}
