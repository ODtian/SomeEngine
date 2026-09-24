namespace SomeEngine.Render.Assets;

public sealed class Mesh
{
    public Mesh(string name, ReadOnlyMemory<byte> payload, ulong bvhOffset)
    {
        Name = string.IsNullOrWhiteSpace(name) ? nameof(Mesh) : name;
        Payload = payload.IsEmpty ? ReadOnlyMemory<byte>.Empty : payload.ToArray();
        BvhOffset = bvhOffset;
    }

    public string Name { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    public ulong BvhOffset { get; }
}
