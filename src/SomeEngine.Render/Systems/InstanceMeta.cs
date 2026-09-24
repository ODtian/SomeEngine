using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Core.ECS.Components;

namespace SomeEngine.Render.Systems;

internal sealed class InstanceMeta
{
    private byte[] _data = [];

    public static int SlotSize => Unsafe.SizeOf<MaterialOverride>();
    public int ByteCount { get; private set; }
    public ReadOnlySpan<byte> Data => _data.AsSpan(0, ByteCount);

    public void Begin(int count, bool hasMeta)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        ByteCount = hasMeta ? checked(count * SlotSize) : 0;
        Ensure(ByteCount);
    }

    public uint Offset(int index)
        => checked((uint)(index * SlotSize));

    public ReadOnlySpan<byte> Slot(int index)
        => _data.AsSpan(checked(index * SlotSize), SlotSize);

    public uint Write(int index, in MaterialOverride value)
    {
        int offset = checked(index * SlotSize);
        if (offset + SlotSize > _data.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        MemoryMarshal.Write(_data.AsSpan(offset), in value);
        return checked((uint)offset);
    }

    private void Ensure(int bytes)
    {
        if (_data.Length < bytes)
            Array.Resize(ref _data, bytes);
    }
}
