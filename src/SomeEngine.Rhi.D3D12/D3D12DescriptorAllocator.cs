using Vortice.Direct3D12;

namespace SomeEngine.Rhi.D3D12;

internal sealed class CpuDescriptorPool : IDisposable
{
    private readonly ID3D12DescriptorHeap _heap;
    private readonly CpuDescriptorHandle _cpuStart;
    private readonly uint _increment;
    private readonly int _capacity;
    private readonly Stack<int> _free = [];
    private readonly object _gate = new();
    private int _next;

    public CpuDescriptorPool(ID3D12Device device, DescriptorHeapType type, int capacity)
    {
        var desc = new DescriptorHeapDescription(type, checked((uint)capacity), DescriptorHeapFlags.None, 0);
        _heap = device.CreateDescriptorHeap(desc);
        _cpuStart = _heap.GetCPUDescriptorHandleForHeapStart();
        _increment = device.GetDescriptorHandleIncrementSize(type);
        _capacity = capacity;
    }

    public DescriptorAllocation Allocate()
    {
        lock (_gate)
        {
            int index;
            if (_free.Count > 0)
            {
                index = _free.Pop();
            }
            else
            {
                if (_next >= _capacity)
                    throw new RhiException(ErrorCode.BackendFailure, $"CPU descriptor heap capacity {_capacity} was exceeded.");
                index = _next++;
            }

            var handle = new CpuDescriptorHandle(_cpuStart, index, _increment);
            return new DescriptorAllocation(handle, index);
        }
    }

    public void Free(DescriptorAllocation allocation)
    {
        if (allocation.Index < 0)
            return;

        lock (_gate)
        {
            _free.Push(allocation.Index);
        }
    }

    public void Dispose() => _heap.Dispose();
}

internal sealed class ShaderDescriptorPool : IDisposable
{
    private readonly ID3D12DescriptorHeap _heap;
    private readonly CpuDescriptorHandle _cpuStart;
    private readonly GpuDescriptorHandle _gpuStart;
    private readonly uint _increment;
    private readonly int _capacity;
    private readonly List<DescriptorBlock> _free = [];
    private readonly object _gate = new();
    private int _next;

    public ShaderDescriptorPool(ID3D12Device device, DescriptorHeapType type, int capacity)
    {
        var desc = new DescriptorHeapDescription(type, checked((uint)capacity), DescriptorHeapFlags.ShaderVisible, 0);
        _heap = device.CreateDescriptorHeap(desc);
        _cpuStart = _heap.GetCPUDescriptorHandleForHeapStart();
        _gpuStart = _heap.GetGPUDescriptorHandleForHeapStart();
        _increment = device.GetDescriptorHandleIncrementSize(type);
        _capacity = capacity;
    }

    public ID3D12DescriptorHeap Heap => _heap;

    public ShaderDescriptorAllocation Allocate(int count)
    {
        if (count <= 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Descriptor allocation count must be greater than zero.");
        int index;
        lock (_gate)
        {
            index = AllocateBlock(count);
        }

        return new ShaderDescriptorAllocation(
            new CpuDescriptorHandle(_cpuStart, index, _increment),
            new GpuDescriptorHandle(_gpuStart, index, _increment),
            index,
            count);
    }

    public ShaderDescriptorAllocation AllocateTransient(int count)
    {
        if (count <= 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Descriptor allocation count must be greater than zero.");
        int index;
        lock (_gate)
        {
            index = count <= _capacity - _next
                ? AllocateTail(count)
                : AllocateBlock(count);
        }

        return new ShaderDescriptorAllocation(
            new CpuDescriptorHandle(_cpuStart, index, _increment),
            new GpuDescriptorHandle(_gpuStart, index, _increment),
            index,
            count);
    }

    public CpuDescriptorHandle CpuAt(ShaderDescriptorAllocation allocation, int offset)
        => new(allocation.Cpu, offset, _increment);

    public void Free(ShaderDescriptorAllocation allocation)
    {
        if (allocation.Index < 0 || allocation.Count <= 0)
            return;
        lock (_gate)
        {
            var block = new DescriptorBlock(allocation.Index, allocation.Count);
            int insert = 0;
            while (insert < _free.Count && _free[insert].Index < block.Index)
                insert++;

            if (insert > 0)
            {
                var previous = _free[insert - 1];
                int previousEnd = checked(previous.Index + previous.Count);
                if (previousEnd > block.Index)
                    throw new RhiException(ErrorCode.ValidationFailure, "Shader descriptor allocator received an overlapping free block.");
                if (previousEnd == block.Index)
                {
                    block = new DescriptorBlock(previous.Index, checked(previous.Count + block.Count));
                    _free.RemoveAt(--insert);
                }
            }

            if (insert < _free.Count)
            {
                var next = _free[insert];
                int blockEnd = checked(block.Index + block.Count);
                if (blockEnd > next.Index)
                    throw new RhiException(ErrorCode.ValidationFailure, "Shader descriptor allocator received an overlapping free block.");
                if (blockEnd == next.Index)
                {
                    block = new DescriptorBlock(block.Index, checked(block.Count + next.Count));
                    _free.RemoveAt(insert);
                }
            }

            _free.Insert(insert, block);
        }
    }

    public void Dispose() => _heap.Dispose();

    private int AllocateBlock(int count)
    {
        for (int index = 0; index < _free.Count; index++)
        {
            var block = _free[index];
            if (block.Count < count)
                continue;
            int result = block.Index;
            if (block.Count == count)
            {
                _free.RemoveAt(index);
            }
            else
            {
                _free[index] = new DescriptorBlock(block.Index + count, block.Count - count);
            }

            return result;
        }

        if (count > _capacity - _next)
            throw new RhiException(ErrorCode.BackendFailure, $"Shader descriptor heap capacity {_capacity} was exceeded.");
        return AllocateTail(count);
    }

    private int AllocateTail(int count)
    {
        int next = _next;
        _next = checked(_next + count);
        return next;
    }
}

internal readonly record struct DescriptorBlock(int Index, int Count);
