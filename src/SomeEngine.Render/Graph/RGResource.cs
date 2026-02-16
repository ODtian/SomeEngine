using Diligent;

namespace SomeEngine.Render.Graph;

public struct RGResourceHandle
{
    public int Id;
    public int Version; // For handling transient resources over frames if needed, or just unique ID
    
    public static readonly RGResourceHandle Invalid = new RGResourceHandle { Id = -1 };
    
    public bool IsValid => Id != -1;
}

public abstract class RGResource
{
    public string Name { get; private set; }
    public RGResourceHandle Handle { get; internal set; }
    public bool IsImported { get; internal set; } // Imported external resource (e.g. BackBuffer)
    
    // State tracking
    public ResourceState InitialState { get; internal set; } = ResourceState.Undefined;
    public ResourceState CurrentState { get; internal set; } = ResourceState.Undefined;

    protected RGResource(string name)
    {
        Name = name;
    }
}

public class RGTexture : RGResource
{
    public TextureDesc Desc;
    public ITexture? InternalTexture; // The actual RHI texture
    public ITextureView? InternalView; // Optional: Imported view (e.g. BackBuffer RTV)

    public RGTexture(string name, TextureDesc desc) : base(name)
    {
        Desc = desc;
    }
}

public class RGBuffer : RGResource
{
    public BufferDesc Desc;
    public IBuffer? InternalBuffer;
    
    public RGBuffer(string name, BufferDesc desc) : base(name)
    {
        Desc = desc;
    }
}
