using Diligent;

namespace SomeEngine.Render.Graph;

public struct RGResourceHandle
{
    public int Id;
    public int Version; // For handling transient resources over frames if needed, or just unique ID

    public static readonly RGResourceHandle Invalid = new RGResourceHandle { Id = -1 };

    public readonly bool IsValid => Id != -1;
}

public abstract class RGResource(string name)
{
    public string Name { get; private set; } = name;
    public RGResourceHandle Handle { get; internal set; }
    public bool IsImported { get; internal set; } // Imported external resource (e.g. BackBuffer)

    // State tracking
    public ResourceState InitialState { get; internal set; } = ResourceState.Undefined;
    public ResourceState CurrentState { get; internal set; } = ResourceState.Undefined;

    // Placed resource memory info (set during Compile)
    internal ulong MemorySize { get; set; }
    internal ulong MemoryAlignment { get; set; }
    internal ulong MemoryOffset { get; set; } = ulong.MaxValue;
    internal int HeapIndex { get; set; } = -1;
}

public class RGTexture(string name, TextureDesc desc) : RGResource(name)
{
    public TextureDesc Desc = desc;
    public ITexture? InternalTexture; // The actual RHI texture
    public ITextureView? InternalView; // Optional: Imported view (e.g. BackBuffer RTV)
}

public class RGBuffer(string name, BufferDesc desc) : RGResource(name)
{
    public BufferDesc Desc = desc;
    public IBuffer? InternalBuffer;
}
