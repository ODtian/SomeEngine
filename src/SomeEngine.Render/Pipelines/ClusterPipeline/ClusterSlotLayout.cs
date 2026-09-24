namespace SomeEngine.Render.Pipelines;

internal readonly record struct ClusterSlotSpan(int Offset, int Count);

internal readonly record struct ClusterSlotLayout
{
    public ClusterSlotLayout(int fields, int capacity)
    {
        if (fields <= 0)
            throw new ArgumentOutOfRangeException(nameof(fields));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        Fields = fields;
        Capacity = Even(capacity);
    }

    public int Fields { get; }
    public int Capacity { get; }
    public int ElementCount => checked(Fields * Capacity);
    public int ByteCount => checked(ElementCount * sizeof(ushort));

    public static int Even(int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        return checked((count + 1) & ~1);
    }

    public int Field(int field)
    {
        ValidateField(field);
        return checked(field * Capacity);
    }

    public ClusterSlotLayout Grow(int required)
    {
        if (required <= 0)
            throw new ArgumentOutOfRangeException(nameof(required));

        return required <= Capacity
            ? this
            : new ClusterSlotLayout(Fields, required);
    }

    public int Index(int field, int slot)
    {
        ValidateSlot(slot);
        return checked(Field(field) + slot);
    }

    public ClusterSlotSpan Span(int field, int minSlot, int maxSlot)
    {
        ValidateField(field);
        ValidateSlot(minSlot);
        ValidateSlot(maxSlot);
        if (minSlot > maxSlot)
            throw new ArgumentOutOfRangeException(nameof(minSlot));

        int min = minSlot & ~1;
        int max = Math.Min(Capacity - 1, maxSlot | 1);
        return new ClusterSlotSpan(checked(Field(field) + min), max - min + 1);
    }

    private void ValidateField(int field)
    {
        if ((uint)field >= (uint)Fields)
            throw new ArgumentOutOfRangeException(nameof(field));
    }

    private void ValidateSlot(int slot)
    {
        if ((uint)slot >= (uint)Capacity)
            throw new ArgumentOutOfRangeException(nameof(slot));
    }
}
